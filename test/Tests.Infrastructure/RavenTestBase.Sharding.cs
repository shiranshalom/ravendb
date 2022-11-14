﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Attachments;
using Raven.Client.Documents.Operations.Backups;
using Raven.Client.Documents.Operations.CompareExchange;
using Raven.Client.Documents.Operations.Identities;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Operations.Revisions;
using Raven.Client.Documents.Session;
using Raven.Client.Documents.Subscriptions;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Raven.Client.ServerWide.Sharding;
using Raven.Client.Util;
using Raven.Server;
using Raven.Server.Documents;
using Raven.Server.Documents.PeriodicBackup;
using Raven.Server.Documents.Sharding.Handlers;
using Raven.Server.Documents.Sharding.Handlers.Processors.OngoingTasks;
using Raven.Server.Documents.Sharding;
using Raven.Server.Json;
using Raven.Server.ServerWide.Context;
using Raven.Server.Utils;
using Raven.Server.Web;
using Raven.Tests.Core.Utils.Entities;
using Sparrow.Json;
using Tests.Infrastructure;
using Xunit;

namespace FastTests;

public partial class RavenTestBase
{
    public readonly ShardingTestBase Sharding;

    public class ShardingTestBase
    {
        public ShardedBackupTestsBase Backup;

        private readonly RavenTestBase _parent;
        public readonly ReshardingTestBase Resharding;

        public ShardingTestBase(RavenTestBase parent)
        {
            _parent = parent ?? throw new ArgumentNullException(nameof(parent));
            Backup = new ShardedBackupTestsBase(_parent);
            Resharding = new ReshardingTestBase(_parent);
        }

        public DocumentStore GetDocumentStore(Options options = null, [CallerMemberName] string caller = null, DatabaseTopology[] shards = null)
        {
            var shardedOptions = options ?? new Options();
            shardedOptions.ModifyDatabaseRecord += r =>
            {
                r.Sharding ??= new ShardingConfiguration();

                if (shards == null)
                {
                    r.Sharding.Shards = new[]
                    {
                        new DatabaseTopology(),
                        new DatabaseTopology(),
                        new DatabaseTopology(),
                    };
                }
                else
                {
                    r.Sharding.Shards = shards;
                }
            };
            return _parent.GetDocumentStore(shardedOptions, caller);
        }

        public Options GetOptionsForCluster(RavenServer leader, int shards, int shardReplicationFactor, int orchestratorReplicationFactor)
        {
            var options = new Options
            {
                ModifyDatabaseRecord = r =>
                {
                    r.Sharding = new ShardingConfiguration
                    {
                        Shards = new DatabaseTopology[shards],
                        Orchestrator = new OrchestratorConfiguration
                        {
                            Topology = new OrchestratorTopology
                            {
                                ReplicationFactor = orchestratorReplicationFactor
                            }
                        }
                    };

                    for (int i = 0; i < r.Sharding.Shards.Length; i++)
                    {
                        r.Sharding.Shards[i] = new DatabaseTopology
                        {
                            ReplicationFactor = shardReplicationFactor
                        };
                    }
                },
                Server = leader
            };

            return options;
        }

        public async Task<ShardingConfiguration> GetShardingConfigurationAsync(IDocumentStore store)
        {
            var record = await store.Maintenance.Server.SendAsync(new GetDatabaseRecordOperation(store.Database));
            return record.Sharding;
        }

        public async Task<int> GetShardNumber(IDocumentStore store, string id)
        {
            var record = await store.Maintenance.Server.SendAsync(new GetDatabaseRecordOperation(store.Database));
            var bucket = ShardHelper.GetBucket(id);
            return ShardHelper.GetShardNumber(record.Sharding.BucketRanges, bucket);
        }

        public async Task<ShardedDocumentDatabase> GetShardedDocumentDatabaseForBucketAsync(string database, int bucket)
        {
            using (_parent.Server.ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            using (context.OpenReadTransaction())
            {
                var config = _parent.Server.ServerStore.Cluster.ReadShardingConfiguration(context, database);
                var shardNumber = ShardHelper.GetShardNumber(config.BucketRanges, bucket);
                var shardedName = ShardHelper.ToShardName(database, shardNumber);
                var shardedDatabase = (await _parent.Server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(shardedName)) as ShardedDocumentDatabase;
                Assert.NotNull(shardedDatabase);
                return shardedDatabase;

            }
        }

        public IAsyncEnumerable<ShardedDocumentDatabase> GetShardsDocumentDatabaseInstancesFor(IDocumentStore store, List<RavenServer> servers = null)
        {
            return GetShardsDocumentDatabaseInstancesFor(store.Database, servers);
        }

        public async IAsyncEnumerable<ShardedDocumentDatabase> GetShardsDocumentDatabaseInstancesFor(string database, List<RavenServer> servers = null)
        {
            servers ??= _parent.GetServers();
            foreach (var server in servers)
            {
                foreach (var task in server.ServerStore.DatabasesLandlord.TryGetOrCreateShardedResourcesStore(database))
                {
                    var databaseInstance = await task;
                    Debug.Assert(databaseInstance != null, $"The requested database '{database}' is null, probably you try to loaded sharded database without the $");
                    yield return databaseInstance;
                }
            }
        }

        public bool AllShardHaveDocs(IDictionary<string, List<DocumentDatabase>> servers, long count = 1L)
        {
            foreach (var kvp in servers)
            {
                foreach (var documentDatabase in kvp.Value)
                {
                    using (documentDatabase.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
                    using (context.OpenReadTransaction())
                    {
                        var ids = documentDatabase.DocumentsStorage.GetNumberOfDocuments(context);
                        if (ids < count)
                            return false;
                    }
                }
            }

            return true;
        }

        public async Task<bool> AllShardHaveDocsAsync(RavenServer server, string databaseName, long count = 1L)
        {
            var databases = server.ServerStore.DatabasesLandlord.TryGetOrCreateShardedResourcesStore(databaseName);
            foreach (var task in databases)
            {
                var documentDatabase = await task;
                using (documentDatabase.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
                using (context.OpenReadTransaction())
                {
                    var ids = documentDatabase.DocumentsStorage.GetNumberOfDocuments(context);
                    if (ids < count)
                        return false;
                }
            }

            return true;
        }

        public async Task<Dictionary<int, string>> GetOneDocIdForEachShardAsync(RavenServer server, string databaseName)
        {
            var docIdPerShard = new Dictionary<int, string>();
            var databases = server.ServerStore.DatabasesLandlord.TryGetOrCreateShardedResourcesStore(databaseName);
            foreach (var task in databases)
            {
                var documentDatabase = await task;
                using (documentDatabase.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
                using (context.OpenReadTransaction())
                {
                    var ids = documentDatabase.DocumentsStorage.GetNumberOfDocuments(context);
                    if (ids < 1)
                        return null;

                    var randomId = documentDatabase.DocumentsStorage.GetAllIds(context).FirstOrDefault();
                    if (randomId == null)
                        return null;

                    docIdPerShard.Add(documentDatabase.ShardNumber, randomId);
                }
            }

            return docIdPerShard;
        }

        public long GetDocsCountForCollectionInAllShards(IDictionary<string, List<DocumentDatabase>> servers, string collection)
        {
            var sum = 0L;
            foreach (var kvp in servers)
            {
                foreach (var documentDatabase in kvp.Value)
                {
                    using (documentDatabase.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
                    using (context.OpenReadTransaction())
                    {
                        var ids = documentDatabase.DocumentsStorage.GetCollectionDetails(context, collection).CountOfDocuments;
                        sum += ids;
                    }
                }
            }

            return sum;
        }

        internal async Task<ShardedOngoingTasksHandlerProcessorForGetOngoingTasks> InstantiateShardedOutgoingTaskProcessor(string name, RavenServer server)
        {
            Assert.True(server.ServerStore.DatabasesLandlord.ShardedDatabasesCache.TryGetValue(name, out var db));
            var database = await db;
            var handler = new ShardedOngoingTasksHandler();
            var ctx = new RequestHandlerContext
            {
                RavenServer = server,
                DatabaseContext = database,
                HttpContext = new DefaultHttpContext()
            };
            handler.InitForOfflineOperation(ctx);
            return new ShardedOngoingTasksHandlerProcessorForGetOngoingTasks(handler);
        }

        public class ShardedBackupTestsBase
        {
            internal readonly RavenTestBase _parent;

            public ShardedBackupTestsBase(RavenTestBase parent)
            {
                _parent = parent ?? throw new ArgumentNullException(nameof(parent));
            }

            public async Task InsertData(IDocumentStore store)
            {
                using (var session = store.OpenAsyncSession())
                {
                    await SetupRevisionsAsync(store, configuration: new RevisionsConfiguration
                    {
                        Default = new RevisionsCollectionConfiguration
                        {
                            Disabled = false
                        }
                    });

                    //Docs
                    await session.StoreAsync(new User { Name = "Name1", LastName = "LastName1", Age = 5 }, "users/1");
                    await session.StoreAsync(new User { Name = "Name2", LastName = "LastName2", Age = 78 }, "users/2");
                    await session.StoreAsync(new User { Name = "Name1", LastName = "LastName3", Age = 4 }, "users/3");
                    await session.StoreAsync(new User { Name = "Name2", LastName = "LastName4", Age = 15 }, "users/4");

                    //Time series
                    session.TimeSeriesFor("users/1", "Heartrate")
                        .Append(DateTime.Now, 59d, "watches/fitbit");
                    session.TimeSeriesFor("users/3", "Heartrate")
                        .Append(DateTime.Now.AddHours(6), 59d, "watches/fitbit");
                    //counters
                    session.CountersFor("users/2").Increment("Downloads", 100);
                    //Attachments
                    var names = new[]
                    {
                        "background-photo.jpg",
                        "fileNAME_#$1^%_בעברית.txt",
                        "profile.png",
                    };
                    await using (var profileStream = new MemoryStream(new byte[] { 1, 2, 3 }))
                    await using (var backgroundStream = new MemoryStream(new byte[] { 10, 20, 30, 40, 50 }))
                    await using (var fileStream = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 }))
                    {
                        session.Advanced.Attachments.Store("users/1", names[0], backgroundStream, "ImGgE/jPeG");
                        session.Advanced.Attachments.Store("users/2", names[1], fileStream);
                        session.Advanced.Attachments.Store("users/3", names[2], profileStream, "image/png");
                        await session.SaveChangesAsync();
                    }
                }

                //tombstone + revision
                using (var session = store.OpenAsyncSession())
                {
                    session.Delete("users/4");
                    var user = await session.LoadAsync<User>("users/1");
                    user.Age = 10;
                    await session.SaveChangesAsync();
                }

                //subscription
                await store.Subscriptions.CreateAsync(new SubscriptionCreationOptions<User>());

                //Identity
                store.Maintenance.Send(new SeedIdentityForOperation("users", 1990));

                //CompareExchange
                var user1 = new User
                {
                    Name = "Toli"
                };
                var user2 = new User
                {
                    Name = "Mitzi"
                };

                await store.Operations.SendAsync(new PutCompareExchangeValueOperation<User>("cat/toli", user1, 0));
                var operationResult = await store.Operations.SendAsync(new PutCompareExchangeValueOperation<User>("cat/mitzi", user2, 0));
                await store.Operations.SendAsync(new DeleteCompareExchangeValueOperation<User>("cat/mitzi", operationResult.Index));

                //Cluster transaction
                using (var session = store.OpenAsyncSession(new SessionOptions
                {
                    TransactionMode = TransactionMode.ClusterWide
                }))
                {
                    var user5 = new User { Name = "Ayende" };
                    await session.StoreAsync(user5, "users/5");
                    await session.StoreAsync(new { ReservedFor = user5.Id }, "usernames/" + user5.Name);

                    await session.SaveChangesAsync();
                }

                //Index
                await new Index().ExecuteAsync(store);
            }

            public async Task CheckData(IDocumentStore store, RavenDatabaseMode dbMode = RavenDatabaseMode.Single, long expectedRevisionsCount = 28, string database = null)
            {
                long docsCount = default, tombstonesCount = default, revisionsCount = default;
                database ??= store.Database;
                if (dbMode == RavenDatabaseMode.Sharded)
                {
                    await foreach (var shard in _parent.Sharding.GetShardsDocumentDatabaseInstancesFor(database))
                    {
                        var storage = shard.DocumentsStorage;

                        docsCount += storage.GetNumberOfDocuments();
                        using (storage.ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
                        using (context.OpenReadTransaction())
                        {
                            tombstonesCount += storage.GetNumberOfTombstones(context);
                            revisionsCount += storage.RevisionsStorage.GetNumberOfRevisionDocuments(context);
                        }

                        //Index
                        Assert.Equal(1, shard.IndexStore.Count);
                    }
                }
                else
                {
                    var db = await _parent.GetDocumentDatabaseInstanceFor(store, database);
                    var storage = db.DocumentsStorage;

                    docsCount = storage.GetNumberOfDocuments();
                    using (storage.ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
                    using (context.OpenReadTransaction())
                    {
                        tombstonesCount = storage.GetNumberOfTombstones(context);
                        revisionsCount = storage.RevisionsStorage.GetNumberOfRevisionDocuments(context);
                    }

                    //Index
                    var indexes = await store.Maintenance.ForDatabase(database).SendAsync(new GetIndexesOperation(0, 128));
                    Assert.Equal(1, indexes.Length);
                }

                //doc
                Assert.Equal(5, docsCount);

                //Assert.Equal(1, detailedStats.CountOfCompareExchangeTombstones); //TODO - test number of processed compare exchange tombstones  

                //tombstone
                Assert.Equal(1, tombstonesCount);

                //revisions
                Assert.Equal(expectedRevisionsCount, revisionsCount);

                //Subscriptions
                var subscriptionDocuments = await store.Subscriptions.GetSubscriptionsAsync(0, 10, database);
                Assert.Equal(1, subscriptionDocuments.Count);

                using (var session = store.OpenSession(database))
                {
                    //Time series
                    var val = session.TimeSeriesFor("users/1", "Heartrate")
                        .Get(DateTime.MinValue, DateTime.MaxValue);

                    Assert.Equal(1, val.Length);

                    val = session.TimeSeriesFor("users/3", "Heartrate")
                        .Get(DateTime.MinValue, DateTime.MaxValue);

                    Assert.Equal(1, val.Length);

                    //Counters
                    var counterValue = session.CountersFor("users/2").Get("Downloads");
                    Assert.Equal(100, counterValue.Value);
                }

                using (var session = store.OpenAsyncSession(database))
                {
                    var attachmentNames = new[]
                    {
                        "background-photo.jpg",
                        "fileNAME_#$1^%_בעברית.txt",
                        "profile.png",
                    };

                    for (var i = 0; i < attachmentNames.Length; i++)
                    {
                        var user = await session.LoadAsync<User>("users/" + (i + 1));
                        var metadata = session.Advanced.GetMetadataFor(user);

                        //Attachment
                        var attachments = metadata.GetObjects(Constants.Documents.Metadata.Attachments);
                        Assert.Equal(1, attachments.Length);
                        var attachment = attachments[0];
                        Assert.Equal(attachmentNames[i], attachment.GetString(nameof(AttachmentName.Name)));
                        var hash = attachment.GetString(nameof(AttachmentName.Hash));
                        if (i == 0)
                        {
                            Assert.Equal("igkD5aEdkdAsAB/VpYm1uFlfZIP9M2LSUsD6f6RVW9U=", hash);
                            Assert.Equal(5, attachment.GetLong(nameof(AttachmentName.Size)));
                        }
                        else if (i == 1)
                        {
                            Assert.Equal("Arg5SgIJzdjSTeY6LYtQHlyNiTPmvBLHbr/Cypggeco=", hash);
                            Assert.Equal(5, attachment.GetLong(nameof(AttachmentName.Size)));
                        }
                        else if (i == 2)
                        {
                            Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", hash);
                            Assert.Equal(3, attachment.GetLong(nameof(AttachmentName.Size)));
                        }
                    }

                    await session.StoreAsync(new User() { Name = "Toli" }, "users|");
                    await session.SaveChangesAsync();
                }
                //Identity
                using (var session = store.OpenAsyncSession(database))
                {
                    var user = await session.LoadAsync<User>("users/1991");
                    Assert.NotNull(user);
                }
                //CompareExchange
                using (var session = store.OpenAsyncSession(new SessionOptions { Database = database, TransactionMode = TransactionMode.ClusterWide }))
                {
                    var user = await session.Advanced.ClusterTransaction.GetCompareExchangeValueAsync<User>("cat/toli");
                    Assert.NotNull(user);

                    user = await session.Advanced.ClusterTransaction.GetCompareExchangeValueAsync<User>("rvn-atomic/usernames/Ayende");
                    Assert.NotNull(user);

                    user = await session.Advanced.ClusterTransaction.GetCompareExchangeValueAsync<User>("rvn-atomic/users/5");
                    Assert.NotNull(user);

                    var user2 = await session.LoadAsync<User>("users/5");
                    Assert.NotNull(user2);

                    user2 = await session.LoadAsync<User>("usernames/Ayende");
                    Assert.NotNull(user2);
                }
            }

            public Task<WaitHandle[]> WaitForBackupToComplete(IDocumentStore store)
            {
                return WaitForBackupsToComplete(new[] { store });
            }

            public async Task<WaitHandle[]> WaitForBackupsToComplete(IEnumerable<IDocumentStore> stores)
            {
                var waitHandles = new List<WaitHandle>();
                foreach (var store in stores)
                {
                    await foreach (var db in _parent.Sharding.GetShardsDocumentDatabaseInstancesFor(store))
                    {
                        BackupTestBase.FillBackupCompletionHandles(waitHandles, db);
                    }
                }

                return waitHandles.ToArray();
            }

            public async Task<WaitHandle[]> WaitForBackupsToComplete(List<RavenServer> nodes, string database)
            {
                var waitHandles = new List<WaitHandle>();

                await foreach (var db in _parent.Sharding.GetShardsDocumentDatabaseInstancesFor(database, nodes))
                {
                    BackupTestBase.FillBackupCompletionHandles(waitHandles, db);
                }

                return waitHandles.ToArray();
            }

            public Task<long> UpdateConfigurationAndRunBackupAsync(RavenServer server, IDocumentStore store, PeriodicBackupConfiguration config, bool isFullBackup = false)
            {
                return UpdateConfigurationAndRunBackupAsync(new List<RavenServer> { server }, store, config, isFullBackup);
            }

            public async Task<long> UpdateConfigurationAndRunBackupAsync(List<RavenServer> servers, IDocumentStore store, PeriodicBackupConfiguration config, bool isFullBackup = false)
            {
                var result = await store.Maintenance.SendAsync(new UpdatePeriodicBackupOperation(config));

                await RunBackupAsync(store.Database, result.TaskId, isFullBackup, servers);

                return result.TaskId;
            }

            public async Task RunBackupAsync(string database, long taskId, bool isFullBackup, List<RavenServer> servers = null)
            {
                await foreach (var documentDatabase in _parent.Sharding.GetShardsDocumentDatabaseInstancesFor(database, servers))
                {
                    var periodicBackupRunner = documentDatabase.PeriodicBackupRunner;
                    periodicBackupRunner.StartBackupTask(taskId, isFullBackup);
                }
            }


            public IDisposable ReadOnly(string path)
            {
                var allFiles = new List<string>();
                var dirs = Directory.GetDirectories(path);
                FileAttributes attributes = default;
                foreach (string dir in dirs)
                {
                    var files = Directory.GetFiles(dir);
                    if (attributes != default)
                        attributes = new FileInfo(files[0]).Attributes;

                    foreach (string file in files)
                    {
                        File.SetAttributes(file, FileAttributes.ReadOnly);
                    }

                    allFiles.AddRange(files);
                }


                return new DisposableAction(() =>
                {
                    foreach (string file in allFiles)
                    {
                        File.SetAttributes(file, attributes);
                    }
                });
            }

            private static async Task<long> SetupRevisionsAsync(
                IDocumentStore store,
                RevisionsConfiguration configuration)
            {
                if (store == null)
                    throw new ArgumentNullException(nameof(store));

                var result = await store.Maintenance.ForDatabase(store.Database).SendAsync(new ConfigureRevisionsOperation(configuration));
                return result.RaftCommandIndex ?? -1;
            }

            private class Item
            {

            }

            private class Index : AbstractIndexCreationTask<Item>
            {
                public Index()
                {
                    Map = items =>
                        from item in items
                        select new
                        {
                            _ = new[]
                            {
                                CreateField("foo", "a"),
                                CreateField("foo", "b"),
                            }
                        };
                }
            }
        }

        public class ReshardingTestBase
        {
            private readonly RavenTestBase _parent;

            public ReshardingTestBase(RavenTestBase parent)
            {
                _parent = parent;
            }

            public async Task StartMovingShardForId(IDocumentStore store, string id, List<RavenServer> servers = null)
            {
                servers ??= _parent.GetServers();

                var server = servers[0].ServerStore;

                var record = await store.Maintenance.Server.SendAsync(new GetDatabaseRecordOperation(store.Database));
                var bucket = ShardHelper.GetBucket(id);
                var location = ShardHelper.GetShardNumber(record.Sharding.BucketRanges, bucket);
                var newLocation = (location + 1) % record.Sharding.Shards.Length;

                using (var session = store.OpenAsyncSession(ShardHelper.ToShardName(store.Database, location)))
                {
                    var user = await session.Advanced.ExistsAsync(id);
                    Assert.NotNull(user);
                }

                await server.Sharding.StartBucketMigration(store.Database, bucket, location, newLocation);

                var exists = _parent.WaitForDocument<dynamic>(store, id, predicate: null, database: ShardHelper.ToShardName(store.Database, newLocation));
                Assert.True(exists, $"{id} wasn't found at shard {newLocation}");
            }

            public async Task WaitForMigrationComplete(IDocumentStore store, string id)
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                {
                    var bucket = ShardHelper.GetBucket(id);
                    var record = await store.Maintenance.Server.SendAsync(new GetDatabaseRecordOperation(store.Database), cts.Token);
                    while (record.Sharding.BucketMigrations.ContainsKey(bucket))
                    {
                        await Task.Delay(250, cts.Token);
                        record = await store.Maintenance.Server.SendAsync(new GetDatabaseRecordOperation(store.Database), cts.Token);
                    }
                }
            }

            public async Task MoveShardForId(IDocumentStore store, string id, List<RavenServer> servers = null)
            {
                try
                {
                    await StartMovingShardForId(store, id, servers);
                    await WaitForMigrationComplete(store, id);
                }
                catch (Exception e)
                {
                    var record = await store.Maintenance.Server.SendAsync(new GetDatabaseRecordOperation(store.Database));
                    using (var ctx = JsonOperationContext.ShortTermSingleUse())
                    {
                        var sharding = store.Conventions.Serialization.DefaultConverter.ToBlittable(record.Sharding, ctx).ToString();
                        throw new InvalidOperationException(
                            $"Failed to completed the migration for {id}{Environment.NewLine}{sharding}{Environment.NewLine}{_parent.Cluster.CollectLogsFromNodes(servers ?? new List<RavenServer> { _parent.Server })}",
                            e);
                    }
                }
            }
        }
    }
}

