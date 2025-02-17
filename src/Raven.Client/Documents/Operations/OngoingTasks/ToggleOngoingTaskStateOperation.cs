﻿using System;
using System.Net.Http;
using Raven.Client.Documents.Conventions;
using Raven.Client.Http;
using Raven.Client.Json.Serialization;
using Raven.Client.Util;
using Sparrow.Json;

namespace Raven.Client.Documents.Operations.OngoingTasks
{
    /// <summary>
    /// Operation to enable or disable an ongoing task.
    /// </summary>
    public sealed class ToggleOngoingTaskStateOperation : IMaintenanceOperation<ModifyOngoingTaskResult>
    {
        private readonly long _taskId;
        private readonly string _taskName;
        private readonly OngoingTaskType _type;
        private readonly bool _disable;

        /// <inheritdoc cref="ToggleOngoingTaskStateOperation"/>
        /// <param name="taskId">The unique identifier of the ongoing task.</param>
        /// <param name="type">The type of the ongoing task.</param>
        /// <param name="disable">A boolean flag indicating whether to disable (true) or enable (false) the task.</param>
        public ToggleOngoingTaskStateOperation(long taskId, OngoingTaskType type, bool disable)
        {
            _taskId = taskId;
            _type = type;
            _disable = disable;
        }

        /// <inheritdoc cref="ToggleOngoingTaskStateOperation"/>
        /// <param name="taskName">The name of the ongoing task.</param>
        /// <param name="type">The type of the ongoing task.</param>
        /// <param name="disable">A boolean flag indicating whether to disable (true) or enable (false) the task.</param>
        /// <exception cref="ArgumentException">Thrown when <paramref name="taskName"/> is null or whitespace.</exception>
        internal ToggleOngoingTaskStateOperation(string taskName, OngoingTaskType type, bool disable)
        {
            if (string.IsNullOrWhiteSpace(taskName)) 
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(taskName));

            _taskName = taskName;
            _type = type;
            _disable = disable;
        }

        public RavenCommand<ModifyOngoingTaskResult> GetCommand(DocumentConventions conventions, JsonOperationContext ctx)
        {
            return new ToggleTaskStateCommand(_taskId, _taskName, _type, _disable);
        }

        private sealed class ToggleTaskStateCommand : RavenCommand<ModifyOngoingTaskResult>, IRaftCommand
        {
            private readonly long _taskId;
            private readonly string _taskName;
            private readonly OngoingTaskType _type;
            private readonly bool _disable;

            public ToggleTaskStateCommand(long taskId, string taskName, OngoingTaskType type, bool disable)
            {
                _taskId = taskId;
                _taskName = taskName;
                _type = type;
                _disable = disable;
            }

            public override HttpRequestMessage CreateRequest(JsonOperationContext ctx, ServerNode node, out string url)
            {
                url = $"{node.Url}/databases/{node.Database}/admin/tasks/state?key={_taskId}&type={_type}&disable={_disable}";

                if (_taskName != null)
                    url += $"&taskName={Uri.EscapeDataString(_taskName)}";

                var request = new HttpRequestMessage
                {
                    Method = HttpMethod.Post
                };

                return request;
            }

            public override void SetResponse(JsonOperationContext context, BlittableJsonReaderObject response, bool fromCache)
            {
                if (response != null)
                    Result = JsonDeserializationClient.ModifyOngoingTaskResult(response);
            }

            public override bool IsReadRequest => false;
            public string RaftUniqueRequestId { get; } = RaftIdGenerator.NewId();
        }
    }
}
