﻿// DiscoveryProxy.cs  
//----------------------------------------------------------------  
// Copyright (c) Microsoft Corporation.  All rights reserved.  
//----------------------------------------------------------------  

using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using System.ServiceModel.Discovery;
using System.Xml;
using System.Xml.Linq;

namespace WCFDiscoveryProxy
{
    // Implement DiscoveryProxy by extending the DiscoveryProxy class and overriding the abstract methods  
    [ServiceBehavior(InstanceContextMode = InstanceContextMode.Single, ConcurrencyMode = ConcurrencyMode.Multiple)]
    public class DiscoveryProxyService : DiscoveryProxy
    {
        // Repository to store EndpointDiscoveryMetadata. A database or a flat file could also be used instead.  
        Dictionary<EndpointAddress, EndpointDiscoveryMetadata> onlineServices;
        private Dictionary<string, int> ServicesCount { get; set; }

        public DiscoveryProxyService()
        {
            this.onlineServices = new Dictionary<EndpointAddress, EndpointDiscoveryMetadata>();
            this.ServicesCount = new Dictionary<string, int>();
        }

        // OnBeginOnlineAnnouncement method is called when a Hello message is received by the Proxy  
        protected override IAsyncResult OnBeginOnlineAnnouncement(DiscoveryMessageSequence messageSequence, EndpointDiscoveryMetadata endpointDiscoveryMetadata, AsyncCallback callback, object state)
        {
            this.AddOnlineService(endpointDiscoveryMetadata);
            return new OnOnlineAnnouncementAsyncResult(callback, state);
        }

        protected override void OnEndOnlineAnnouncement(IAsyncResult result)
        {
            OnOnlineAnnouncementAsyncResult.End(result);
        }

        // OnBeginOfflineAnnouncement method is called when a Bye message is received by the Proxy  
        protected override IAsyncResult OnBeginOfflineAnnouncement(DiscoveryMessageSequence messageSequence, EndpointDiscoveryMetadata endpointDiscoveryMetadata, AsyncCallback callback, object state)
        {
            this.RemoveOnlineService(endpointDiscoveryMetadata);
            return new OnOfflineAnnouncementAsyncResult(callback, state);
        }

        protected override void OnEndOfflineAnnouncement(IAsyncResult result)
        {
            OnOfflineAnnouncementAsyncResult.End(result);
        }

        // OnBeginFind method is called when a Probe request message is received by the Proxy  
        protected override IAsyncResult OnBeginFind(FindRequestContext findRequestContext, AsyncCallback callback, object state)
        {
            this.MatchFromOnlineService(findRequestContext);
            return new OnFindAsyncResult(callback, state);
        }

        protected override void OnEndFind(IAsyncResult result)
        {
            OnFindAsyncResult.End(result);
        }

        // OnBeginFind method is called when a Resolve request message is received by the Proxy  
        protected override IAsyncResult OnBeginResolve(ResolveCriteria resolveCriteria, AsyncCallback callback, object state)
        {
            return new OnResolveAsyncResult(this.MatchFromOnlineService(resolveCriteria), callback, state);
        }

        protected override EndpointDiscoveryMetadata OnEndResolve(IAsyncResult result)
        {
            return OnResolveAsyncResult.End(result);
        }

        // The following are helper methods required by the Proxy implementation  
        void AddOnlineService(EndpointDiscoveryMetadata endpointDiscoveryMetadata)
        {
            EndpointAddress address;
            // Check to see if the endpoint has a listenUri and if it differs from the Address URI
            if (endpointDiscoveryMetadata.ListenUris.Count == 0 || 
                endpointDiscoveryMetadata.Address.Uri == endpointDiscoveryMetadata.ListenUris[0])
            {
                address = endpointDiscoveryMetadata.Address;
            }
            else
            {
                address = new EndpointAddress(endpointDiscoveryMetadata.ListenUris[0]);
            }

            string serviceName = address.ToString().Split('/').Last();
            int count = -1;

            foreach (XElement customMetadata in endpointDiscoveryMetadata.Extensions)
            {
                if (customMetadata.Name != "Parent") continue;
                
                lock (this.ServicesCount)
                {
                    this.ServicesCount.TryGetValue(serviceName, out count); //count=0 if serviceName doesn't exist
                    this.ServicesCount[serviceName] = count + 1;
                }
                break;
            }

            lock (this.onlineServices)
            {
                XElement xName;
                if (count == -1)
                {
                    xName = new XElement("Name", serviceName);

                    EndpointDiscoveryMetadata oldService = this.onlineServices.Values.FirstOrDefault
                        (x => x.Extensions.Any(y => y.Name.LocalName=="Name" && y.Value==serviceName));
                    if (oldService != null)
                    {
                        RemoveOnlineService(oldService);
                    }
                }
                else
                {
                    xName = new XElement("Name", serviceName + count);
                }

                endpointDiscoveryMetadata.Extensions.Add(xName);
                this.onlineServices[address] = endpointDiscoveryMetadata;
            }

            PrintDiscoveryMetadata(endpointDiscoveryMetadata, "Adding");
        }

        void RemoveOnlineService(EndpointDiscoveryMetadata endpointDiscoveryMetadata)
        {
            if (endpointDiscoveryMetadata == null) return;
            EndpointAddress address;
            // Check to see if the endpoint has a listenUri and if it differs from the Address URI
            if (endpointDiscoveryMetadata.ListenUris.Count == 0 ||
                endpointDiscoveryMetadata.Address.Uri == endpointDiscoveryMetadata.ListenUris[0])
            {
                address = endpointDiscoveryMetadata.Address;
            }
            else
            {
                address = new EndpointAddress(endpointDiscoveryMetadata.ListenUris[0]);
            }

            lock (this.onlineServices)
            {
                this.onlineServices.Remove(address);
            }

            PrintDiscoveryMetadata(endpointDiscoveryMetadata, "Removing");
        }

        void MatchFromOnlineService(FindRequestContext findRequestContext)
        {
            lock (this.onlineServices)
            {
                foreach (EndpointDiscoveryMetadata endpointDiscoveryMetadata in this.onlineServices.Values)
                {
                    if (!findRequestContext.Criteria.IsMatch(endpointDiscoveryMetadata)) continue;
                    if (findRequestContext.Criteria.Extensions.Count > 0)
                    {
                        int i = 0;
                        while (i < findRequestContext.Criteria.Extensions.Count)
                        {
                            string criteriaValue = findRequestContext.Criteria.Extensions[i].Value;
                            if (endpointDiscoveryMetadata.Extensions.All(x => x.Value != criteriaValue))
                            {
                                break;
                            }
                            i += 1;
                        }

                        if (i == findRequestContext.Criteria.Extensions.Count)
                        {
                            findRequestContext.AddMatchingEndpoint(endpointDiscoveryMetadata);
                        }
                    }
                    else
                    {
                        findRequestContext.AddMatchingEndpoint(endpointDiscoveryMetadata);
                    }
                }
            }
        }

        EndpointDiscoveryMetadata MatchFromOnlineService(ResolveCriteria criteria)
        {
            EndpointDiscoveryMetadata matchingEndpoint = null;
            lock (this.onlineServices)
            {
                foreach (EndpointDiscoveryMetadata endpointDiscoveryMetadata in this.onlineServices.Values)
                {
                    EndpointAddress address;
                    // Check to see if the endpoint has a listenUri and if it differs from the Address URI
                    if (endpointDiscoveryMetadata.ListenUris.Count == 0 ||
                        endpointDiscoveryMetadata.Address.Uri == endpointDiscoveryMetadata.ListenUris[0])
                    {
                        address = endpointDiscoveryMetadata.Address;
                    }
                    else
                    {
                        address = new EndpointAddress(endpointDiscoveryMetadata.ListenUris[0]);
                    }

                    if (criteria.Address == address)
                    {
                        matchingEndpoint = endpointDiscoveryMetadata;
                    }
                }
            }
            return matchingEndpoint;
        }

        void PrintDiscoveryMetadata(EndpointDiscoveryMetadata endpointDiscoveryMetadata, string verb)
        {
            Console.WriteLine("\n**** " + verb + " service of the following type from cache. ");
            foreach (XmlQualifiedName contractName in endpointDiscoveryMetadata.ContractTypeNames)
            {
                Console.WriteLine("** " + contractName);
                break;
            }
            Console.WriteLine("**** Operation Completed");
        }

        sealed class OnOnlineAnnouncementAsyncResult : AsyncResult
        {
            public OnOnlineAnnouncementAsyncResult(AsyncCallback callback, object state)
                : base(callback, state)
            {
                this.Complete(true);
            }

            public static void End(IAsyncResult result)
            {
                AsyncResult.End<OnOnlineAnnouncementAsyncResult>(result);
            }
        }

        sealed class OnOfflineAnnouncementAsyncResult : AsyncResult
        {
            public OnOfflineAnnouncementAsyncResult(AsyncCallback callback, object state)
                : base(callback, state)
            {
                this.Complete(true);
            }

            public static void End(IAsyncResult result)
            {
                AsyncResult.End<OnOfflineAnnouncementAsyncResult>(result);
            }
        }

        sealed class OnFindAsyncResult : AsyncResult
        {
            public OnFindAsyncResult(AsyncCallback callback, object state)
                : base(callback, state)
            {
                this.Complete(true);
            }

            public static void End(IAsyncResult result)
            {
                AsyncResult.End<OnFindAsyncResult>(result);
            }
        }

        sealed class OnResolveAsyncResult : AsyncResult
        {
            EndpointDiscoveryMetadata matchingEndpoint;

            public OnResolveAsyncResult(EndpointDiscoveryMetadata matchingEndpoint, AsyncCallback callback, object state)
                : base(callback, state)
            {
                this.matchingEndpoint = matchingEndpoint;
                this.Complete(true);
            }

            public static EndpointDiscoveryMetadata End(IAsyncResult result)
            {
                OnResolveAsyncResult thisPtr = AsyncResult.End<OnResolveAsyncResult>(result);
                return thisPtr.matchingEndpoint;
            }
        }
    }
}