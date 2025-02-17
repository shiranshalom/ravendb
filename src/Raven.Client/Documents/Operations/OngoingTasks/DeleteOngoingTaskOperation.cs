﻿using System.Net.Http;
using Raven.Client.Documents.Conventions;
using Raven.Client.Http;
using Raven.Client.Json.Serialization;
using Raven.Client.Util;
using Sparrow.Json;

namespace Raven.Client.Documents.Operations.OngoingTasks
{
    /// <summary>
    /// Operation to delete an ongoing task.
    /// </summary>
    public sealed class DeleteOngoingTaskOperation : IMaintenanceOperation<ModifyOngoingTaskResult>
    {
        private readonly long _taskId;
        private readonly OngoingTaskType _taskType;

        /// <inheritdoc cref="DeleteOngoingTaskOperation"/>
        /// <param name="taskId">The unique identifier of the ongoing task to be deleted.</param>
        /// <param name="taskType">The type of the ongoing task.</param>
        public DeleteOngoingTaskOperation(long taskId, OngoingTaskType taskType)
        {
            _taskId = taskId;
            _taskType = taskType;
        }

        public RavenCommand<ModifyOngoingTaskResult> GetCommand(DocumentConventions conventions, JsonOperationContext ctx)
        {
            return new DeleteOngoingTaskCommand(_taskId, _taskType);
        }

        private sealed class DeleteOngoingTaskCommand : RavenCommand<ModifyOngoingTaskResult>, IRaftCommand
        {
            private readonly long _taskId;
            private readonly OngoingTaskType _taskType;

            public DeleteOngoingTaskCommand(long taskId, OngoingTaskType taskType)
            {
                _taskId = taskId;
                _taskType = taskType;
            }

            public override HttpRequestMessage CreateRequest(JsonOperationContext ctx, ServerNode node, out string url)
            {
                url = $"{node.Url}/databases/{node.Database}/admin/tasks?id={_taskId}&type={_taskType}";

                var request = new HttpRequestMessage
                {
                    Method = HttpMethod.Delete
                };

                return request;
            }

            public override void SetResponse(JsonOperationContext context, BlittableJsonReaderObject response, bool fromCache)
            {
                if (response == null)
                    ThrowInvalidResponse();

                Result = JsonDeserializationClient.ModifyOngoingTaskResult(response);
            }

            public override bool IsReadRequest => false;
            public string RaftUniqueRequestId { get; } = RaftIdGenerator.NewId();
        }
    }
}
