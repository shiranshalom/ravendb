﻿// -----------------------------------------------------------------------
//  <copyright file="RevisionsHandler.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client;
using Raven.Client.Documents.Commands;
using Raven.Client.Documents.Operations.Revisions;
using Raven.Client.Documents.Session.Operations;
using Raven.Server.Documents.Revisions;
using Raven.Server.Json;
using Raven.Server.NotificationCenter.Notifications.Details;
using Raven.Server.Routing;
using Raven.Server.ServerWide.Context;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Sparrow.Utils;

namespace Raven.Server.Documents.Handlers
{
    public class RevisionsHandler : DatabaseRequestHandler
    {
        [RavenAction("/databases/*/revisions/config", "GET", AuthorizationStatus.ValidUser, EndpointType.Read)]
        public async Task GetRevisionsConfig()
        {
            using (Server.ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            using (context.OpenReadTransaction())
            {
                RevisionsConfiguration revisionsConfig;
                using (var rawRecord = Server.ServerStore.Cluster.ReadRawDatabaseRecord(context, Database.Name))
                {
                    revisionsConfig = rawRecord?.RevisionsConfiguration;
                }

                if (revisionsConfig != null)
                {
                    var revisionsCollection = new DynamicJsonValue();
                    foreach (var collection in revisionsConfig.Collections)
                    {
                        revisionsCollection[collection.Key] = collection.Value.ToJson();
                    }

                    await using (var writer = new AsyncBlittableJsonTextWriter(context, ResponseBodyStream()))
                    {
                        context.Write(writer, new DynamicJsonValue
                        {
                            [nameof(revisionsConfig.Default)] = revisionsConfig.Default?.ToJson(),
                            [nameof(revisionsConfig.Collections)] = revisionsCollection
                        });
                    }
                }
                else
                {
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                }
            }
        }

        [RavenAction("/databases/*/revisions/conflicts/config", "GET", AuthorizationStatus.ValidUser, EndpointType.Read)]
        public async Task GetConflictRevisionsConfig()
        {
            using (Server.ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            using (context.OpenReadTransaction())
            {
                RevisionsCollectionConfiguration revisionsForConflictsConfig;
                using (var rawRecord = Server.ServerStore.Cluster.ReadRawDatabaseRecord(context, Database.Name))
                {
                    revisionsForConflictsConfig = rawRecord?.RevisionsForConflicts ?? Database.DocumentsStorage?.RevisionsStorage?.ConflictConfiguration?.Default;
                }

                if (revisionsForConflictsConfig != null)
                {
                    await using (var writer = new AsyncBlittableJsonTextWriter(context, ResponseBodyStream()))
                    {
                        context.Write(writer, revisionsForConflictsConfig.ToJson());
                    }
                }
                else
                {
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                }
            }
        }

        public const string ConflictedRevisionsConfigTag = "conflicted-revisions-config";

        [RavenAction("/databases/*/admin/revisions/conflicts/config", "POST", AuthorizationStatus.DatabaseAdmin)]
        public Task ConfigConflictedRevisions()
        {
            return DatabaseConfigurations(
                ServerStore.ModifyRevisionsForConflicts,
                ConflictedRevisionsConfigTag,
                GetRaftRequestIdFromQuery());
        }

        public const string ReadRevisionsConfigTag = "read-revisions-config";

        [RavenAction("/databases/*/admin/revisions/config", "POST", AuthorizationStatus.DatabaseAdmin)]
        public Task ConfigRevisions()
        {
            return DatabaseConfigurations(
                ServerStore.ModifyDatabaseRevisions,
                ReadRevisionsConfigTag,
                GetRaftRequestIdFromQuery(),
                beforeSetupConfiguration: (string name, ref BlittableJsonReaderObject configuration, JsonOperationContext context) =>
                {
                    if (configuration == null ||
                        configuration.TryGet(nameof(RevisionsConfiguration.Collections), out BlittableJsonReaderObject collections) == false ||
                        collections?.Count > 0 == false)
                        return;

                    var uniqueKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var prop = new BlittableJsonReaderObject.PropertyDetails();

                    for (var i = 0; i < collections.Count; i++)
                    {
                        collections.GetPropertyByIndex(i, ref prop);

                        if (uniqueKeys.Add(prop.Name) == false)
                        {
                            throw new InvalidOperationException("Cannot have two different revision configurations on the same collection. " +
                                                                $"Collection name : '{prop.Name}'");
                        }
                    }
                });
        }

        [RavenAction("/databases/*/admin/revisions/config/enforce", "POST", AuthorizationStatus.DatabaseAdmin)]
        public async Task EnforceConfigRevisions()
        {
            var token = CreateTimeLimitedBackgroundOperationToken();
            var operationId = ServerStore.Operations.GetNextOperationId();

            EnforceRevisionsConfigurationRequest configuration;
            using (ServerStore.ContextPool.AllocateOperationContext(out JsonOperationContext context))
            {
                var json = await context.ReadForMemoryAsync(RequestBodyStream(), "revisions/revert");
                configuration = JsonDeserializationServer.EnforceRevisionsConfiguration(json);
            }

            HashSet<string> collections = configuration.Collections?.Length > 0 ? new HashSet<string>(configuration.Collections, StringComparer.OrdinalIgnoreCase) : null;

            var t = Database.Operations.AddOperation(
                Database,
                $"Enforce revision configuration in database '{Database.Name}'.",
                Operations.Operations.OperationType.EnforceRevisionConfiguration,
                onProgress => Database.DocumentsStorage.RevisionsStorage.EnforceConfigurationAsync(onProgress, configuration.IncludeForceCreated, collections, token),
                operationId,
                token: token);

            using (ServerStore.ContextPool.AllocateOperationContext(out JsonOperationContext context))
            await using (var writer = new AsyncBlittableJsonTextWriter(context, ResponseBodyStream()))
            {
                writer.WriteOperationIdAndNodeTag(context, operationId, ServerStore.NodeTag);
            }
        }

        [RavenAction("/databases/*/admin/revisions/orphaned/adopt", "POST", AuthorizationStatus.DatabaseAdmin)]
        public async Task AdoptOrphans()
        {
            var token = CreateTimeLimitedBackgroundOperationToken();
            var operationId = ServerStore.Operations.GetNextOperationId();

            var t = Database.Operations.AddOperation(
                Database,
                $"Adopting orphaned revisions in database '{Database.Name}'.",
                Operations.Operations.OperationType.AdoptOrphanedRevisions,
                onProgress => Database.DocumentsStorage.RevisionsStorage.AdoptOrphanedAsync(onProgress, token),
                operationId,
                token: token);

            using (ServerStore.ContextPool.AllocateOperationContext(out JsonOperationContext context))
            await using (var writer = new AsyncBlittableJsonTextWriter(context, ResponseBodyStream()))
            {
                writer.WriteOperationIdAndNodeTag(context, operationId, ServerStore.NodeTag);
            }
        }

        [RavenAction("/databases/*/revisions/count", "GET", AuthorizationStatus.ValidUser, EndpointType.Read)]
        public async Task GetRevisionsCountFor()
        {
            using (ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            using (context.OpenReadTransaction())
            {
                await GetRevisionsCount(context);
            }
        }

        [RavenAction("/databases/*/revisions", "GET", AuthorizationStatus.ValidUser, EndpointType.Read)]
        public async Task GetRevisionsFor()
        {
            using (ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            using (context.OpenReadTransaction())
            using (var token = CreateHttpRequestBoundOperationToken())
            {
                var changeVectors = GetStringValuesQueryString("changeVector", required: false);
                var metadataOnly = GetBoolValueQueryString("metadataOnly", required: false) ?? false;

                if (changeVectors.Count > 0)
                    await GetRevisionByChangeVector(context, changeVectors, metadataOnly, token.Token);
                else
                    await GetRevisions(context, metadataOnly, token.Token);
            }
        }

        [RavenAction("/databases/*/revisions/size", "POST", AuthorizationStatus.ValidUser, EndpointType.Read)]
        public async Task GetRevisionsSize()
        {
            List<RevisionSizeDetails> sizes;
            List<string> changeVectors;

            using (ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            {
                var json = await context.ReadForMemoryAsync(RequestBodyStream(), "ChangeVectors");
                var parameters = JsonDeserializationServer.Parameters.GetRevisionsSizeParameters(json);

                using (context.OpenReadTransaction())
                using (var token = CreateHttpRequestBoundOperationToken())
                {
                    changeVectors = parameters.ChangeVectors;
                    sizes = GetRevisionsSizeByChangeVector(context, changeVectors);
                }
            }

            using (ServerStore.ContextPool.AllocateOperationContext(out JsonOperationContext ctx))
            await using (var writer = new AsyncBlittableJsonTextWriter(ctx, ResponseBodyStream()))
            {
                writer.WriteStartObject();

                writer.WriteArray("Sizes", sizes.Select(size => size.ToJson()), ctx);

                writer.WriteEndObject();
            }
        }

        public class GetRevisionsSizeParameters
        {
            public List<string> ChangeVectors;
        }

        private List<RevisionSizeDetails> GetRevisionsSizeByChangeVector(DocumentsOperationContext context, List<string> changeVectors)
        {
            var revisionsStorage = Database.DocumentsStorage.RevisionsStorage;

            var sizes = new List<RevisionSizeDetails>(changeVectors.Count);

            foreach (var cv in changeVectors)
            {
                var metrics = revisionsStorage.GetRevisionMetrics(context, cv);

                var exist = metrics != null;
                if (exist == false)
                    metrics = (0, 0, false);

                sizes.Add(new RevisionSizeDetails
                {
                    ChangeVector = cv,
                    Exist = exist,
                    ActualSize = metrics.Value.ActualSize,
                    HumaneActualSize = Sizes.Humane(metrics.Value.ActualSize),
                    AllocatedSize = metrics.Value.AllocatedSize,
                    HumaneAllocatedSize = Sizes.Humane(metrics.Value.AllocatedSize),
                    IsCompressed = metrics.Value.IsCompressed
                });
            }

            return sizes;
        }


        [RavenAction("/databases/*/revisions/revert", "POST", AuthorizationStatus.ValidUser, EndpointType.Write, DisableOnCpuCreditsExhaustion = true)]
        public async Task Revert()
        {
            RevertRevisionsRequest configuration;

            using (ServerStore.ContextPool.AllocateOperationContext(out JsonOperationContext context))
            {
                var json = await context.ReadForMemoryAsync(RequestBodyStream(), "revisions/revert");

                configuration = JsonDeserializationServer.RevertRevisions(json);
            }
            
            HashSet<string> collections = configuration.Collections?.Length > 0 ? new HashSet<string>(configuration.Collections, StringComparer.OrdinalIgnoreCase) : null;

            var token = CreateTimeLimitedBackgroundOperationToken();
            var operationId = ServerStore.Operations.GetNextOperationId();

            var t = Database.Operations.AddOperation(
                Database,
                $"Revert database '{Database.Name}' to {configuration.Time} UTC.",
                Operations.Operations.OperationType.DatabaseRevert,
                onProgress => Database.DocumentsStorage.RevisionsStorage.RevertRevisions(configuration.Time, TimeSpan.FromSeconds(configuration.WindowInSec), onProgress, collections, token),
                operationId,
                token: token);

            using (ServerStore.ContextPool.AllocateOperationContext(out JsonOperationContext context))
            await using (var writer = new AsyncBlittableJsonTextWriter(context, ResponseBodyStream()))
            {
                writer.WriteOperationIdAndNodeTag(context, operationId, ServerStore.NodeTag);
            }
        }

        private async Task GetRevisionByChangeVector(DocumentsOperationContext context, Microsoft.Extensions.Primitives.StringValues changeVectors, bool metadataOnly, CancellationToken token)
        {
            var revisionsStorage = Database.DocumentsStorage.RevisionsStorage;
            var sw = Stopwatch.StartNew();

            var revisions = new List<Document>(changeVectors.Count);

            foreach (var changeVector in changeVectors)
            {
                var revision = revisionsStorage.GetRevision(context, changeVector);
                if (revision == null && changeVectors.Count == 1)
                {
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    return;
                }

                revisions.Add(revision);
            }

            var actualEtag = ComputeHttpEtags.ComputeEtagForRevisions(revisions);

            var etag = GetStringFromHeaders(Constants.Headers.IfNoneMatch);
            if (etag == actualEtag)
            {
                HttpContext.Response.StatusCode = (int)HttpStatusCode.NotModified;
                return;
            }

            HttpContext.Response.Headers[Constants.Headers.Etag] = "\"" + actualEtag + "\"";

            long numberOfResults;
            long totalDocumentsSizeInBytes;
            var blittable = GetBoolValueQueryString("blittable", required: false) ?? false;
            if (blittable)
            {
                WriteRevisionsBlittable(context, revisions, out numberOfResults, out totalDocumentsSizeInBytes);
            }
            else
            {
                (numberOfResults, totalDocumentsSizeInBytes) = await WriteRevisionsJsonAsync(context, metadataOnly, revisions, token);
            }

            AddPagingPerformanceHint(PagingOperationType.Documents, nameof(GetRevisionByChangeVector), HttpContext.Request.QueryString.Value, numberOfResults, revisions.Count, sw.ElapsedMilliseconds, totalDocumentsSizeInBytes);
        }

        private async Task<(long NumberOfResults, long TotalDocumentsSizeInBytes)> WriteRevisionsJsonAsync(JsonOperationContext context, bool metadataOnly, IEnumerable<Document> documentsToWrite, CancellationToken token)
        {
            long numberOfResults;
            long totalDocumentsSizeInBytes;
            await using (var writer = new AsyncBlittableJsonTextWriter(context, ResponseBodyStream()))
            {
                writer.WriteStartObject();
                writer.WritePropertyName(nameof(GetDocumentsResult.Results));
                (numberOfResults, totalDocumentsSizeInBytes) = await writer.WriteDocumentsAsync(context, documentsToWrite, metadataOnly, token);
                writer.WriteEndObject();
            }

            return (numberOfResults, totalDocumentsSizeInBytes);
        }

        private void WriteRevisionsBlittable(DocumentsOperationContext context, IEnumerable<Document> documentsToWrite, out long numberOfResults, out long totalDocumentsSizeInBytes)
        {
            numberOfResults = 0;
            totalDocumentsSizeInBytes = 0;
            HttpContext.Response.Headers["Content-Type"] = "binary/blittable-json";

            using (var streamBuffer = new UnmanagedStreamBuffer(context, ResponseBodyStream()))
            using (var writer = new ManualBlittableJsonDocumentBuilder<UnmanagedStreamBuffer>(context,
                null, new BlittableWriter<UnmanagedStreamBuffer>(context, streamBuffer)))
            {
                writer.StartWriteObjectDocument();

                writer.StartWriteObject();
                writer.WritePropertyName(nameof(GetDocumentsResult.Results));

                writer.StartWriteArray();
                foreach (var document in documentsToWrite)
                {
                    numberOfResults++;
                    writer.WriteEmbeddedBlittableDocument(document.Data);
                    totalDocumentsSizeInBytes += document.Data.Size;
                }
                writer.WriteArrayEnd();

                writer.WriteObjectEnd();

                writer.FinalizeDocument();
            }
        }

        private async Task GetRevisionsCount(DocumentsOperationContext documentContext)
        {
            var docId = GetQueryStringValueAndAssertIfSingleAndNotEmpty("id");

            var documentRevisionsDetails = new GetRevisionsCountOperation.DocumentRevisionsCount()
            {
                RevisionsCount = 0
            };

            documentRevisionsDetails.RevisionsCount = Database.DocumentsStorage.RevisionsStorage.GetRevisionsCount(documentContext, docId);

            await using (var writer = new AsyncBlittableJsonTextWriter(documentContext, ResponseBodyStream()))
            {
                documentContext.Write(writer, documentRevisionsDetails.ToJson());
            }
        }

        private async Task GetRevisions(DocumentsOperationContext context, bool metadataOnly, CancellationToken token)
        {
            var sw = Stopwatch.StartNew();

            var id = GetQueryStringValueAndAssertIfSingleAndNotEmpty("id");
            var before = GetDateTimeQueryString("before", required: false);
            var start = GetStart();
            var pageSize = GetPageSize();

            Document[] revisions = Array.Empty<Document>();
            long count = 0;
            if (before != null)
            {
                var revision = Database.DocumentsStorage.RevisionsStorage.GetRevisionBefore(context, id, before.Value);
                if (revision != null)
                {
                    count = 1;
                    revisions = new[] { revision };
                }
            }
            else
            {
                (revisions, count) = Database.DocumentsStorage.RevisionsStorage.GetRevisions(context, id, start, pageSize);
            }

            var actualChangeVector = revisions.Length == 0 ? "" : revisions[0].ChangeVector;

            if (GetStringFromHeaders(Constants.Headers.IfNoneMatch) == actualChangeVector)
            {
                HttpContext.Response.StatusCode = (int)HttpStatusCode.NotModified;
                return;
            }

            HttpContext.Response.Headers["ETag"] = "\"" + actualChangeVector + "\"";

            long loadedRevisionsCount;
            long totalDocumentsSizeInBytes;
            await using (var writer = new AsyncBlittableJsonTextWriter(context, ResponseBodyStream()))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("Results");
                (loadedRevisionsCount, totalDocumentsSizeInBytes) = await writer.WriteDocumentsAsync(context, revisions, metadataOnly, token);

                writer.WriteComma();

                writer.WritePropertyName("TotalResults");
                writer.WriteInteger(count);
                writer.WriteEndObject();
            }

            AddPagingPerformanceHint(PagingOperationType.Revisions, nameof(GetRevisions), HttpContext.Request.QueryString.Value, loadedRevisionsCount, pageSize, sw.ElapsedMilliseconds, totalDocumentsSizeInBytes);
        }

        [RavenAction("/databases/*/revisions/resolved", "GET", AuthorizationStatus.ValidUser, EndpointType.Read)]
        public async Task GetResolvedConflictsSince()
        {
            var since = GetStringQueryString("since", required: false);
            var take = GetIntValueQueryString("take", required: false) ?? 1024;
            var date = Convert.ToDateTime(since).ToUniversalTime();
            using (ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            using (context.OpenReadTransaction())
            using (var token = CreateHttpRequestBoundOperationToken())
            await using (var writer = new AsyncBlittableJsonTextWriter(context, ResponseBodyStream()))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("Results");
                var revisions = Database.DocumentsStorage.RevisionsStorage.GetResolvedDocumentsSince(context, date, take);
                await writer.WriteDocumentsAsync(context, revisions, metadataOnly: false, token.Token);
                writer.WriteEndObject();
            }
        }

        [RavenAction("/databases/*/revisions/bin", "GET", AuthorizationStatus.ValidUser, EndpointType.Read)]
        public async Task GetRevisionsBin()
        {
            var revisionsStorage = Database.DocumentsStorage.RevisionsStorage;

            var sw = Stopwatch.StartNew();
            var etag = GetLongQueryString("etag", false) ?? 0;
            var pageSize = GetPageSize();

            using (ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            using (context.OpenReadTransaction())
            {
                string match = null;
                revisionsStorage.GetLatestRevisionsBinEntry(context, out var actualChangeVector);
                
                if (actualChangeVector != null)
                {
                    var countRevs = revisionsStorage.GetNumberOfRevisionDocuments(context);
                    match = $"{actualChangeVector}/{countRevs}";
                    if (GetStringFromHeaders(Constants.Headers.IfNoneMatch) == match)
                    {
                        HttpContext.Response.StatusCode = (int)HttpStatusCode.NotModified;
                        return;
                    }
                }

                HttpContext.Response.Headers["ETag"] = "\"" + match + "\"";

                long count;
                long totalDocumentsSizeInBytes;

                using (var token = CreateHttpRequestBoundOperationToken())
                await using (var writer = new AsyncBlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("Results");

                    var revisions = revisionsStorage.GetRevisionsBinEntries(context, etag, pageSize);
                    (count, totalDocumentsSizeInBytes) = await writer.WriteDocumentsAsync(context, revisions, metadataOnly: false, token.Token);

                    writer.WriteEndObject();
                }

                AddPagingPerformanceHint(PagingOperationType.Revisions, nameof(GetRevisionsBin), HttpContext.Request.QueryString.Value, count, pageSize, sw.ElapsedMilliseconds, totalDocumentsSizeInBytes);
            }
        }
    }

    public sealed class RevisionSizeDetails : SizeDetails
    {
        public string ChangeVector { get; set; }

        public bool Exist { get; set; }

        public override DynamicJsonValue ToJson()
        {
            var json = base.ToJson();
            json[nameof(ChangeVector)] = ChangeVector;
            json[nameof(Exist)] = Exist;
            return json;
        }
    }
}
