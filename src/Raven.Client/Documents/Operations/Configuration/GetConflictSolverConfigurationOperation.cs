using System.Net.Http;
using Raven.Client.Documents.Conventions;
using Raven.Client.Http;
using Raven.Client.Json.Serialization;
using Raven.Client.ServerWide;
using Sparrow.Json;

namespace Raven.Client.Documents.Operations.Configuration
{
    public sealed class GetConflictSolverConfigurationOperation : IMaintenanceOperation<ConflictSolver>
    {
        public RavenCommand<ConflictSolver> GetCommand(DocumentConventions conventions, JsonOperationContext context)
        {
            return new GetConflictSolverConfigurationCommand();
        }

        internal sealed class GetConflictSolverConfigurationCommand : RavenCommand<ConflictSolver>
        {
            public override bool IsReadRequest => true;

            public override HttpRequestMessage CreateRequest(JsonOperationContext ctx, ServerNode node, out string url)
            {
                url = $"{node.Url}/databases/{node.Database}/replication/conflicts/solver";

                var request = new HttpRequestMessage
                {
                    Method = HttpMethod.Get
                };

                return request;
            }

            public override void SetResponse(JsonOperationContext context, BlittableJsonReaderObject response, bool fromCache)
            {
                if (response == null)
                    return;

                Result = JsonDeserializationClient.ConflictSolverConfiguration(response);
            }
        }
    }
}
