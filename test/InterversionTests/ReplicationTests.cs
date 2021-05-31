﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FastTests.Server.Replication;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Operations.ConnectionStrings;
using Raven.Client.Documents.Operations.ETL;
using Raven.Client.Documents.Operations.OngoingTasks;
using Raven.Client.Documents.Operations.Replication;
using Raven.Client.Exceptions;
using Raven.Client.ServerWide.Tcp;
using Raven.Server.Utils;
using Raven.Tests.Core.Utils.Entities;
using Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace InterversionTests
{
    public class ReplicationTests : InterversionTestBase
    {
        public ReplicationTests(ITestOutputHelper output) : base(output)
        {
        }

        [Theory(Skip = "TODO: Add compatible version of v4.2 when released")]
        public async Task CannotReplicateTimeSeriesToV42()
        {
            var version = "4.2.101"; // todo:Add compatible version of v4.2 when released
            var getOldStore = GetDocumentStoreAsync(version);
            await Task.WhenAll(getOldStore);

            using var oldStore = await getOldStore;
            using var store = GetDocumentStore();
            using (var session = store.OpenAsyncSession())
            {
                await session.StoreAsync(new User { Name = "Egor" }, "user/322");
                session.TimeSeriesFor("user/322", "a").Append(DateTime.UtcNow, 1);
                await session.SaveChangesAsync();
            }

            var externalTask = new ExternalReplication(oldStore.Database.ToLowerInvariant(), "MyConnectionString")
            {
                Name = "MyExternalReplication",
                Url = oldStore.Urls.First()
            };

            await SetupReplication(store, externalTask);

            var replicationLoader = (await GetDocumentDatabaseInstanceFor(store)).ReplicationLoader;
            Assert.NotEmpty(replicationLoader.OutgoingFailureInfo);
            Assert.True(WaitForValue(() => replicationLoader.OutgoingFailureInfo.Any(ofi => ofi.Value.RetriesCount > 2), true));
            Assert.True(replicationLoader.OutgoingFailureInfo.Any(ofi => ofi.Value.Errors.Any(x => x.GetType() == typeof(LegacyReplicationViolationException))));
            Assert.True(replicationLoader.OutgoingFailureInfo.Any(ofi => ofi.Value.Errors.Select(x => x.Message).Any(x => x.Contains("TimeSeries"))));
        }

        private static async Task<ModifyOngoingTaskResult> SetupReplication(IDocumentStore store, ExternalReplicationBase watcher)
        {
            var result = await store.Maintenance.SendAsync(new PutConnectionStringOperation<RavenConnectionString>(new RavenConnectionString
            {
                Name = watcher.ConnectionStringName,
                Database = watcher.Database,
                TopologyDiscoveryUrls = new[]
                {
                    watcher.Url
                }
            }));
            Assert.NotNull(result.RaftCommandIndex);

            IMaintenanceOperation<ModifyOngoingTaskResult> op;
            switch (watcher)
            {
                case PullReplicationAsSink pull:
                    op = new UpdatePullReplicationAsSinkOperation(pull);
                    break;
                case ExternalReplication ex:
                    op = new UpdateExternalReplicationOperation(ex);
                    break;
                default:
                    throw new ArgumentException($"Unrecognized type: {watcher.GetType().FullName}");
            }

            return await store.Maintenance.SendAsync(op);
        }

        /*[Fact]
        public async Task ShouldNotSupportDataCompressionFeatureInReplication()
        {
            using var store42 = await GetDocumentStoreAsync("4.2.102-nightly-20200415-0501");
            using var storeCurrent = GetDocumentStore();

            using (var session = store42.OpenAsyncSession())
            {
                for (var i = 0; i < 5; i++)
                {
                    var user = new User { Name = "raven" + i };
                    await session.StoreAsync(user);
                }
                await session.SaveChangesAsync();
            }

            var externalTask = new ExternalReplication(store42.Database.ToLowerInvariant(), "MyConnectionString")
            {
                Name = "MyExternalReplication",
                Url = store42.Urls.First()
            };

            var database42 = await GetDatabase(store42.Database);
            await SetupReplication(storeCurrent, externalTask);
            

            var replicationLoader = (await GetDocumentDatabaseInstanceFor(storeCurrent)).ReplicationLoader;
            var supportedFeatures = new List<TcpConnectionHeaderMessage.SupportedFeatures>();
            foreach (var op in replicationLoader.OutgoingHandlers)
            {
               supportedFeatures.Add(op.SupportedFeatures);
            }
            //Assert.True(supportedFeatures.Contains(TcpConnectionHeaderMessage.SupportedFeatures.ReplicationFeatures));
            //var supportedFeatures = TcpConnectionHeaderMessage.GetSupportedFeaturesFor(TcpConnectionHeaderMessage.OperationTypes.Replication, "4.2.102-nightly-20200415-0501");
            // replicationLoader.Database.Operations.
        }*/
    }
}
