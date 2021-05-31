﻿using System;
using System.IO;
using System.IO.Compression;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using FastTests.Server.Replication;
using Raven.Client.Documents.Operations.ConnectionStrings;
using Raven.Client.Documents.Operations.ETL;
using Raven.Client.Documents.Operations.Replication;
using Raven.Tests.Core.Utils.Entities;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Server.Replication
{
    public class ExternalReplicationTests : ReplicationTestBase
    {
        public ExternalReplicationTests(ITestOutputHelper output) : base(output)
        {
        }

        [Theory]
        [InlineData(3000)]
        public async Task ExternalReplicationShouldWorkWithSmallTimeoutStress(int timeout)
        {
            using (var store1 = GetDocumentStore(new Options
            {
                ModifyDatabaseName = s => $"{s}_FooBar-1"
            }))
            using (var store2 = GetDocumentStore(new Options
            {
                ModifyDatabaseName = s => $"{s}_FooBar-2"
            }))
            using (var store3 = GetDocumentStore(new Options
            {
                ModifyDatabaseName = s => $"{s}_FooBar-3"
            }))
            {
                await SetupReplicationAsync(store1, store2, store3);

                using (var s1 = store1.OpenSession())
                {
                    s1.Store(new User(), "foo/bar");
                    s1.SaveChanges();
                }
                // SlowTests uses the regular old value of 3000mSec. But if called from StressTests - needs more timeout
                Assert.True(WaitForDocument(store2, "foo/bar", timeout), store2.Identifier);
                Assert.True(WaitForDocument(store3, "foo/bar", timeout), store3.Identifier);
            }
        }

        [Fact]
        public async Task DelayedExternalReplication()
        {
            using (var store1 = GetDocumentStore())
            using (var store2 = GetDocumentStore())
            {
                var delay = TimeSpan.FromSeconds(5);
                var externalTask = new ExternalReplication(store2.Database, "DelayedExternalReplication")
                {
                    DelayReplicationFor = delay
                };
                await AddWatcherToReplicationTopology(store1, externalTask);
                DateTime date;

                using (var s1 = store1.OpenSession())
                {
                    s1.Store(new User(), "foo/bar");
                    date = DateTime.UtcNow;
                    s1.SaveChanges();
                }

                Assert.True(WaitForDocument(store2, "foo/bar"));
                var elapsed = DateTime.UtcNow - date;
                Assert.True(elapsed >= delay, $" only {elapsed}/{delay} ticks elapsed");

            }
        }

        [Fact]
        public async Task EditExternalReplication()
        {
            using (var store1 = GetDocumentStore())
            using (var store2 = GetDocumentStore())
            {
                var delay = TimeSpan.FromSeconds(5);
                var externalTask = new ExternalReplication(store2.Database, "DelayedExternalReplication")
                {
                    DelayReplicationFor = delay
                };
                await AddWatcherToReplicationTopology(store1, externalTask);
                DateTime date;

                using (var s1 = store1.OpenSession())
                {
                    s1.Store(new User(), "foo/bar");
                    date = DateTime.UtcNow;
                    s1.SaveChanges();
                }

                Assert.True(WaitForDocument(store2, "foo/bar"));
                var elapsed = DateTime.UtcNow - date;
                Assert.True(elapsed >= delay, $" only {elapsed}/{delay} ticks elapsed");

                delay = TimeSpan.Zero;
                externalTask.DelayReplicationFor = delay;
                var op = new UpdateExternalReplicationOperation(externalTask);
                await store1.Maintenance.SendAsync(op);

                using (var s1 = store1.OpenSession())
                {
                    s1.Store(new User(), "foo/bar");
                    date = DateTime.UtcNow;
                    s1.SaveChanges();
                }

                Assert.True(WaitForDocument(store2, "foo/bar"));
                var elapsedTime = DateTime.UtcNow - date;
                Assert.True(elapsedTime >= delay && elapsedTime < elapsed, $" only {elapsed}/{delay} ticks elapsed");
            }
        }

        [Fact]
        public async Task CanChangeConnectionString()
        {
            using (var store1 = GetDocumentStore())
            using (var store2 = GetDocumentStore())
            {
                var externalReplication = new ExternalReplication
                {
                    ConnectionStringName = "ExReplication"
                };
                await store1.Maintenance.SendAsync(new PutConnectionStringOperation<RavenConnectionString>(new RavenConnectionString
                {
                    Name = externalReplication.ConnectionStringName,
                    Database = "NotExist",
                    TopologyDiscoveryUrls = store1.Urls
                }));
                await store1.Maintenance.SendAsync(new UpdateExternalReplicationOperation(externalReplication));

                using (var s1 = store1.OpenSession())
                {
                    s1.Store(new User(), "foo/bar");
                    s1.SaveChanges();
                }

                Assert.False(WaitForDocument(store2, "foo/bar", timeout: 5_000));

                await store1.Maintenance.SendAsync(new PutConnectionStringOperation<RavenConnectionString>(new RavenConnectionString
                {
                    Name = externalReplication.ConnectionStringName,
                    Database = store2.Database,
                    TopologyDiscoveryUrls = store1.Urls
                }));

                Assert.True(WaitForDocument(store2, "foo/bar", timeout: 5_000));

                await store1.Maintenance.SendAsync(new PutConnectionStringOperation<RavenConnectionString>(new RavenConnectionString
                {
                    Name = externalReplication.ConnectionStringName,
                    Database = "NotExist",
                    TopologyDiscoveryUrls = store1.Urls
                }));

                using (var s1 = store1.OpenSession())
                {
                    s1.Store(new User(), "foo/bar/2");
                    s1.SaveChanges();
                }
                Assert.False(WaitForDocument(store2, "foo/bar/2", timeout: 5_000));
            }
        }

        [Fact]
        public async Task ExternalReplicationToNonExistingDatabase()
        {
            using (var store1 = GetDocumentStore())
            using (var store2 = GetDocumentStore())
            {
                var externalTask = new ExternalReplication(store2.Database + "test", $"Connection to {store2.Database} test");
                await AddWatcherToReplicationTopology(store1, externalTask);
            }
        }

        [Fact]
        public async Task ExternalReplicationToNonExistingNode()
        {
            using (var store1 = GetDocumentStore())
            {
                var externalTask = new ExternalReplication(store1.Database + "test", $"Connection to {store1.Database} test");
                await AddWatcherToReplicationTopology(store1, externalTask, new []{"http://1.2.3.4:8080"});
            }
        }

        [Theory]
        [InlineData(3000)]
        public async Task NetworkStreamCompressionInReplication(int timeout)
        {
            DoNotReuseServer();
            using (var store1 = GetDocumentStore(new Options
            {
                ModifyDatabaseName = s => $"{s}-foo-bar-1"
            }))
            using (var store2 = GetDocumentStore(new Options
            {
                ModifyDatabaseName = s => $"{s}-foo-bar-2"
            }))
            {
                Server.ServerStore.Observer.Suspended = true;
                using (var s1 = store1.OpenSession())
                {
                    s1.Store(new User() { AddressId = "Head Haam", LastName = "Rhinos", Name = "Shiran" }, $"foo/bar");
                    s1.SaveChanges();
                }

                var writeTest = await GetDatabase(store1.Database);

                NetworkStreamTest outgoingNetworkStream = null;
                writeTest.ForTestingPurposesOnly().ReplaceNetworkStream = socket =>
                {
                    outgoingNetworkStream = new NetworkStreamTest(socket);
                    return outgoingNetworkStream;
                };

                GZipStreamTest outgoingGZipStream = null;
                writeTest.ForTestingPurposesOnly().ReplaceGzipStream = stream =>
                {
                    outgoingGZipStream = new GZipStreamTest(stream, CompressionMode.Compress, leaveOpen: true);
                    return outgoingGZipStream;
                };

                var readTest = await GetDatabase(store2.Database);

                NetworkStreamTest incomingNetworkStream = null;
                readTest.ForTestingPurposesOnly().ReplaceNetworkStream = socket =>
                {
                    incomingNetworkStream = new NetworkStreamTest(socket);
                    return incomingNetworkStream;
                };

                GZipStreamTest incomingGZipStream = null;
                readTest.ForTestingPurposesOnly().ReplaceGzipStream = stream =>
                {
                    incomingGZipStream = new GZipStreamTest(stream, CompressionMode.Decompress, leaveOpen: true);
                    return incomingGZipStream;
                };

                await SetupReplicationAsync(store1, store2);

                Assert.True(WaitForDocument(store2, "foo/bar", timeout), store2.Identifier);
                Assert.True(outgoingNetworkStream.NetworkStreamTotalWrittenBytes < outgoingGZipStream.GZipStreamTotalWrittenBytes);
                Assert.True(incomingNetworkStream.NetworkStreamTotalReadenBytes < incomingGZipStream.GZipStreamTotalReadenBytes);
            }
        }

        internal class NetworkStreamTest : NetworkStream
        {
            public int NetworkStreamTotalWrittenBytes;
            public int NetworkStreamTotalReadenBytes;

            public NetworkStreamTest(Socket socket) : base(socket)
            {
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                NetworkStreamTotalWrittenBytes += count;
                base.Write(buffer, offset, count);
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = new CancellationToken())
            {
                NetworkStreamTotalReadenBytes += buffer.Length;
                return base.ReadAsync(buffer, cancellationToken);
            }
        }

        public class GZipStreamTest : GZipStream
        {
            public int GZipStreamTotalWrittenBytes;
            public int GZipStreamTotalReadenBytes;
            public GZipStreamTest(Stream stream, CompressionMode compressionMode, bool leaveOpen) : base(stream, compressionMode, leaveOpen: leaveOpen)
            {
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                GZipStreamTotalWrittenBytes += count;
                base.Write(buffer, offset, count);
            }
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = new CancellationToken())
            {
                GZipStreamTotalReadenBytes += buffer.Length;
                return base.ReadAsync(buffer, cancellationToken);
            }
        }
    }
}
