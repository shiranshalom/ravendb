﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Raven.Client;
using Raven.Client.Documents.Changes;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;
using Raven.Client.Exceptions;
using Raven.Client.Exceptions.Documents.Indexes;
using Raven.Client.Extensions;
using Raven.Server.Documents.Patch;
using Raven.Server.Documents.Queries;
using Raven.Server.Documents.Queries.AST;
using Raven.Server.Json;
using Raven.Server.NotificationCenter;
using Raven.Server.NotificationCenter.Notifications.Details;
using Raven.Server.Routing;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Context;
using Raven.Server.TrafficWatch;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using PatchRequest = Raven.Server.Documents.Patch.PatchRequest;

namespace Raven.Server.Documents.Handlers
{
    public class QueriesHandler : DatabaseRequestHandler
    {
        [RavenAction("/databases/*/queries", "POST", AuthorizationStatus.ValidUser, DisableOnCpuCreditsExhaustion = true)]
        public Task Post()
        {
            return HandleQuery(HttpMethod.Post);
        }

        [RavenAction("/databases/*/queries", "GET", AuthorizationStatus.ValidUser, DisableOnCpuCreditsExhaustion = true)]
        public Task Get()
        {
            return HandleQuery(HttpMethod.Get);
        }

        public async Task HandleQuery(HttpMethod httpMethod)
        {
            using (var tracker = new RequestTimeTracker(HttpContext, Logger, Database, "Query"))
            {
                try
                {
                    using (var token = CreateTimeLimitedQueryToken())
                    using (Database.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
                    {
                        var debug = GetStringQueryString("debug", required: false);
                        if (string.IsNullOrWhiteSpace(debug) == false)
                        {
                            await Debug(context, debug, token, tracker, httpMethod);
                            return;
                        }

                        var diagnostics = GetBoolValueQueryString("diagnostics", required: false) ?? false;
                        await Query(context, token, tracker, httpMethod, diagnostics);
                    }
                }
                catch (Exception e)
                {
                    if (tracker.Query == null)
                    {
                        string errorMessage;
                        if (e is EndOfStreamException || e is ArgumentException)
                        {
                            errorMessage = "Failed: " + e.Message;
                        }
                        else
                        {
                            errorMessage = "Failed: " +
                                           HttpContext.Request.Path.Value +
                                           e.ToString();
                        }
                        tracker.Query = errorMessage;
                        if (TrafficWatchManager.HasRegisteredClients)
                            AddStringToHttpContext(errorMessage, TrafficWatchChangeType.Queries);
                    }
                    throw;
                }
            }
        }

        private async Task FacetedQuery(IndexQueryServerSide indexQuery, DocumentsOperationContext context, OperationCancelToken token)
        {
            var existingResultEtag = GetLongFromHeaders("If-None-Match");

            var result = await Database.QueryRunner.ExecuteFacetedQuery(indexQuery, existingResultEtag, context, token);

            if (result.NotModified)
            {
                HttpContext.Response.StatusCode = (int)HttpStatusCode.NotModified;
                return;
            }

            HttpContext.Response.Headers[Constants.Headers.Etag] = CharExtensions.ToInvariantString(result.ResultEtag);

            int numberOfResults;
            using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
            {
                writer.WriteFacetedQueryResult(context, result, numberOfResults: out numberOfResults);
            }

            Database.QueryMetadataCache.MaybeAddToCache(indexQuery.Metadata, result.IndexName);
            AddPagingPerformanceHint(PagingOperationType.Queries, $"{nameof(FacetedQuery)} ({result.IndexName})", indexQuery.Query, numberOfResults, indexQuery.PageSize, result.DurationInMs);
        }

        private async Task Query(DocumentsOperationContext context, OperationCancelToken token, RequestTimeTracker tracker, HttpMethod method, bool diagnostics)
        {
            var indexQuery = await GetIndexQuery(context, method, tracker);
            indexQuery.Diagnostics = diagnostics ? new List<string>() : null;

            if (TrafficWatchManager.HasRegisteredClients)
                TrafficWatchQuery(indexQuery);

            var existingResultEtag = GetLongFromHeaders("If-None-Match");

            if (indexQuery.Metadata.HasFacet)
            {
                await FacetedQuery(indexQuery, context, token);
                return;
            }

            if (indexQuery.Metadata.HasSuggest)
            {
                await SuggestQuery(indexQuery, context, token);
                return;
            }

            var metadataOnly = GetBoolValueQueryString("metadataOnly", required: false) ?? false;
            var shouldReturnServerSideQuery = GetBoolValueQueryString("includeServerSideQuery", required: false) ?? false;

            DocumentQueryResult result;
            try
            {
                result = await Database.QueryRunner.ExecuteQuery(indexQuery, context, existingResultEtag, token).ConfigureAwait(false);
            }
            catch (IndexDoesNotExistException)
            {
                HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            if (result.NotModified)
            {
                HttpContext.Response.StatusCode = (int)HttpStatusCode.NotModified;
                return;
            }

            HttpContext.Response.Headers[Constants.Headers.Etag] = CharExtensions.ToInvariantString(result.ResultEtag);

            int numberOfResults;
            using (var writer = new AsyncBlittableJsonTextWriter(context, ResponseBodyStream(), Database.DatabaseShutdown))
            {
                result.Timings = indexQuery.Timings?.ToTimings();
                numberOfResults = await writer.WriteDocumentQueryResultAsync(context, result, metadataOnly, WriteAdditionalData(indexQuery, shouldReturnServerSideQuery));
                await writer.OuterFlushAsync();
            }

            Database.QueryMetadataCache.MaybeAddToCache(indexQuery.Metadata, result.IndexName);
            AddPagingPerformanceHint(PagingOperationType.Queries, $"{nameof(Query)} ({result.IndexName})", indexQuery.Query, numberOfResults, indexQuery.PageSize, result.DurationInMs);
        }

        private Action<AsyncBlittableJsonTextWriter> WriteAdditionalData(IndexQueryServerSide indexQuery, bool shouldReturnServerSideQuery)
        {
            if (indexQuery.Diagnostics == null && shouldReturnServerSideQuery == false)
                return null;

            return w =>
            {
                if (shouldReturnServerSideQuery)
                {
                    w.WriteComma();
                    w.WritePropertyName(nameof(indexQuery.ServerSideQuery));
                    w.WriteString(indexQuery.ServerSideQuery);
                }

                if (indexQuery.Diagnostics != null)
                {
                    w.WriteComma();
                    w.WriteArray(nameof(indexQuery.Diagnostics), indexQuery.Diagnostics);
                }
            };
        }

        private async Task<IndexQueryServerSide> GetIndexQuery(JsonOperationContext context, HttpMethod method, RequestTimeTracker tracker)
        {
            if (method == HttpMethod.Get)
                return IndexQueryServerSide.Create(HttpContext, GetStart(), GetPageSize(), context, tracker);

            var json = await context.ReadForMemoryAsync(RequestBodyStream(), "index/query");

            return IndexQueryServerSide.Create(HttpContext, json, Database.QueryMetadataCache, tracker, Database);
        }

        private async Task SuggestQuery(IndexQueryServerSide indexQuery, DocumentsOperationContext context, OperationCancelToken token)
        {
            var existingResultEtag = GetLongFromHeaders("If-None-Match");

            var result = await Database.QueryRunner.ExecuteSuggestionQuery(indexQuery, context, existingResultEtag, token);
            if (result.NotModified)
            {
                HttpContext.Response.StatusCode = (int)HttpStatusCode.NotModified;
                return;
            }

            HttpContext.Response.Headers[Constants.Headers.Etag] = CharExtensions.ToInvariantString(result.ResultEtag);

            int numberOfResults;
            using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
            {
                writer.WriteSuggestionQueryResult(context, result, numberOfResults: out numberOfResults);
            }

            AddPagingPerformanceHint(PagingOperationType.Queries, $"{nameof(SuggestQuery)} ({result.IndexName})", indexQuery.Query, numberOfResults, indexQuery.PageSize, result.DurationInMs);
        }

        private async Task DetailedGraphResult(DocumentsOperationContext context, RequestTimeTracker tracker, HttpMethod method)
        {
            var indexQuery = await GetIndexQuery(context, method, tracker);
            var queryRunner = Database.QueryRunner.GetRunner(indexQuery);
            if (!(queryRunner is GraphQueryRunner gqr))
                throw new InvalidOperationException("The specified query is not a graph query.");
            using (var token = CreateTimeLimitedQueryToken())
            using (Database.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext ctx))
            using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
            {
                await gqr.WriteDetailedQueryResult(indexQuery, ctx, writer, token);
            }
        }

        private async Task Graph(DocumentsOperationContext context, RequestTimeTracker tracker, HttpMethod method)
        {
            var indexQuery = await GetIndexQuery(context, method, tracker);
            var queryRunner = Database.QueryRunner.GetRunner(indexQuery);
            if (!(queryRunner is GraphQueryRunner gqr))
                throw new InvalidOperationException("The specified query is not a graph query.");

            using (var token = CreateTimeLimitedQueryToken())
            using (Database.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext ctx))
            {
                var results = await gqr.GetAnalyzedQueryResults(indexQuery, ctx, null, token);

                var nodes = new DynamicJsonArray();
                var edges = new DynamicJsonArray();
                var output = new DynamicJsonValue
                {
                    ["Nodes"] = nodes,
                    ["Edges"] = edges
                };

                foreach (var item in results.Nodes)
                {
                    var val = item.Value;
                    if (val is Document d)
                    {
                        d.EnsureMetadata();
                        val = d.Data;
                    }
                    nodes.Add(new DynamicJsonValue
                    {
                        ["Id"] = item.Key,
                        ["Value"] = val
                    });
                }
                var added = new HashSet<(string, string, object)>();
                foreach (var edge in results.Edges)
                {
                    added.Clear();
                    var array = new DynamicJsonArray();
                    var djv = new DynamicJsonValue
                    {
                        ["Name"] = edge.Key,
                        ["Results"] = array
                    };
                    foreach (var item in edge.Value)
                    {
                        var edgeVal = item.Edge;
                        if (edgeVal is Document d)
                        {
                            edgeVal = d.Id?.ToString() ?? "anonymous/" + Guid.NewGuid();
                        }
                        if(added.Add((item.Source, item.Destination, edgeVal)) == false)
                            continue;
                        array.Add(new DynamicJsonValue
                        {
                            ["From"] = item.Source,
                            ["To"] = item.Destination,
                            ["Edge"] = edgeVal
                        });
                    }
                    edges.Add(djv);
                }

                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    context.Write(writer, output);
                }
            }
        }

        private async Task Explain(DocumentsOperationContext context, RequestTimeTracker tracker, HttpMethod method)
        {
            var indexQuery = await GetIndexQuery(context, method, tracker);

            var explanations = Database.QueryRunner.ExplainDynamicIndexSelection(indexQuery, out string indexName);

            using (var writer = new AsyncBlittableJsonTextWriter(context, ResponseBodyStream(), Database.DatabaseShutdown))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("IndexName");
                writer.WriteString(indexName);
                writer.WriteComma();
                writer.WriteArray(context, "Results", explanations, (w, c, explanation) =>
                {
                    w.WriteExplanation(context, explanation);
                });

                writer.WriteEndObject();
                await writer.OuterFlushAsync();
            }
        }

        private async Task ServerSideQuery(DocumentsOperationContext context, RequestTimeTracker tracker, HttpMethod method)
        {
            var indexQuery = await GetIndexQuery(context, method, tracker);
            using (var writer = new AsyncBlittableJsonTextWriter(context, ResponseBodyStream(), Database.DatabaseShutdown))
            {
                writer.WriteStartObject();
                writer.WritePropertyName(nameof(indexQuery.ServerSideQuery));
                writer.WriteString(indexQuery.ServerSideQuery);

                writer.WriteEndObject();
                await writer.OuterFlushAsync();
            }
        }

        [RavenAction("/databases/*/queries", "DELETE", AuthorizationStatus.ValidUser)]
        public Task Delete()
        {
            var returnContextToPool = ContextPool.AllocateOperationContext(out DocumentsOperationContext context); // we don't dispose this as operation is async

            try
            {
                using (var tracker = new RequestTimeTracker(HttpContext, Logger, Database, "DeleteByQuery"))
                {
                    var reader = context.Read(RequestBodyStream(), "queries/delete");
                    var query = IndexQueryServerSide.Create(HttpContext, reader, Database.QueryMetadataCache, tracker);

                    if (TrafficWatchManager.HasRegisteredClients)
                        TrafficWatchQuery(query);

                    ExecuteQueryOperation(query,
                        (runner, options, onProgress, token) => runner.ExecuteDeleteQuery(query, options, context, onProgress, token), returnContextToPool, Operations.Operations.OperationType.DeleteByQuery);

                    return Task.CompletedTask;
                }
            }
            catch
            {
                returnContextToPool.Dispose();
                throw;
            }
        }

        [RavenAction("/databases/*/queries/test", "PATCH", AuthorizationStatus.ValidUser, DisableOnCpuCreditsExhaustion = true)]
        public Task PatchTest()
        {
            using (ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            {
                var reader = context.Read(RequestBodyStream(), "queries/patch");
                if (reader == null)
                    throw new BadRequestException("Missing JSON content.");
                if (reader.TryGet("Query", out BlittableJsonReaderObject queryJson) == false || queryJson == null)
                    throw new BadRequestException("Missing 'Query' property.");

                var query = IndexQueryServerSide.Create(HttpContext, queryJson, Database.QueryMetadataCache, null, queryType: QueryType.Update);

                if (TrafficWatchManager.HasRegisteredClients)
                    TrafficWatchQuery(query);

                var patch = new PatchRequest(query.Metadata.GetUpdateBody(query.QueryParameters), PatchRequestType.Patch, query.Metadata.DeclaredFunctions);

                var docId = GetQueryStringValueAndAssertIfSingleAndNotEmpty("id");

                var command = new PatchDocumentCommand(context, docId,
                    expectedChangeVector: null,
                    skipPatchIfChangeVectorMismatch: false,
                    patch: (patch, query.QueryParameters),
                    patchIfMissing: (null, null),
                    database: context.DocumentDatabase,
                    debugMode: true,
                    isTest: true,
                    collectResultsNeeded: true,
                    returnDocument: false);

                using (context.OpenWriteTransaction())
                {
                    command.Execute(context, null);
                }

                switch (command.PatchResult.Status)
                {
                    case PatchStatus.DocumentDoesNotExist:
                        HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                        return Task.CompletedTask;
                    case PatchStatus.Created:
                        HttpContext.Response.StatusCode = (int)HttpStatusCode.Created;
                        break;
                    case PatchStatus.Skipped:
                        HttpContext.Response.StatusCode = (int)HttpStatusCode.NotModified;
                        return Task.CompletedTask;
                    case PatchStatus.Patched:
                    case PatchStatus.NotModified:
                        HttpContext.Response.StatusCode = (int)HttpStatusCode.OK;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                WritePatchResultToResponse(context, command);

                return Task.CompletedTask;
            }
        }

        private void WritePatchResultToResponse(DocumentsOperationContext context, PatchDocumentCommand command)
        {
            using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
            {
                writer.WriteStartObject();

                writer.WritePropertyName(nameof(command.PatchResult.Status));
                writer.WriteString(command.PatchResult.Status.ToString());
                writer.WriteComma();

                writer.WritePropertyName(nameof(command.PatchResult.ModifiedDocument));
                writer.WriteObject(command.PatchResult.ModifiedDocument);

                writer.WriteComma();
                writer.WritePropertyName(nameof(command.PatchResult.OriginalDocument));
                writer.WriteObject(command.PatchResult.OriginalDocument);

                writer.WriteComma();

                writer.WritePropertyName(nameof(command.PatchResult.Debug));

                context.Write(writer, new DynamicJsonValue
                {
                    ["Info"] = new DynamicJsonArray(command.DebugOutput),
                    ["Actions"] = command.DebugActions
                });

                writer.WriteEndObject();
            }
        }

        [RavenAction("/databases/*/queries", "PATCH", AuthorizationStatus.ValidUser, DisableOnCpuCreditsExhaustion = true)]
        public Task Patch()
        {
            var returnContextToPool = ContextPool.AllocateOperationContext(out DocumentsOperationContext context); // we don't dispose this as operation is async

            try
            {
                var reader = context.Read(RequestBodyStream(), "queries/patch");
                if (reader == null)
                    throw new BadRequestException("Missing JSON content.");
                if (reader.TryGet("Query", out BlittableJsonReaderObject queryJson) == false || queryJson == null)
                    throw new BadRequestException("Missing 'Query' property.");

                var query = IndexQueryServerSide.Create(HttpContext, queryJson, Database.QueryMetadataCache, null, queryType: QueryType.Update);

                if (TrafficWatchManager.HasRegisteredClients)
                    TrafficWatchQuery(query);

                var patch = new PatchRequest(query.Metadata.GetUpdateBody(query.QueryParameters), PatchRequestType.Patch, query.Metadata.DeclaredFunctions);

                ExecuteQueryOperation(query,
                    (runner, options, onProgress, token) => runner.ExecutePatchQuery(
                        query, options, patch, query.QueryParameters, context, onProgress, token),
                    returnContextToPool, Operations.Operations.OperationType.UpdateByQuery);

                return Task.CompletedTask;
            }
            catch
            {
                returnContextToPool.Dispose();
                throw;
            }
        }
        /// <summary>
        /// TrafficWatchQuery writes query data to httpContext
        /// </summary>
        /// <param name="indexQuery"></param>
        private void TrafficWatchQuery(IndexQueryServerSide indexQuery)
        {
            var sb = new StringBuilder();
            // append stringBuilder with the query
            sb.Append(indexQuery.Query);
            // if query got parameters append with parameters
            if (indexQuery.QueryParameters != null && indexQuery.QueryParameters.Count > 0)
                sb.AppendLine().Append(indexQuery.QueryParameters);
            AddStringToHttpContext(sb.ToString(), TrafficWatchChangeType.Queries);
        }

        private void ExecuteQueryOperation(IndexQueryServerSide query,
                Func<QueryRunner,
                QueryOperationOptions,
                Action<IOperationProgress>, OperationCancelToken,
                Task<IOperationResult>> operation,
                IDisposable returnContextToPool,
                Operations.Operations.OperationType operationType)
        {
            var options = GetQueryOperationOptions();
            var token = CreateTimeLimitedQueryOperationToken();

            var operationId = Database.Operations.GetNextOperationId();

            var indexName = query.Metadata.IsDynamic
                ? (query.Metadata.IsCollectionQuery ? "collection/" : "dynamic/") + query.Metadata.CollectionName
                : query.Metadata.IndexName;

            var details = new BulkOperationResult.OperationDetails
            {
                Query = query.Query
            };

            var task = Database.Operations.AddOperation(
                Database,
                indexName,
                operationType,
                onProgress => operation(Database.QueryRunner, options, onProgress, token), operationId, details, token);

            using (ContextPool.AllocateOperationContext(out JsonOperationContext context))
            using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
            {
                writer.WriteOperationIdAndNodeTag(context, operationId, ServerStore.NodeTag);
            }

            task.ContinueWith(_ =>
            {
                using (returnContextToPool)
                    token.Dispose();
            });
        }

        private async Task Debug(DocumentsOperationContext context, string debug, OperationCancelToken token, RequestTimeTracker tracker, HttpMethod method)
        {
            if (string.Equals(debug, "entries", StringComparison.OrdinalIgnoreCase))
            {
                await IndexEntries(context, token, tracker, method);
                return;
            }

            if (string.Equals(debug, "explain", StringComparison.OrdinalIgnoreCase))
            {
                await Explain(context, tracker, method);
                return;
            }

            if (string.Equals(debug, "serverSideQuery", StringComparison.OrdinalIgnoreCase))
            {
                await ServerSideQuery(context, tracker, method);
                return;
            }

            if (string.Equals(debug, "graph", StringComparison.OrdinalIgnoreCase))
            {
                await Graph(context, tracker, method);
                return;
            }

            if (string.Equals(debug, "detailedGraphResult", StringComparison.OrdinalIgnoreCase))
            {
                await DetailedGraphResult(context, tracker, method);
                return;
            }
            throw new NotSupportedException($"Not supported query debug operation: '{debug}'");
        }

        private async Task IndexEntries(DocumentsOperationContext context, OperationCancelToken token, RequestTimeTracker tracker, HttpMethod method)
        {
            var indexQuery = await GetIndexQuery(context, method, tracker);
            var existingResultEtag = GetLongFromHeaders("If-None-Match");

            var result = await Database.QueryRunner.ExecuteIndexEntriesQuery(indexQuery, context, existingResultEtag, token);

            if (result.NotModified)
            {
                HttpContext.Response.StatusCode = (int)HttpStatusCode.NotModified;
                return;
            }

            HttpContext.Response.Headers[Constants.Headers.Etag] = CharExtensions.ToInvariantString(result.ResultEtag);

            using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
            {
                writer.WriteIndexEntriesQueryResult(context, result);
            }
        }

        private QueryOperationOptions GetQueryOperationOptions()
        {
            return new QueryOperationOptions
            {
                AllowStale = GetBoolValueQueryString("allowStale", required: false) ?? false,
                MaxOpsPerSecond = GetIntValueQueryString("maxOpsPerSec", required: false),
                StaleTimeout = GetTimeSpanQueryString("staleTimeout", required: false),
                RetrieveDetails = GetBoolValueQueryString("details", required: false) ?? false
            };
        }
    }
}
