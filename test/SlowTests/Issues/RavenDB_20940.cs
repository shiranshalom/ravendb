using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FastTests;
using FastTests.Server.Replication;
using Raven.Client.Documents;
using Raven.Client.Documents.Commands;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Queries;
using Raven.Client.Documents.Replication;
using Raven.Client.Documents.Session;
using SlowTests.Core.Utils.Entities;
using Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Issues
{
    public class RavenDB_20940 : ReplicationTestBase
    {
        public RavenDB_20940(ITestOutputHelper output) : base(output)
        {
        }

        [RavenTheory(RavenTestCategory.Replication | RavenTestCategory.Indexes)]
        [RavenData(DatabaseMode = RavenDatabaseMode.All)]
        public async Task ResolveConflictOfDifferentCollectionShouldUpdateIndex(Options options)
        {
            using (var store1 = GetDocumentStore(options))
            using (var store2 = GetDocumentStore(options))
            {
                using (var session = store1.OpenSession())
                {
                    session.Store(new User { Name = "Karmel" }, "foo/bar");
                    session.SaveChanges();
                }

                using (var session = store2.OpenSession())
                {
                    session.Store(new Company { Name = "Karmel" }, "foo/bar");
                    session.SaveChanges();
                }

                await new MapReduce().ExecuteAsync(store1);
                await new MapReduce2().ExecuteAsync(store2);

                Indexes.WaitForIndexing(store1);
                Indexes.WaitForIndexing(store2);

                await AssertIndexEntries(store1, expectedEntries: 1);
                await AssertIndexEntries(store2, expectedEntries: 1);

                await SetupReplicationAsync(store1, store2);
                await EnsureReplicatingAsync(store1, store2);

                await SetupReplicationAsync(store2, store1);
                await EnsureReplicatingAsync(store2, store1);

                using (var session = store1.OpenSession())
                {
                    session.Delete("foo/bar");
                    session.SaveChanges();
                }

                await EnsureReplicatingAsync(store1, store2);

                await EnsureNoReplicationLoop(Server, store1.Database);
                await EnsureNoReplicationLoop(Server, store2.Database);

                Indexes.WaitForIndexing(store1);
                Indexes.WaitForIndexing(store2);

                await AssertIndexEntries(store1, expectedEntries: 0);
                await AssertIndexEntries(store2, expectedEntries: 0);
            }
        }

        [RavenTheory(RavenTestCategory.Replication | RavenTestCategory.Indexes)]
        [RavenData(DatabaseMode = RavenDatabaseMode.All)]
        public async Task ConflictShouldUpdateMapReduceIndex(Options options)
        {
            using (var store1 = GetDocumentStore(options))
            using (var store2 = GetDocumentStore(options))
            {
                await UpdateConflictResolver(store1, resolveToLatest: false);
                await UpdateConflictResolver(store2, resolveToLatest: false);

                using (var session = store1.OpenSession())
                {
                    session.Store(new User { Name = "Karmel" }, "foo/bar");
                    session.SaveChanges();
                }

                using (var session = store2.OpenSession())
                {
                    session.Store(new User { Name = "Karmel2" }, "foo/bar");
                    session.SaveChanges();
                }

                await new MapReduce().ExecuteAsync(store1);
                await new MapReduce().ExecuteAsync(store2);

                Indexes.WaitForIndexing(store1);
                Indexes.WaitForIndexing(store2);

                await AssertIndexEntries(store1, expectedEntries: 1);
                await AssertIndexEntries(store2, expectedEntries: 1);

                await SetupReplicationAsync(store1, store2);
                await EnsureReplicatingAsync(store1, store2);

                await SetupReplicationAsync(store2, store1);
                await EnsureReplicatingAsync(store2, store1);

                await EnsureNoReplicationLoop(Server, store1.Database);
                await EnsureNoReplicationLoop(Server, store2.Database);

                Indexes.WaitForIndexing(store1);
                Indexes.WaitForIndexing(store2);

                await AssertIndexEntries(store1, expectedEntries: 0);
                await AssertIndexEntries(store2, expectedEntries: 0);
            }
        }

        [RavenTheory(RavenTestCategory.Replication | RavenTestCategory.Indexes)]
        [RavenData(DatabaseMode = RavenDatabaseMode.All)]
        public async Task ConflictShouldUpdateMapIndex(Options options)
        {
            using (var store1 = GetDocumentStore(options))
            using (var store2 = GetDocumentStore(options))
            {
                await UpdateConflictResolver(store1, resolveToLatest: false);
                await UpdateConflictResolver(store2, resolveToLatest: false);

                using (var session = store1.OpenSession())
                {
                    session.Store(new User { Name = "Karmel" }, "foo/bar");
                    session.SaveChanges();
                }

                using (var session = store2.OpenSession())
                {
                    session.Store(new User { Name = "Karmel2" }, "foo/bar");
                    session.SaveChanges();
                }

                await new Map().ExecuteAsync(store1);
                await new Map().ExecuteAsync(store2);

                Indexes.WaitForIndexing(store1);
                Indexes.WaitForIndexing(store2);

                AssertIndexEntries2(store1, expectedEntries: 1);
                AssertIndexEntries2(store2, expectedEntries: 1);

                await AssertIndexEntries(store1, expectedEntries: 1);
                await AssertIndexEntries(store2, expectedEntries: 1);

                await SetupReplicationAsync(store1, store2);
                await EnsureReplicatingAsync(store1, store2);

                await SetupReplicationAsync(store2, store1);
                await EnsureReplicatingAsync(store2, store1);

                await EnsureNoReplicationLoop(Server, store1.Database);
                await EnsureNoReplicationLoop(Server, store2.Database);

                Indexes.WaitForIndexing(store1);
                Indexes.WaitForIndexing(store2);

                AssertIndexEntries2(store1, expectedEntries: 0);
                AssertIndexEntries2(store2, expectedEntries: 0);
            }
        }

        [RavenTheory(RavenTestCategory.Replication | RavenTestCategory.Indexes)]
        [RavenData(DatabaseMode = RavenDatabaseMode.All)]
        public async Task ResolveConflictOfDifferentCollectionToTombstoneShouldUpdateIndex(Options options)
        {
            using (var store1 = GetDocumentStore(options))
            using (var store2 = GetDocumentStore(options))
            {
                var id = "foo/bar";

                using (var session = store1.OpenSession())
                {
                    session.Store(new User { Name = "Karmel" }, id);
                    session.SaveChanges();
                }

                using (var session = store2.OpenSession())
                {
                    session.Store(new Company { Name = "Karmel" }, id);
                    session.SaveChanges();
                }

                await new MapReduce().ExecuteAsync(store1);
                await new MapReduce2().ExecuteAsync(store2);

                Indexes.WaitForIndexing(store1);
                Indexes.WaitForIndexing(store2);

                AssertIndexEntries3(store1, 1);
                AssertIndexEntries3(store2, 1);

                await SetupReplicationAsync(store1, store2);
                await EnsureReplicatingAsync(store1, store2);

                await SetupReplicationAsync(store2, store1);
                await EnsureReplicatingAsync(store2, store1);

                using (var session = store1.OpenSession())
                {
                    session.Delete(id);
                    session.SaveChanges();
                }

                await EnsureReplicatingAsync(store1, store2);

                await EnsureNoReplicationLoop(Server, store1.Database);
                await EnsureNoReplicationLoop(Server, store2.Database);

                Indexes.WaitForIndexing(store1);
                Indexes.WaitForIndexing(store2);

                AssertIndexEntries3(store1, 0);
                AssertIndexEntries3(store2, 0);
            }
        }

        [RavenTheory(RavenTestCategory.Replication | RavenTestCategory.Attachments | RavenTestCategory.Indexes)]
        [RavenData(DatabaseMode = RavenDatabaseMode.All)]
        public async Task ResolveConflictOfDifferentCollectionAndAttachmentsConflictShouldUpdateIndex(Options options)
        {
            using (var store1 = GetDocumentStore(options))
            using (var store2 = GetDocumentStore(options))
            {
                var id = "foo/bar";

                using (var fooStream = new MemoryStream(new byte[] { 1, 2, 3 }))
                using (var session = store1.OpenSession())
                {
                    session.Store(new User { Name = "Karmel" }, id);
                    session.Advanced.Attachments.Store(id, "foo.png", fooStream, "image/png");
                    session.SaveChanges();
                }

                using (var fooStream2 = new MemoryStream(new byte[] { 4, 5, 6 }))
                using (var session = store2.OpenSession())
                {
                    session.Store(new Company { Name = "Karmel" }, id);
                    session.Advanced.Attachments.Store(id, "foo2.png", fooStream2, "image/png");
                    session.SaveChanges();
                }

                var index1 = new MapReduce();
                var index2 = new MapReduce2();

                await index1.ExecuteAsync(store1);
                await index2.ExecuteAsync(store2);

                Indexes.WaitForIndexing(store1);
                Indexes.WaitForIndexing(store2);

                AssertIndexEntries3(store1, 1);
                AssertIndexEntries3(store2, 1);

                await SetupReplicationAsync(store1, store2);
                await EnsureReplicatingAsync(store1, store2);

                await SetupReplicationAsync(store2, store1);
                await EnsureReplicatingAsync(store2, store1);

                await SetReplicationConflictResolutionAsync(store2, StraightforwardConflictResolution.ResolveToLatest);

                await EnsureReplicatingAsync(store2, store1);
                await EnsureReplicatingAsync(store1, store2);

                await EnsureNoReplicationLoop(Server, store1.Database);
                await EnsureNoReplicationLoop(Server, store2.Database);

                Indexes.WaitForIndexing(store1);
                Indexes.WaitForIndexing(store2);

                AssertIndexEntries3(store1, 0);
                AssertIndexEntries3(store2, 1);
            }
        }

        [RavenTheory(RavenTestCategory.Replication | RavenTestCategory.Indexes)]
        [RavenData(DatabaseMode = RavenDatabaseMode.All)]
        public async Task ConflictBetweenDocumentAndTombstoneShouldUpdateMapIndex(Options options)
        {
            using (var store1 = GetDocumentStore(options))
            using (var store2 = GetDocumentStore(options))
            {
                await UpdateConflictResolver(store1, resolveToLatest: false);
                await UpdateConflictResolver(store2, resolveToLatest: false);

                using (var session = store1.OpenSession())
                {
                    session.Store(new User { Name = "test" }, "users/1");
                    session.Store(new User { Name = "test" }, "users/2");
                    session.Store(new User { Name = "test" }, "users/3");
                    session.SaveChanges();
                    session.Delete("users/1");
                    session.SaveChanges();
                }
                using (var session = store2.OpenSession())
                {
                    session.Store(new User { Name = "test2" }, "users/1");
                    session.Store(new User { Name = "test2" }, "users/2");
                    session.Store(new User { Name = "test2" }, "users/3");
                    session.SaveChanges();
                    session.Delete("users/1");
                    session.Delete("users/2");
                    session.SaveChanges();
                }

                await new Map().ExecuteAsync(store1);
                await new Map().ExecuteAsync(store2);

                Indexes.WaitForIndexing(store1);
                Indexes.WaitForIndexing(store2);

                AssertIndexEntries2(store1, expectedEntries: 2);
                AssertIndexEntries2(store2, expectedEntries: 1);

                await SetupReplicationAsync(store1, store2);

                Indexes.WaitForIndexing(store1);
                Indexes.WaitForIndexing(store2);

                Assert.Equal(2, WaitUntilHasConflict(store2, "users/3").Length);
                Assert.Equal(2, WaitUntilHasConflict(store2, "users/2").Length);
                // conflict between two tombstones, resolved automaticlly to tombstone.
                var tombstones = WaitUntilHasTombstones(store2, skipArtificial: true);
                Assert.Equal("users/1", tombstones.Single());

                AssertIndexEntries2(store1, expectedEntries: 2);
                AssertIndexEntries2(store2, expectedEntries: 0);
            }
        }

        private async Task AssertIndexEntries(DocumentStore store, int expectedEntries)
        {
            using (var session = store.OpenSession())
            {
                QueryCommand queryCommand = new(session as InMemoryDocumentSessionOperations, new IndexQuery { Query = "from index 'MapReduce'" });
                await store.Commands().ExecuteAsync(queryCommand);
                Assert.Equal(expectedEntries, queryCommand.Result.Results.Length);
            }
        }

        private void AssertIndexEntries2(DocumentStore store, int expectedEntries)
        {
            using (var session = store.OpenSession())
            {
                var results = session
                    .Query<User, Map>()
                    .Select(x => new { x.Name, x.Count })
                    .ToList();

                Assert.Equal(expectedEntries, results.Count);
            }
        }

        private void AssertIndexEntries3(DocumentStore store, int expectedEntries)
        {
            using (var session = store.OpenSession())
            {
                var res = session.Query<dynamic>("MapReduce").ToList();
                Assert.Equal(expectedEntries, res.Count);
            }
        }

        private class Result
        {
            public int Count { get; set; }
            public string Name { get; set; }
        }

        private class Map : AbstractIndexCreationTask<User>
        {
            public override string IndexName => "MapReduce";

            public Map()
            {
                Map = users => from user in users
                               select new Result
                               {
                                   Name = user.Name,
                                   Count = 1
                               };
                StoreAllFields(FieldStorage.Yes);
            }
        }

        private class MapReduce : AbstractIndexCreationTask<User, Result>
        {
            public override string IndexName => "MapReduce";

            public MapReduce()
            {
                Map = users => from user in users
                               select new Result
                               {
                                   Name = user.Name,
                                   Count = 1
                               };

                Reduce = results => from result in results
                                    group result by result.Name
                    into g
                                    select new
                                    {
                                        Name = g.Key,
                                        Count = g.Sum(x => x.Count)
                                    };

            }
        }

        private class MapReduce2 : AbstractIndexCreationTask<Company, Result>
        {
            public override string IndexName => "MapReduce";

            public MapReduce2()
            {
                Map = companies => from company in companies
                                   select new Result
                                   {
                                       Name = company.Name,
                                       Count = 1
                                   };

                Reduce = results => from result in results
                                    group result by result.Name
                    into g
                                    select new
                                    {
                                        Name = g.Key,
                                        Count = g.Sum(x => x.Count)
                                    };
            }
        }
    }
}
