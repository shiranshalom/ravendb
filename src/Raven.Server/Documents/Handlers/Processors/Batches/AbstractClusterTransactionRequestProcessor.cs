using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Raven.Client.Documents.Commands.Batches;
using Raven.Client.Documents.Operations.CompareExchange;
using Raven.Client.Exceptions;
using Raven.Server.Config.Categories;
using Raven.Server.Documents.Handlers.Batches;
using Raven.Server.Documents.Handlers.Batches.Commands;
using Raven.Server.Rachis;
using Raven.Server.Routing;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Commands;
using Raven.Server.ServerWide.Context;
using Raven.Server.Web;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using static Raven.Server.ServerWide.Commands.ClusterTransactionCommand;

namespace Raven.Server.Documents.Handlers.Processors.Batches;

public abstract class AbstractClusterTransactionRequestProcessor<TRequestHandler, TBatchCommand>
    where TRequestHandler : RequestHandler
    where TBatchCommand : IBatchCommand
{
    protected readonly TRequestHandler RequestHandler;

    protected AbstractClusterTransactionRequestProcessor([NotNull] TRequestHandler requestHandler)
    {
        RequestHandler = requestHandler ?? throw new ArgumentNullException(nameof(requestHandler));
    }

    protected abstract ArraySegment<BatchRequestParser.CommandData> GetParsedCommands(TBatchCommand command);

    protected abstract ClusterConfiguration GetClusterConfiguration();

    public async ValueTask<(long Index, DynamicJsonArray Results)> ProcessAsync(JsonOperationContext context, TBatchCommand command)
    {
        ArraySegment<BatchRequestParser.CommandData> parsedCommands = GetParsedCommands(command);

        var waitForIndexesTimeout = RequestHandler.GetTimeSpanQueryString("waitForIndexesTimeout", required: false);
        var waitForIndexThrow = RequestHandler.GetBoolValueQueryString("waitForIndexThrow", required: false) ?? true;
        var specifiedIndexesQueryString = RequestHandler.HttpContext.Request.Query["waitForSpecificIndex"];

        var disableAtomicDocumentWrites = RequestHandler.GetBoolValueQueryString("disableAtomicDocumentWrites", required: false) ??
                                          GetClusterConfiguration().DisableAtomicDocumentWrites;
        CheckBackwardCompatibility(ref disableAtomicDocumentWrites);

        ValidateCommands(parsedCommands, disableAtomicDocumentWrites);

        var raftRequestId = RequestHandler.GetRaftRequestIdFromQuery();

        var options =
            new ClusterTransactionOptions(taskId: raftRequestId, disableAtomicDocumentWrites,
                RequestHandler.ServerStore.Engine.CommandsVersionManager.CurrentClusterMinimalVersion)
            {
                WaitForIndexesTimeout = waitForIndexesTimeout,
                WaitForIndexThrow = waitForIndexThrow,
                SpecifiedIndexesQueryString = specifiedIndexesQueryString.Count > 0 ? specifiedIndexesQueryString.ToArray() : null
            };

        ClusterTransactionCommand clusterTransactionCommand = CreateClusterTransactionCommand(parsedCommands, options, raftRequestId);

        var (index, result) = await RequestHandler.ServerStore.SendToLeaderAsync(clusterTransactionCommand);
        DynamicJsonArray array = null;

        using (CreateClusterTransactionTask(id: options.TaskId, index, out var onDatabaseCompletionTask))
        {
            array = await GetClusterTransactionDatabaseCommandsResults(result, raftRequestId, clusterTransactionCommand.DatabaseCommandsCount, onDatabaseCompletionTask);
        }

        foreach (var clusterCommands in clusterTransactionCommand.ClusterCommands)
        {
            array.Add(new DynamicJsonValue
            {
                [nameof(ICommandData.Type)] = clusterCommands.Type,
                [nameof(ICompareExchangeValue.Key)] = clusterCommands.Id,
                [nameof(ICompareExchangeValue.Index)] = index
            });
        }

        return (index, array);
    }

    private async Task<DynamicJsonArray> GetClusterTransactionDatabaseCommandsResults(object result, string raftRequestId, long databaseCommandsCount, Task<ClusterTransactionResult> onDatabaseCompletionTask)
    {
        if (result is List<ClusterTransactionErrorInfo> errors)
            ThrowClusterTransactionConcurrencyException(errors);

        if (databaseCommandsCount == 0)
            return new DynamicJsonArray();

        RequestHandler.ServerStore.ForTestingPurposes?.AfterCommitInClusterTransaction?.Invoke();
        ClusterTransactionResult dbResult = null;
        using (var cts = RequestHandler.CreateHttpRequestBoundTimeLimitedOperationToken(RequestHandler.ServerStore.Engine.OperationTimeout))
        {
            dbResult = await WaitForDatabaseCompletion(onDatabaseCompletionTask, cts.Token);
        }

        if (dbResult != null)
        {
            if (dbResult.Errors != null)
                ThrowClusterTransactionConcurrencyException(dbResult.Errors);

            if (dbResult.GeneratedResult != null)
            {
                return dbResult.GeneratedResult;
            }
        }

        /* Failover was occurred,
           and because the ClusterTransactionCommand is already completed in the DocumentDatabase in this time,
           the task with the result is no longer exists ,so the 'onDatabaseCompletionTask' is completed task which is holding null
           (that's why the count has no value). */

        if (result is ClusterTransactionResult clusterTxResult)
        {
            // We'll try to take the count from the result of the cluster transaction command that we get from the leader.
            return clusterTxResult.GeneratedResult;
        }

        // leader isn't updated (thats why the result is empty),
        // so we'll try to take the result from the local history log.
        using (RequestHandler.ServerStore.Engine.ContextPool.AllocateOperationContext(out ClusterOperationContext ctx))
        using (ctx.OpenReadTransaction())
        {
            if (RequestHandler.ServerStore.Engine.LogHistory.TryGetResultByGuid<ClusterTransactionResult>(ctx, raftRequestId, out var clusterTxLocalResult))
            {
                return clusterTxLocalResult.GeneratedResult;
            }
        }

        // the command was already deleted from the log
        throw new InvalidOperationException(
            "Cluster-transaction was succeeded, but Leader is outdated and its results are inaccessible (the command has been already deleted from the history log).  We recommend you to update all nodes in the cluster to the last stable version.");
    }

    public abstract IDisposable CreateClusterTransactionTask(string id, long index, out Task<ClusterTransactionResult> task);

    public abstract Task<ClusterTransactionResult> WaitForDatabaseCompletion(Task<ClusterTransactionResult> onDatabaseCompletionTask, CancellationToken token);

    private void ThrowClusterTransactionConcurrencyException(List<ClusterTransactionErrorInfo> errors)
    {
        RequestHandler.HttpContext.Response.StatusCode = (int)HttpStatusCode.Conflict;
        throw new ClusterTransactionConcurrencyException($"Failed to execute cluster transaction due to the following issues: {string.Join(Environment.NewLine, errors.Select(e => e.Message))}")
        {
            ConcurrencyViolations = errors.Select(e => e.Violation).ToArray()
        };
    }

    protected abstract ClusterTransactionCommand CreateClusterTransactionCommand(ArraySegment<BatchRequestParser.CommandData> parsedCommands,
        ClusterTransactionOptions options, string raftRequestId);

    private void CheckBackwardCompatibility(ref bool disableAtomicDocumentWrites)
    {
        if (disableAtomicDocumentWrites)
            return;

        if (RequestRouter.TryGetClientVersion(RequestHandler.HttpContext, out var clientVersion) == false)
        {
            disableAtomicDocumentWrites = true;
            return;
        }

        if (clientVersion.Major < 5 || (clientVersion.Major == 5 && clientVersion.Minor < 2))
        {
            disableAtomicDocumentWrites = true;
        }
    }
}
