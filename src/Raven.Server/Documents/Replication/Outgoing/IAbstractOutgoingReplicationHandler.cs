﻿using System;
using Raven.Client.Documents.Replication;
using Raven.Server.ServerWide;

namespace Raven.Server.Documents.Replication.Outgoing
{
    public interface IAbstractOutgoingReplicationHandler : IDisposable, IReportOutgoingReplicationPerformance
    {
        public ReplicationNode Node { get; }
        public long LastSentDocumentEtag { get; }
        public string LastAcceptedChangeVector { get; set; }
        public ReplicationNode Destination { get; }
        public bool IsConnectionDisposed { get; }
        public ServerStore Server { get; }
        public string GetNode();
        public void Start();
    }
}
