﻿using System;
using System.Net.Http;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Indexes;
using Raven.Client.Http;
using Sparrow.Json;

namespace Raven.Client.Documents.Operations.Indexes
{
    /// <summary>
    /// Operation to retrieve performance statistics for some or all indexes.
    /// </summary>
    public sealed class GetIndexPerformanceStatisticsOperation : IMaintenanceOperation<IndexPerformanceStats[]>
    {
        private readonly string[] _indexNames;

        /// <summary>
        /// Operation to retrieve performance statistics for all indexes.
        /// </summary>
        public GetIndexPerformanceStatisticsOperation()
        {
        }

        /// <inheritdoc cref="GetIndexPerformanceStatisticsOperation"/>
        /// <param name="indexNames">An array of index names for which to retrieve performance statistics.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="indexNames"/> is null.</exception>
        public GetIndexPerformanceStatisticsOperation(string[] indexNames)
        {
            _indexNames = indexNames ?? throw new ArgumentNullException(nameof(indexNames));
        }

        public RavenCommand<IndexPerformanceStats[]> GetCommand(DocumentConventions conventions, JsonOperationContext context)
        {
            return new GetIndexPerformanceStatisticsCommand(_indexNames);
        }

        internal sealed class GetIndexPerformanceStatisticsCommand : RavenCommand<IndexPerformanceStats[]>
        {
            private readonly string[] _indexNames;

            public GetIndexPerformanceStatisticsCommand(string[] indexNames)
                : this(indexNames, nodeTag: null)
            {
            }

            internal GetIndexPerformanceStatisticsCommand(string[] indexNames, string nodeTag)
            {
                _indexNames = indexNames;
                SelectedNodeTag = nodeTag;
            }

            public override HttpRequestMessage CreateRequest(JsonOperationContext ctx, ServerNode node, out string url)
            {
                url = GetUrl(node);

                return new HttpRequestMessage
                {
                    Method = HttpMethod.Get
                };
            }

            public override void SetResponse(JsonOperationContext context, BlittableJsonReaderObject response, bool fromCache)
            {
                if (response == null ||
                    response.TryGet("Results", out BlittableJsonReaderArray results) == false)
                {
                    ThrowInvalidResponse();
                    return; // never hit
                }

                var stats = new IndexPerformanceStats[results.Length];
                for (var i = 0; i < results.Length; i++)
                {
                    stats[i] = DocumentConventions.Default.Serialization.DefaultConverter.FromBlittable<IndexPerformanceStats>((BlittableJsonReaderObject)results[i]);
                }

                Result = stats;
            }

            private string GetUrl(ServerNode node)
            {
                var url = $"{node.Url}/databases/{node.Database}/indexes/performance";

                if (_indexNames is { Length: > 0 })
                {
                    url += "?";
                    foreach (var indexName in _indexNames)
                    {
                        url += $"&name={Uri.EscapeDataString(indexName)}";
                    }
                }

                return url;
            }

            public override bool IsReadRequest => true;
        }
    }
}
