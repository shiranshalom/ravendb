﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Raven.Client;
using Raven.Client.Documents.Changes;
using Raven.Client.Documents.Commands;
using Raven.Client.Documents.Subscriptions;
using Raven.Client.Exceptions.Documents.Subscriptions;
using Raven.Server.Documents.Handlers;
using Raven.Server.Documents.ShardedTcpHandlers;
using Raven.Server.Documents.Sharding;
using Raven.Server.Documents.Sharding.Commands;
using Raven.Server.Documents.Subscriptions;
using Raven.Server.Documents.TcpHandlers;
using Raven.Server.Json;
using Raven.Server.Routing;
using Raven.Server.ServerWide.Commands.Subscriptions;
using Raven.Server.ServerWide.Context;
using Raven.Server.TrafficWatch;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Sparrow.Utils;
using DeleteSubscriptionCommand = Raven.Server.ServerWide.Commands.Subscriptions.DeleteSubscriptionCommand;

namespace Raven.Server.Documents.ShardedHandlers
{
    public class ShardedSubscriptionsHandler : ShardedRequestHandler
    {
        [RavenShardedAction("/databases/*/subscriptions", "PUT")]
        public async Task Create()
        {
            using (ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            using (context.OpenReadTransaction())
            {
                var json = await context.ReadForMemoryAsync(RequestBodyStream(), null);
                if (TrafficWatchManager.HasRegisteredClients)
                    AddStringToHttpContext(json.ToString(), TrafficWatchChangeType.Subscriptions);

                var options = JsonDeserializationServer.SubscriptionCreationParams(json);
                var id = GetLongQueryString("id", required: false);
                bool? disabled = GetBoolValueQueryString("disabled", required: false);

                await CreateSubscriptionInternal(id, disabled, options, context);
            }
        }

        [RavenShardedAction("/databases/*/subscriptions/update", "POST")]
        public async Task Update()
        {
            using (ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            using (context.OpenReadTransaction())
            {
                var json = await context.ReadForMemoryAsync(RequestBodyStream(), null);
                var options = JsonDeserializationServer.SubscriptionUpdateOptions(json);
                var id = options.Id;

                SubscriptionState state;

                try
                {
                    if (id == null)
                    {
                        state = SubscriptionStorage.GetSubscriptionFromServerStore(ServerStore, context, ShardedContext.DatabaseName, options.Name);
                        id = state.SubscriptionId;
                    }
                    else
                    {
                        state = SubscriptionStorage.GetSubscriptionFromServerStoreById(ServerStore, ShardedContext.DatabaseName, id.Value);

                        // keep the old subscription name
                        if (options.Name == null)
                            options.Name = state.SubscriptionName;
                    }
                }
                catch (SubscriptionDoesNotExistException)
                {
                    if (options.CreateNew)
                    {
                        if (id == null)
                        {
                            // subscription with such name doesn't exist, add new subscription
                            await CreateSubscriptionInternal(id: null, disabled: false, options, context);
                            return;
                        }

                        if (options.Name == null)
                        {
                            // subscription with such id doesn't exist, add new subscription using id
                            await CreateSubscriptionInternal(id, disabled: false, options, context);
                            return;
                        }

                        // this is the case when we have both name and id, and there no subscription with such id
                        try
                        {
                            // check the name
                            state = SubscriptionStorage.GetSubscriptionFromServerStore(ServerStore, context, ShardedContext.DatabaseName, options.Name);
                            id = state.SubscriptionId;
                        }
                        catch (SubscriptionDoesNotExistException)
                        {
                            // subscription with such id or name doesn't exist, add new subscription using both name and id
                            await CreateSubscriptionInternal(id, disabled: false, options, context);
                            return;
                        }
                    }
                    else
                    {
                        throw;
                    }
                }

                if (options.ChangeVector == null)
                    options.ChangeVector = state.ChangeVectorForNextBatchStartingPoint;

                if (options.MentorNode == null)
                    options.MentorNode = state.MentorNode;

                if (options.Query == null)
                    options.Query = state.Query;

                if (SubscriptionsHandler.SubscriptionHasChanges(options, state) == false)
                {
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.NotModified;
                    return;
                }

                await CreateSubscriptionInternal(id, disabled: false, options, context);
            }
        }

        private  async Task CreateSubscriptionInternal(long? id, bool? disabled, SubscriptionCreationOptions options, JsonOperationContext context)
        {
            var sub = SubscriptionConnection.ParseSubscriptionQuery(options.Query);
            if (Enum.TryParse(options.ChangeVector, out Client.Constants.Documents.SubscriptionChangeVectorSpecialStates changeVectorSpecialValue))
            {
                switch (changeVectorSpecialValue)
                {
                    case Client.Constants.Documents.SubscriptionChangeVectorSpecialStates.BeginningOfTime:
                        options.ChangeVector = null;
                        break;

                    case Client.Constants.Documents.SubscriptionChangeVectorSpecialStates.LastDocument:
                        options.ChangeVector = await ShardedContext.GetLastDocumentChangeVectorForCollection(sub.Collection);
                        break;
                }
            }

            var (etag, _) = await ServerStore.SendToLeaderAsync(new PutSubscriptionCommand(ShardedContext.DatabaseName, options.Query, options.MentorNode, GetRaftRequestIdFromQuery())
            {
                InitialChangeVector = options.ChangeVector,
                SubscriptionName = options.Name,
                SubscriptionId = id,
                Disabled = disabled ?? false
            });

            await ServerStore.Cluster.WaitForIndexNotification(etag, ServerStore.Engine.OperationTimeout);

            long subscriptionId;
            long index;
            if (id != null)
            {
                // updated existing subscription
                subscriptionId = id.Value;
                index = etag;

            }
            else
            {
                subscriptionId = etag;
                index = etag;
            }

            var name = options.Name ?? subscriptionId.ToString();

            await GetResponsibleNodesAndWaitForExecution(name, index);

            HttpContext.Response.StatusCode = (int)HttpStatusCode.Created;
            await using (var writer = new AsyncBlittableJsonTextWriter(context, ResponseBodyStream()))
            {
                context.Write(writer, new DynamicJsonValue
                {
                    [nameof(CreateSubscriptionResult.Name)] = name,
                    [nameof(CreateSubscriptionResult.RaftIndex)] = index
                });
            }
        }

        private async Task GetResponsibleNodesAndWaitForExecution(string name, long index)
        {
            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            using (context.OpenReadTransaction())
            {
                var subscription = SubscriptionStorage.GetSubscriptionFromServerStore(ServerStore, context, ShardedContext.DatabaseName, name);

                foreach (var topology in ShardedContext.ShardsTopology)
                {
                    var node = topology.WhoseTaskIsIt(ServerStore.Engine.CurrentState, subscription, null);
                    if (node == null || node == ServerStore.NodeTag)
                        continue;

                    await WaitForExecutionOnSpecificNode(context, ServerStore.GetClusterTopology(context), node, index);
                }
            }
        }

        [RavenShardedAction("/databases/*/subscriptions", "DELETE")]
        public async Task Delete()
        {
            var subscriptionName = GetQueryStringValueAndAssertIfSingleAndNotEmpty("taskName");
            var command = new DeleteSubscriptionCommand(ShardedContext.DatabaseName, subscriptionName, GetRaftRequestIdFromQuery());
            var (etag, _) = await ServerStore.SendToLeaderAsync(command);
            await ServerStore.Cluster.WaitForIndexNotification(etag, ServerStore.Engine.OperationTimeout);

            await NoContent();
        }

        [RavenShardedAction("/databases/*/subscriptions", "GET")]
        public async Task GetAll()
        {
            var start = GetStart();
            var pageSize = GetPageSize();
            var history = GetBoolValueQueryString("history", required: false) ?? false;
            if (history)
                throw new ArgumentException(nameof(history) + " not supported");

            var running = GetBoolValueQueryString("running", required: false) ?? false;
            var id = GetLongQueryString("id", required: false);
            var name = GetStringQueryString("name", required: false);

            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            using (context.OpenReadTransaction())
            {
                List<SubscriptionStorage.SubscriptionGeneralDataAndStats> subscriptions = new List<SubscriptionStorage.SubscriptionGeneralDataAndStats>();
                if (string.IsNullOrEmpty(name) && id == null)
                {
                    var allSubs = SubscriptionStorage.GetAllSubscriptionsWithoutState(context, ShardedContext.DatabaseName, start, pageSize);
                    if (allSubs == null)
                    {
                        HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                        return;
                    }

                    if (running)
                    {
                        foreach (var sub in allSubs)
                        {
                            if (ShardedSubscriptionConnection.Connections.ContainsKey(sub.SubscriptionName))
                            {
                                subscriptions.Add(sub);
                            }
                        }
                    }
                    else
                    {
                        subscriptions = allSubs.ToList();
                    }
                }
                else
                {
                    var subscription = id == null ? SubscriptionStorage.GetSubscriptionFromServerStore(ServerStore, context, ShardedContext.DatabaseName, name) : (SubscriptionStorage.SubscriptionGeneralDataAndStats)SubscriptionStorage.GetSubscriptionFromServerStoreById(ServerStore, ShardedContext.DatabaseName, id.Value);

                    if (subscription == null)
                    {
                        HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                        return;
                    }

                    if (running)
                    {
                        if (ShardedSubscriptionConnection.Connections.ContainsKey(subscription.SubscriptionName) == false)
                        {
                            HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                            return;
                        }
                    }

                    subscriptions = new List<SubscriptionStorage.SubscriptionGeneralDataAndStats> { subscription };
                }

                await using (var writer = new AsyncBlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    SubscriptionsHandler.WriteGetAllResult(writer, subscriptions, context);
                }
            }
        }

        [RavenShardedAction("/databases/*/subscriptions/drop", "POST")]
        public async Task DropSubscriptionConnection()
        {
            var subscriptionId = GetLongQueryString("id", required: false);

            var subscriptionName = GetStringQueryString("name", required: false);
            if (subscriptionId.HasValue && string.IsNullOrEmpty(subscriptionName))
                throw new InvalidOperationException("Drop Subscription Connection by id not supported for sharded connection.");

            var workerId = GetStringQueryString("workerId", required: false);
            try
            {
                using var result = await ShardedContext.TryExecuteCommandOnShards<object>(string.IsNullOrEmpty(workerId)
                 ? () => new DropSubscriptionConnectionCommand(subscriptionName)
                 : () => new DropSubscriptionConnectionCommand(subscriptionName, workerId));
            }
            finally
            {
                if (ShardedSubscriptionConnection.Connections.TryRemove(subscriptionName, out ShardedSubscriptionConnection connection))
                {
                    connection.Dispose();
                }
            }
        }

        [RavenShardedAction("/databases/*/subscriptions/connection-details", "GET")]
        public async Task GetSubscriptionConnectionDetails()
        {
            var subscriptionName = GetStringQueryString("name", false);

            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            using (context.OpenReadTransaction())
            {
                if (string.IsNullOrEmpty(subscriptionName))
                {
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    return;
                }

                if (ShardedSubscriptionConnection.Connections.TryGetValue(subscriptionName, out var connection) == false)
                {
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    return;
                }

                var subscriptionConnectionDetails = new SubscriptionConnectionDetails
                {
                    ClientUri = connection?.ClientUri,
                    Strategy = connection?.Strategy
                };

                await using (var writer = new AsyncBlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    context.Write(writer, subscriptionConnectionDetails.ToJson());
                }
            }
        }

        [RavenShardedAction("/databases/*/subscriptions/try", "POST")]
        public async Task Try()
        {
            using (ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                using var json = await context.ReadForMemoryAsync(RequestBodyStream(), null);
                var tryout = JsonDeserializationServer.SubscriptionTryout(json);

                var sub = SubscriptionConnection.ParseSubscriptionQuery(tryout.Query);
                if (sub.Collection == null)
                    throw new ArgumentException("Collection must be specified");

                const int maxPageSize = 1024;
                var pageSize = GetIntValueQueryString("pageSize") ?? 1;
                if (pageSize > maxPageSize)
                    throw new ArgumentException($"Cannot gather more than {maxPageSize} results during tryouts, but requested number was {pageSize}.");

                var timeLimit = GetIntValueQueryString("timeLimit", false);
                using var result = await ShardedContext.TryExecuteCommandOnShards(() => new SubscriptionTryoutCommand(tryout, pageSize, timeLimit));
                await using (var writer = new AsyncBlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("Results");
                    writer.WriteStartArray();
                    var numberOfDocs = 0;
                    string lastId = null;
                    var f = true;

                    foreach (var cmd in result.ShardCommandResults)
                    {
                        foreach (var res in cmd.Command.Result.Results)
                        {
                            if (numberOfDocs == pageSize)
                                break;

                            if (res is not BlittableJsonReaderObject bjro)
                                continue;

                            using (bjro)
                            {
                                if (f == false)
                                    writer.WriteComma();

                                f = false;
                                WriteBlittable(bjro, writer);
                                if (bjro.TryGetMember(nameof(Constants.Documents.Metadata.Key), out var obj) && obj is BlittableJsonReaderObject metadata
                                                                                                   && metadata.TryGetMember(nameof(Constants.Documents.Metadata.Id), out var obj2) && obj2 is string id)
                                {
                                    lastId = id;
                                }

                                numberOfDocs++;
                            }
                        }
                        if (numberOfDocs == pageSize)
                            break;
                    }

                    writer.WriteEndArray();
                    writer.WriteComma();
                    writer.WritePropertyName("Includes");
                    DevelopmentHelper.ShardingToDo(DevelopmentHelper.TeamMember.Egor, DevelopmentHelper.Severity.Major, "https://issues.hibernatingrhinos.com/issue/RavenDB-16279");
                    writer.WriteStartObject();
                    writer.WriteEndObject();
                    writer.WriteEndObject();

                }
            }
        }

        private static unsafe void WriteBlittable(BlittableJsonReaderObject bjro, AsyncBlittableJsonTextWriter writer)
        {
            var first = true;

            var prop = new BlittableJsonReaderObject.PropertyDetails();
            writer.WriteStartObject();
            using (var buffers = bjro.GetPropertiesByInsertionOrder())
            {
                for (var i = 0; i < buffers.Size; i++)
                {
                    bjro.GetPropertyByIndex(buffers.Properties[i], ref prop);
                    if (first == false)
                    {
                        writer.WriteComma();
                    }

                    first = false;
                    writer.WritePropertyName(prop.Name);
                    writer.WriteValue(prop.Token & BlittableJsonReaderBase.TypesMask, prop.Value);
                }
            }
            writer.WriteEndObject();
        }
    }
}
