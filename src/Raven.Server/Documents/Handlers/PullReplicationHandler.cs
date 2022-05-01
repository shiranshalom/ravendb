﻿using System;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Raven.Client.Documents.Operations.OngoingTasks;
using Raven.Client.Documents.Operations.Replication;
using Raven.Client.Exceptions;
using Raven.Client.Json.Serialization;
using Raven.Client.Util;
using Raven.Server.Documents.Handlers.Processors.Replication;
using Raven.Server.Routing;
using Raven.Server.ServerWide.Commands;
using Raven.Server.ServerWide.Context;
using Sparrow.Json;

namespace Raven.Server.Documents.Handlers
{
    public class PullReplicationHandler : DatabaseRequestHandler
    {
        [RavenAction("/databases/*/admin/tasks/pull-replication/hub", "PUT", AuthorizationStatus.DatabaseAdmin)]
        public async Task DefineHub()
        {
            using (var processor = new PullReplicationHandlerProcessorForDefineHub(this))
                await processor.ExecuteAsync();
        }

        [RavenAction("/databases/*/admin/tasks/pull-replication/hub/access", "PUT", AuthorizationStatus.DatabaseAdmin)]
        public async Task RegisterHubAccess()
        {
            var hubTaskName = GetStringQueryString("name", true);

            if (ResourceNameValidator.IsValidResourceName(Database.Name, ServerStore.Configuration.Core.DataDirectory.FullPath, out string errorMessage) == false)
                throw new BadRequestException(errorMessage);

            ServerStore.LicenseManager.AssertCanAddPullReplicationAsHub();

            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                PullReplicationDefinition hubDefinition;

                using (context.OpenReadTransaction())
                {
                    hubDefinition = Server.ServerStore.Cluster.ReadPullReplicationDefinition(Database.Name, hubTaskName, context);
                    if (hubDefinition == null)
                    {
                        HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                        return;
                    }
                }

#pragma warning disable CS0618 // Type or member is obsolete
                if (hubDefinition.Certificates != null && hubDefinition.Certificates.Count > 0)
#pragma warning restore CS0618 // Type or member is obsolete
                {
                    // handle backward compatibility
                    throw new InvalidOperationException("Cannot register hub access to a replication hub that already has inline certificates: " + hubTaskName +
                                                        ". Create a new replication hub and try again");
                }

                var blittableJson = await context.ReadForMemoryAsync(RequestBodyStream(), "register-hub-access");
                var access = JsonDeserializationClient.ReplicationHubAccess(blittableJson);
                access.Validate(hubDefinition.WithFiltering);

                using var cert = new X509Certificate2(Convert.FromBase64String(access.CertificateBase64));

                var command = new RegisterReplicationHubAccessCommand(Database.Name, hubTaskName, access, cert, GetRaftRequestIdFromQuery());
                var result = await Server.ServerStore.SendToLeaderAsync(command);
                await WaitForIndexToBeAppliedAsync(context, result.Index);

                await using (var writer = new AsyncBlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName(nameof(ReplicationHubAccessResponse.RaftCommandIndex));
                    writer.WriteInteger(result.Index);
                    writer.WriteEndObject();
                }
            }
        }

        [RavenAction("/databases/*/admin/tasks/pull-replication/hub/access", "DELETE", AuthorizationStatus.DatabaseAdmin)]
        public async Task UnregisterHubAccess()
        {
            var hub = GetStringQueryString("name", true);
            var thumbprint = GetStringQueryString("thumbprint", true);

            if (ResourceNameValidator.IsValidResourceName(Database.Name, ServerStore.Configuration.Core.DataDirectory.FullPath, out string errorMessage) == false)
                throw new BadRequestException(errorMessage);

            ServerStore.LicenseManager.AssertCanAddPullReplicationAsHub();

            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                var command = new UnregisterReplicationHubAccessCommand(Database.Name, hub, thumbprint, GetRaftRequestIdFromQuery());
                var result = await Server.ServerStore.SendToLeaderAsync(command);
                await WaitForIndexToBeAppliedAsync(context, result.Index);

                await using (var writer = new AsyncBlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName(nameof(ReplicationHubAccessResponse.RaftCommandIndex));
                    writer.WriteInteger(result.Index);
                    writer.WriteEndObject();
                }
            }
        }

        [RavenAction("/databases/*/admin/tasks/pull-replication/hub/access", "GET", AuthorizationStatus.DatabaseAdmin)]
        public async Task ListHubAccess()
        {
            var hub = GetStringQueryString("name", true);
            var filter = GetStringQueryString("filter", false);
            int pageSize = GetPageSize();
            var start = GetStart();

            using (ServerStore.Engine.ContextPool.AllocateOperationContext(out ClusterOperationContext context))
            using (context.OpenReadTransaction())
            {
                var results = Server.ServerStore.Cluster.GetReplicationHubCertificateByHub(context, Database.Name, hub, filter, start, pageSize);

                await using (var writer = new AsyncBlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    writer.WriteStartObject();
                    writer.WriteArray(nameof(ReplicationHubAccessResult.Results), results);
                    writer.WriteEndObject();
                }
            }
        }

        [RavenAction("/databases/*/admin/tasks/sink-pull-replication", "POST", AuthorizationStatus.DatabaseAdmin)]
        public async Task UpdatePullReplicationOnSinkNode()
        {
            if (ResourceNameValidator.IsValidResourceName(Database.Name, ServerStore.Configuration.Core.DataDirectory.FullPath, out string errorMessage) == false)
                throw new BadRequestException(errorMessage);

            ServerStore.LicenseManager.AssertCanAddPullReplicationAsSink();

            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                PullReplicationAsSink pullReplication = null;
                await DatabaseConfigurations(
                    (_, databaseName, blittableJson, guid) => ServerStore.UpdatePullReplicationAsSink(databaseName, blittableJson, guid, out pullReplication),
                    "update-sink-pull-replication", GetRaftRequestIdFromQuery(),
                    fillJson: (json, _, index) =>
                    {
                        using (context.OpenReadTransaction())
                        {
                            var topology = ServerStore.Cluster.ReadDatabaseTopology(context, Database.Name);
                            json[nameof(OngoingTask.ResponsibleNode)] = Database.WhoseTaskIsIt(topology, pullReplication, null);
                        }

                        json[nameof(ModifyOngoingTaskResult.TaskId)] = pullReplication.TaskId == 0 ? index : pullReplication.TaskId;
                    }, statusCode: HttpStatusCode.Created);
            }
        }

        [RavenAction("/databases/*/admin/pull-replication/generate-certificate", "POST", AuthorizationStatus.DatabaseAdmin, DisableOnCpuCreditsExhaustion = true)]
        public async Task GeneratePullReplicationCertificate()
        {
            using (var processor = new PullReplicationHandlerProcessorForGenerateCertificate<DatabaseRequestHandler, DocumentsOperationContext>(this, ContextPool))
                await processor.ExecuteAsync();
        }

        public class PullReplicationCertificate
        {
            public string PublicKey { get; set; }
            public string Certificate { get; set; }
            public string Thumbprint { get; set; }
        }
    }
}
