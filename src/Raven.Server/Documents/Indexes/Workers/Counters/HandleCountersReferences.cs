﻿using System;
using System.Collections.Generic;
using Raven.Client.Documents.Indexes;
using Raven.Server.Documents.Indexes.Persistence;
using Raven.Server.ServerWide.Context;
using Sparrow.Server.Utils;
using Voron;

namespace Raven.Server.Documents.Indexes.Workers.Counters
{
    public sealed class HandleCountersReferences : HandleReferences
    {
        private readonly CountersStorage _countersStorage;

        public HandleCountersReferences(Index index, Dictionary<string, HashSet<CollectionName>> referencedCollections, CountersStorage countersStorage, DocumentsStorage documentsStorage, IndexStorage indexStorage, Config.Categories.IndexingConfiguration configuration)
            : base(index, referencedCollections, documentsStorage, indexStorage, indexStorage.ReferencesForDocuments, configuration)
        {
            _countersStorage = countersStorage;
        }

        protected override IndexItem GetItem(DocumentsOperationContext databaseContext, Slice key)
        {
            var counter = _countersStorage.Indexing.GetCountersMetadata(databaseContext, key);

            if (counter == null)
                return null;

            return new CounterIndexItem(counter.LuceneKey, counter.DocumentId, counter.Etag, counter.CounterName, counter.Size, counter);
        }

        protected override string GetNextItemId(IndexItem indexItem)
        {
            return indexItem.LowerSourceDocumentId;
        }

        public override void HandleDelete(Tombstone tombstone, string collection, Lazy<IndexWriteOperationBase> writer, TransactionOperationContext indexContext, IndexingStatsScope stats)
        {
            using (DocumentIdWorker.GetSliceFromId(indexContext, tombstone.LowerId, out Slice documentIdPrefixWithTsKeySeparator, SpecialChars.RecordSeparator))
            using (stats.For(IndexingOperation.Storage.UpdateReferences))
                _referencesStorage.RemoveReferencesByPrefix(documentIdPrefixWithTsKeySeparator, collection, null, indexContext.Transaction);
        }
    }
}
