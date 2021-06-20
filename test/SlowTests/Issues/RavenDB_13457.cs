﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Configuration;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Raven.Client.ServerWide.Operations.Configuration;
using Raven.Server.ServerWide.Context;
using Raven.Tests.Core.Utils.Entities;
using Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Issues
{
    public class RavenDB_13457 : ClusterTestBase
    {
        public RavenDB_13457(ITestOutputHelper output) : base(output)
        {
        }

        private class UserIndex : AbstractIndexCreationTask<User>
        {
            public UserIndex()
            {
                Map = users => from user in users
                               select new
                               {
                                   user.Name
                               };
            }
        }

        [Fact]
        public async Task DoesReadBalancerAndMaxNumberOfRequestsClietnConfigurationTakeEffectOnCurrentRequestExecutor()
        {
            var nodesCount = 3;
            var db = GetDatabaseName();

            var (_, leader) = await CreateRaftCluster(nodesCount);
            var result = await CreateDatabaseInCluster(db, nodesCount, leader.WebUrl);

            using (var store =
                new DocumentStore
                {
                    Urls = new[] { leader.WebUrl },
                    Database = db,
                    Conventions = new Raven.Client.Documents.Conventions.DocumentConventions
                    {
                        ReadBalanceBehavior = Raven.Client.Http.ReadBalanceBehavior.RoundRobin,
                        MaxNumberOfRequestsPerSession = 5
                    }
                }.Initialize())
            {
                SpinWait.SpinUntil(() =>
                {
                    using (leader.ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext ctx))
                    using (ctx.OpenReadTransaction())
                    {
                        var record = leader.ServerStore.Cluster.ReadDatabase(ctx, db);
                        return record.Topology.Members.Count == nodesCount;
                    }
                }, TimeSpan.FromSeconds(10));

                using (var session = store.OpenSession())
                {
                    session.Store(new User
                    {
                        Name = "Samson"
                    });
                    session.SaveChanges();
                }

                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                {
                    using (var session = store.OpenAsyncSession())
                    {
                        for (int i = 0; i < 6; i++)
                        {
                            await session.StoreAsync(new User
                            {
                                Name = "Samson"
                            });
                            await session.SaveChangesAsync();
                        }
                    }
                });

                HashSet<string> usedUrls = new HashSet<string>();
                for (var i = 0; i < nodesCount; i++)
                {
                    using (var session = store.OpenSession())
                    {
                        Raven.Client.Http.ServerNode serverNode = await session.Advanced.GetCurrentSessionNode();
                        Assert.True(usedUrls.Add(serverNode.Url.ToLower()));
                    }
                }
                Assert.Equal(nodesCount, usedUrls.Count);


                // now, we modify the values and make sure that we received them
                store.Maintenance.Server.Send(
                    new PutServerWideClientConfigurationOperation(
#pragma warning disable 618
                        new ClientConfiguration { MaxNumberOfRequestsPerSession = 10, PrettifyGeneratedLinqExpressions = false, ReadBalanceBehavior = Raven.Client.Http.ReadBalanceBehavior.None }));
#pragma warning restore 618


                Assert.True(SpinWait.SpinUntil(() =>
                {
                    using (var session = store.OpenSession())
                    {
                        session.Load<User>("users/1"); // we need this to "pull" the new config                    
                        return session.Advanced.RequestExecutor.Conventions.MaxNumberOfRequestsPerSession == 10;
                    }

                }, TimeSpan.FromSeconds(10)));




                using (var session = store.OpenAsyncSession())
                {
                    for (int i = 0; i < 6; i++)
                    {
                        await session.StoreAsync(new User
                        {
                            Name = "Samson"
                        });
                        await session.SaveChangesAsync();
                    }
                }

                usedUrls.Clear();
                for (var i = 0; i < nodesCount; i++)
                {
                    using (var session = store.OpenSession())
                    {
                        Raven.Client.Http.ServerNode serverNode1 = await session.Advanced.GetCurrentSessionNode();
                        usedUrls.Add(serverNode1.Url.ToLower());
                    }
                }
                Assert.Equal(1, usedUrls.Count);


                // now we want to disable the client configuration and use the "default" ones

                store.Maintenance.Server.Send(
                        new PutServerWideClientConfigurationOperation(
#pragma warning disable 618
                            new ClientConfiguration { MaxNumberOfRequestsPerSession = 10, PrettifyGeneratedLinqExpressions = false, ReadBalanceBehavior = Raven.Client.Http.ReadBalanceBehavior.None, Disabled = true }));
#pragma warning restore 618

                using (var session = store.OpenSession())
                {
                    session.Store(new User
                    {
                        Name = "Samson"
                    });
                    session.SaveChanges();
                }


                Assert.True(SpinWait.SpinUntil(() =>
                {
                    using (var session = store.OpenSession())
                    {
                        session.Load<User>("users/1"); // we need this to "pull" the new config                    
                        return session.Advanced.RequestExecutor.Conventions.MaxNumberOfRequestsPerSession == 5;
                    }

                }, TimeSpan.FromSeconds(10)));

                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                {
                    using (var session = store.OpenAsyncSession())
                    {
                        for (int i = 0; i < 6; i++)
                        {
                            await session.StoreAsync(new User
                            {
                                Name = "Samson"
                            });
                            await session.SaveChangesAsync();
                        }
                    }
                });

                usedUrls.Clear();
                for (var i = 0; i < nodesCount; i++)
                {
                    using (var session = store.OpenSession())
                    {
                        Assert.True(usedUrls.Add((await session.Advanced.GetCurrentSessionNode()).Url.ToLower()));
                    }
                }
                Assert.Equal(nodesCount, usedUrls.Count);



            }

        }


        [Fact(Skip = "RavenDB-13473")]
        public async Task DoesPrettifyGeneratedLinqExpressionsConfigurationTakeEffectOnCurrentRequestExecutor()
        {
            var nodesCount = 3;
            var cluster = await CreateRaftCluster(nodesCount);
            var db = GetDatabaseName();

            GetDocumentStore(new Options
            {
                ReplicationFactor = 3
            });
            using (var store =
                new DocumentStore
                {
                    Urls = new[] { cluster.Leader.WebUrl },
                    Database = db,
                    Conventions = new Raven.Client.Documents.Conventions.DocumentConventions
                    {
#pragma warning disable 618
                        PrettifyGeneratedLinqExpressions = true
#pragma warning restore 618
                    }
                }.Initialize())
            {
                var databaseResult = await store.Maintenance.Server.SendAsync(new CreateDatabaseOperation(new DatabaseRecord(db), nodesCount));
                Assert.Equal(nodesCount, databaseResult.Topology.Members.Count);


                SpinWait.SpinUntil(() =>
                {
                    using (cluster.Leader.ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext ctx))
                    using (ctx.OpenReadTransaction())
                    {
                        var record = cluster.Leader.ServerStore.Cluster.ReadDatabase(ctx, db);
                        return record.Topology.Members.Count == nodesCount;
                    }
                }, TimeSpan.FromSeconds(10));


                // first, we just verify that the conventions values are used
                store.ExecuteIndex(new UserIndex());
                var indexDefinition = store.Maintenance.Send(new GetIndexOperation("UserIndex"));

                using (var session = store.OpenSession())
                {
                    session.Store(new User
                    {
                        Name = "Samson"
                    });
                    session.SaveChanges();
                }

                // now, we modify the values and make sure that we received them
                store.Maintenance.Server.Send(
                    new PutServerWideClientConfigurationOperation(
#pragma warning disable 618
                        new ClientConfiguration { PrettifyGeneratedLinqExpressions = false }));
#pragma warning restore 618

                using (var session = store.OpenAsyncSession())
                {
                    await session.LoadAsync<User>("users/1"); // we need this to "pull" the new config                    
                }

                await store.Maintenance.SendAsync(new DeleteIndexOperation("UserIndex"));

                store.ExecuteIndex(new UserIndex());

                IndexDefinition indexDefinition1 = store.Maintenance.Send(new GetIndexOperation("UserIndex"));
                Assert.NotEqual(indexDefinition.Maps.First(), indexDefinition1.Maps.First());


                // now we want to disable the client configuration and use the "default" ones

                store.Maintenance.Server.Send(
                        new PutServerWideClientConfigurationOperation(
#pragma warning disable 618
                            new ClientConfiguration { PrettifyGeneratedLinqExpressions = false, Disabled = true }));
#pragma warning restore 618

                using (var session = store.OpenSession())
                {
                    session.Load<User>("users/1");
                }


                await store.Maintenance.SendAsync(new DeleteIndexOperation("UserIndex"));

                store.ExecuteIndex(new UserIndex());

                IndexDefinition indexDefinition2 = store.Maintenance.Send(new GetIndexOperation("UserIndex"));
                Assert.Equal(indexDefinition.Maps.First(), indexDefinition1.Maps.First());



            }

        }
    }
}
