﻿using System;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Raven.Server.Documents.Handlers.Processors.Replication;
using Raven.Server.Documents.Replication.Stats;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Context;
using Raven.Server.Web.Http;

namespace Raven.Server.Documents.Sharding.Handlers.Processors.Replication
{
    internal sealed class ShardedReplicationHandlerProcessorForGetOutgoingInternalReplicationProgress : AbstractReplicationHandlerProcessorForProgress<ShardedDatabaseRequestHandler, TransactionOperationContext>
    {
        public ShardedReplicationHandlerProcessorForGetOutgoingInternalReplicationProgress([NotNull] ShardedDatabaseRequestHandler requestHandler) : base(requestHandler, internalReplication: true)
        {
        }

        protected override bool SupportsCurrentNode => false;

        protected override ValueTask HandleCurrentNodeAsync() => throw new NotSupportedException();

        protected override Task HandleRemoteNodeAsync(ProxyCommand<IReplicationTaskProgress[]> command, OperationCancelToken token)
        {
            var shardNumber = GetShardNumber();

            return RequestHandler.DatabaseContext.ShardExecutor.ExecuteSingleShardAsync(command, shardNumber, token.Token);
        }
    }
}
