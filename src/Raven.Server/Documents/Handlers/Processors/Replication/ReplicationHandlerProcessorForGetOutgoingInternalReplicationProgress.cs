﻿using System.Collections.Generic;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Raven.Client.Documents.Operations.Replication;
using Raven.Client.Documents.Replication;
using Raven.Server.Documents.Replication.Outgoing;
using Raven.Server.Documents.Replication.Stats;
using Raven.Server.Json;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Context;
using Raven.Server.Utils;
using Raven.Server.Web.Http;

namespace Raven.Server.Documents.Handlers.Processors.Replication
{
    internal sealed class ReplicationHandlerProcessorForGetOutgoingInternalReplicationProgress : AbstractReplicationHandlerProcessorForProgress<DatabaseRequestHandler, DocumentsOperationContext>
    {
        public ReplicationHandlerProcessorForGetOutgoingInternalReplicationProgress([NotNull] DatabaseRequestHandler requestHandler) : base(requestHandler, internalReplication: true)
        {
        }

        protected override bool SupportsCurrentNode => true;

        protected override async ValueTask HandleCurrentNodeAsync()
        {
            using (ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            await using (var writer = new AsyncBlittableJsonTextWriterForDebug(context, ServerStore, RequestHandler.ResponseBodyStream()))
            using (context.OpenReadTransaction())
            {
                var performance = GetProcessesProgress(context);
                writer.WriteReplicationTaskProgress(context, performance);
            }
        }

        protected override Task HandleRemoteNodeAsync(ProxyCommand<IReplicationTaskProgress[]> command, OperationCancelToken token) => RequestHandler.ExecuteRemoteAsync(command, token.Token);

        public IList<InternalReplicationTaskProgress> GetProcessesProgress(DocumentsOperationContext context)
        {
            var replicationTasks = new List<InternalReplicationTaskProgress>();

            foreach (var handler in RequestHandler.Database.ReplicationLoader.OutgoingHandlers)
            {
                if (handler is not OutgoingInternalReplicationHandler || 
                    handler.Destination is not InternalReplication internalReplication)
                    continue;

                replicationTasks.Add(new InternalReplicationTaskProgress
                {
                    TaskName = handler.FromToString,
                    ReplicationType = ReplicationNode.ReplicationType.Internal,
                    DestinationNodeTag = internalReplication.NodeTag,
                    ProcessesProgress = new List<ReplicationProcessProgress>
                    {
                        RequestHandler.Database.ReplicationLoader.GetOutgoingReplicationProgress(context, handler)
                    }
                });
            }

            return replicationTasks;
        }
    }
}
