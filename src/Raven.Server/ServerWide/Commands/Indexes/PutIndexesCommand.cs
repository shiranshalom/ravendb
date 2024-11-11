using System;
using System.Collections.Generic;
using System.Linq;
using Raven.Client.Documents.Indexes;
using Raven.Client.ServerWide;
using Raven.Server.Utils;
using Sparrow.Json.Parsing;
using Raven.Server.Rachis;
using Raven.Server.Documents.Indexes;

namespace Raven.Server.ServerWide.Commands.Indexes
{
    public sealed class PutIndexesCommand : UpdateDatabaseCommand
    {
        public List<IndexDefinition> Static = new List<IndexDefinition>();

        public List<AutoIndexDefinition> Auto = new List<AutoIndexDefinition>();

        public IndexDeploymentMode? DefaultStaticDeploymentMode;

        public IndexDeploymentMode? DefaultAutoDeploymentMode;

        public DateTime CreatedAt { get; set; }

        public string Source { get; set; }
        
        public int RevisionsToKeep { get; set; }

        public PutIndexesCommand()
        {
            // for deserialization
        }

        public PutIndexesCommand(string databaseName, string source, DateTime createdAt, string uniqueRequestId, int revisionsToKeep, IndexDeploymentMode autoDeploymentMode, IndexDeploymentMode staticDeploymentMode)
            : base(databaseName, uniqueRequestId)
        {
            Source = source;
            CreatedAt = createdAt;
            RevisionsToKeep = revisionsToKeep;
            DefaultAutoDeploymentMode = autoDeploymentMode;
            DefaultStaticDeploymentMode = staticDeploymentMode;
        }

        public override void UpdateDatabaseRecord(DatabaseRecord record, long etag)
        {
            if (Static != null)
            {
                HashSet<string> indexNames = null;
                HashSet<string> safeFileSystemIndexNames = null;

                foreach (var definition in Static)
                {
                    if (record.Indexes.TryGetValue(definition.Name, out _) == false)
                    {
                        // this is not an update to an existing index. we'll check for:
                        // - directory name collisions
                        // - index name case sensitivity

                        safeFileSystemIndexNames ??= record.Indexes.Select(x => IndexDefinitionBaseServerSide.GetIndexNameSafeForFileSystem(x.Value.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        indexNames ??= record.Indexes.Select(x => x.Value.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

                        var safeFileSystemIndexName = IndexDefinitionBaseServerSide.GetIndexNameSafeForFileSystem(definition.Name);
                        if (safeFileSystemIndexNames.Add(safeFileSystemIndexName) == false)
                        {
                            var existingIndexName = indexNames.FirstOrDefault(x =>
                                x.Equals(definition.Name, StringComparison.OrdinalIgnoreCase) == false &&
                                safeFileSystemIndexName.Equals(IndexDefinitionBaseServerSide.GetIndexNameSafeForFileSystem(x), StringComparison.OrdinalIgnoreCase));

                            throw new RachisApplyException(
                                $"Could not create index '{definition.Name}' because it would result in directory name collision with '{existingIndexName}' index");
                        }

                        if (indexNames.Add(definition.Name) == false && record.Indexes.TryGetValue(definition.Name, out _) == false)
                        {
                            throw new RachisApplyException($"Can not add index: {definition.Name} because an index with the same name but different casing already exist");
                        }
                    }

                    record.AddIndex(definition, Source, CreatedAt, etag, RevisionsToKeep, DefaultStaticDeploymentMode ?? IndexDeploymentMode.Parallel);
                }
            }

            if (Auto != null)
            {
                foreach (var definition in Auto)
                    record.AddIndex(definition, CreatedAt, etag, DefaultAutoDeploymentMode ?? IndexDeploymentMode.Parallel);
            }
        }

        public override void FillJson(DynamicJsonValue json)
        {
            json[nameof(Static)] = TypeConverter.ToBlittableSupportedType(Static);
            json[nameof(Auto)] = TypeConverter.ToBlittableSupportedType(Auto);
            json[nameof(Source)] = Source;
            json[nameof(CreatedAt)] = CreatedAt;
            json[nameof(RevisionsToKeep)] = RevisionsToKeep;
            json[nameof(DefaultStaticDeploymentMode)] = DefaultStaticDeploymentMode;
            json[nameof(DefaultAutoDeploymentMode)] = DefaultAutoDeploymentMode;
        }
    }
}
