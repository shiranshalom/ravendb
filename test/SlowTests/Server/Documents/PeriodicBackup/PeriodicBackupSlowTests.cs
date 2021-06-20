﻿using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FastTests.Server.Basic.Entities;
using Newtonsoft.Json;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Operations.Attachments;
using Raven.Client.Documents.Operations.Backups;
using Raven.Client.Documents.Operations.CompareExchange;
using Raven.Client.Documents.Operations.OngoingTasks;
using Raven.Client.Documents.Session;
using Raven.Client.Documents.Smuggler;
using Raven.Client.Exceptions;
using Raven.Client.Exceptions.Documents;
using Raven.Client.ServerWide.Operations;
using Raven.Client.ServerWide.Operations.Configuration;
using Raven.Client.Util;
using Raven.Server.Documents;
using Raven.Server.Documents.PeriodicBackup;
using Raven.Server.Documents.PeriodicBackup.Restore;
using Raven.Server.ServerWide.Commands.PeriodicBackup;
using Raven.Server.ServerWide.Context;
using Raven.Tests.Core.Utils.Entities;
using Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Server.Documents.PeriodicBackup
{
    public class PeriodicBackupTestsSlow : ClusterTestBase
    {
        public PeriodicBackupTestsSlow(ITestOutputHelper output) : base(output)
        {
        }

        [Fact, Trait("Category", "Smuggler")]
        public async Task can_backup_to_directory()
        {
            var backupPath = NewDataPath(suffix: "BackupFolder");
            using (var store = GetDocumentStore())
            {
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "oren" }, "users/1");
                    await session.SaveChangesAsync();
                }

                var config = Backup.CreateBackupConfiguration(backupPath, incrementalBackupFrequency: "* * * * *");
                var operation = new UpdatePeriodicBackupOperation(config);
                var result = await store.Maintenance.SendAsync(operation);
                var periodicBackupTaskId = result.TaskId;

                var getPeriodicBackupStatus = new GetPeriodicBackupStatusOperation(periodicBackupTaskId);
                var done = SpinWait.SpinUntil(() => store.Maintenance.Send(getPeriodicBackupStatus).Status?.LastFullBackup != null, TimeSpan.FromSeconds(180));
                Assert.True(done, "Failed to complete the backup in time");
            }

            using (var store = GetDocumentStore(new Options
            {
                ModifyDatabaseName = s => $"{s}_2"
            }))
            {
                await store.Smuggler.ImportIncrementalAsync(new DatabaseSmugglerImportOptions(),
                    Directory.GetDirectories(backupPath).First());

                using (var session = store.OpenAsyncSession())
                {
                    var user = await session.LoadAsync<User>("users/1");
                    Assert.NotNull(user);
                    Assert.Equal("oren", user.Name);
                }
            }
        }

        [Fact, Trait("Category", "Smuggler")]
        public async Task can_backup_to_directory_multiple_backups_with_long_interval()
        {
            var backupPath = NewDataPath(suffix: "BackupFolder");

            using (var store = GetDocumentStore())
            {
                var periodicBackupRunner = (await GetDocumentDatabaseInstanceFor(store)).PeriodicBackupRunner;

                // get by reflection the maxTimerTimeoutInMilliseconds field
                // this field is the maximum interval acceptable in .Net's threading timer
                // if the requested backup interval is bigger than this maximum interval,
                // a timer with maximum interval will be used several times until the interval cumulatively
                // will be equal to requested interval
                typeof(PeriodicBackupRunner)
                    .GetField(nameof(PeriodicBackupRunner.MaxTimerTimeout), BindingFlags.Instance | BindingFlags.Public)
                    .SetValue(periodicBackupRunner, TimeSpan.FromMilliseconds(100));

                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "oren" }, "users/1");
                    await session.SaveChangesAsync();
                }

                var config = Backup.CreateBackupConfiguration(backupPath);
                var backupTaskId = await Backup.UpdateConfigAndRunBackupAsync(Server, config, store);

                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "ayende" }, "users/2");
                    await session.SaveChangesAsync();
                }

                var lastEtag = store.Maintenance.Send(new GetStatisticsOperation()).LastDocEtag;
                await Backup.RunBackupAndReturnStatusAsync(Server, backupTaskId, store, isFullBackup: false, expectedEtag: lastEtag);
            }

            using (var store = GetDocumentStore(new Options
            {
                ModifyDatabaseName = s => $"{s}_2"
            }))
            {
                await store.Smuggler.ImportIncrementalAsync(new DatabaseSmugglerImportOptions(),
                    Directory.GetDirectories(backupPath).First());
                using (var session = store.OpenAsyncSession())
                {
                    var users = await session.LoadAsync<User>(new[] { "users/1", "users/2" });
                    Assert.True(users.Any(x => x.Value.Name == "oren"));
                    Assert.True(users.Any(x => x.Value.Name == "ayende"));
                }
            }
        }

        [Fact, Trait("Category", "Smuggler")]
        public async Task periodic_backup_should_work_with_long_intervals()
        {
            var backupPath = NewDataPath(suffix: "BackupFolder");
            using (var store = GetDocumentStore())
            {
                var periodicBackupRunner = (await GetDocumentDatabaseInstanceFor(store)).PeriodicBackupRunner;

                // get by reflection the maxTimerTimeoutInMilliseconds field
                // this field is the maximum interval acceptable in .Net's threading timer
                // if the requested backup interval is bigger than this maximum interval,
                // a timer with maximum interval will be used several times until the interval cumulatively
                // will be equal to requested interval
                typeof(PeriodicBackupRunner)
                    .GetField(nameof(PeriodicBackupRunner.MaxTimerTimeout), BindingFlags.Instance | BindingFlags.Public)
                    .SetValue(periodicBackupRunner, TimeSpan.FromMilliseconds(100));

                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "oren 1" }, "users/1");
                    await session.SaveChangesAsync();
                }

                var config = Backup.CreateBackupConfiguration(backupPath);
                var backupTaskId = await Backup.UpdateConfigAndRunBackupAsync(Server, config, store);

                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "oren 2" }, "users/2");
                    await session.SaveChangesAsync();
                }

                var lastEtag = store.Maintenance.Send(new GetStatisticsOperation()).LastDocEtag;
                await Backup.RunBackupAndReturnStatusAsync(Server, backupTaskId, store, isFullBackup: false, expectedEtag: lastEtag);
            }

            using (var store = GetDocumentStore(new Options
            {
                ModifyDatabaseName = s => $"{s}_2"
            }))
            {
                await store.Smuggler.ImportIncrementalAsync(new DatabaseSmugglerImportOptions(),
                    Directory.GetDirectories(backupPath).First());
                using (var session = store.OpenAsyncSession())
                {
                    var user = await session.LoadAsync<User>("users/1");
                    Assert.Equal("oren 1", user.Name);

                    user = await session.LoadAsync<User>("users/2");
                    Assert.Equal("oren 2", user.Name);
                }
            }
        }

        [Fact, Trait("Category", "Smuggler")]
        public async Task can_backup_to_directory_multiple_backups()
        {
            var backupPath = NewDataPath(suffix: "BackupFolder");
            using (var store = GetDocumentStore())
            {
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "oren" }, "users/1");
                    await session.SaveChangesAsync();
                }

                var config = Backup.CreateBackupConfiguration(backupPath);
                var backupTaskId = await Backup.UpdateConfigAndRunBackupAsync(Server, config, store);

                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "ayende" }, "users/2");
                    await session.SaveChangesAsync();
                }

                var lastEtag = store.Maintenance.Send(new GetStatisticsOperation()).LastDocEtag;
                await Backup.RunBackupAndReturnStatusAsync(Server, backupTaskId, store, isFullBackup: false, expectedEtag: lastEtag);
            }

            using (var store = GetDocumentStore(new Options
            {
                ModifyDatabaseName = s => $"{s}_2"
            }))
            {
                await store.Smuggler.ImportIncrementalAsync(new DatabaseSmugglerImportOptions(),
                    Directory.GetDirectories(backupPath).First());
                using (var session = store.OpenAsyncSession())
                {
                    var users = await session.LoadAsync<User>(new[] { "users/1", "users/2" });
                    Assert.True(users.Any(x => x.Value.Name == "oren"));
                    Assert.True(users.Any(x => x.Value.Name == "ayende"));
                }
            }
        }

        [Fact, Trait("Category", "Smuggler")]
        public async Task CanImportTombstonesFromIncrementalBackup()
        {
            var backupPath = NewDataPath(suffix: "BackupFolder");
            using (var store = GetDocumentStore())
            {
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "fitzchak" }, "users/1");
                    await session.SaveChangesAsync();
                }

                var config = Backup.CreateBackupConfiguration(backupPath);
                var backupTaskId = await Backup.UpdateConfigAndRunBackupAsync(Server, config, store);

                using (var session = store.OpenAsyncSession())
                {
                    session.Delete("users/1");
                    await session.SaveChangesAsync();
                }

                await Backup.RunBackupAndReturnStatusAsync(Server, backupTaskId, store, isFullBackup: false, expectedEtag: 2);
            }

            using (var store = GetDocumentStore(new Options
            {
                ModifyDatabaseName = s => $"{s}_2"
            }))
            {
                await store.Smuggler.ImportIncrementalAsync(new DatabaseSmugglerImportOptions(),
                    Directory.GetDirectories(backupPath).First());
                using (var session = store.OpenAsyncSession())
                {
                    var user = await session.LoadAsync<User>("users/1");
                    Assert.Null(user);
                }
            }
        }

        [Fact, Trait("Category", "Smuggler")]
        public async Task can_restore_smuggler_correctly()
        {
            var backupPath = NewDataPath(suffix: "BackupFolder");
            using (var store = GetDocumentStore())
            {
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "oren" }, "users/1");
                    await session.SaveChangesAsync();
                }

                var config = Backup.CreateBackupConfiguration(backupPath);
                var backupTaskId = await Backup.UpdateConfigAndRunBackupAsync(Server, config, store);

                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "ayende" }, "users/2");
                    await session.SaveChangesAsync();
                }

                var lastEtag = store.Maintenance.Send(new GetStatisticsOperation()).LastDocEtag;
                await Backup.RunBackupAndReturnStatusAsync(Server, backupTaskId, store, isFullBackup: false, expectedEtag: lastEtag);
            }

            using (var store1 = GetDocumentStore(new Options
            {
                ModifyDatabaseName = s => $"{s}_2"
            }))
            using (var store2 = GetDocumentStore(new Options
            {
                ModifyDatabaseName = s => $"{s}_2"
            }))
            {
                var backupDirectory = Directory.GetDirectories(backupPath).First();

                var backupToMovePath = $"{backupPath}{Path.DirectorySeparatorChar}IncrementalBackupTemp";
                Directory.CreateDirectory(backupToMovePath);
                var incrementalBackupFile = Directory.GetFiles(backupDirectory).OrderBackups().Last();
                var fileName = Path.GetFileName(incrementalBackupFile);
                File.Move(incrementalBackupFile, $"{backupToMovePath}{Path.DirectorySeparatorChar}{fileName}");

                await store1.Smuggler.ImportIncrementalAsync(new DatabaseSmugglerImportOptions(), backupDirectory);
                using (var session = store1.OpenAsyncSession())
                {
                    var users = await session.LoadAsync<User>(new[] { "users/1", "users/2" });
                    var keyValuePair = users.First();
                    Assert.NotNull(keyValuePair.Value);
                    Assert.True(keyValuePair.Value.Name == "oren" && keyValuePair.Key == "users/1");
                    Assert.Null(users.Last().Value);
                }

                await store2.Smuggler.ImportIncrementalAsync(new DatabaseSmugglerImportOptions(), backupToMovePath);
                using (var session = store2.OpenAsyncSession())
                {
                    var users = await session.LoadAsync<User>(new[] { "users/1", "users/2" });
                    Assert.Null(users.First().Value);
                    var keyValuePair = users.Last();
                    Assert.NotNull(keyValuePair.Value);
                    Assert.True(keyValuePair.Value.Name == "ayende" && keyValuePair.Key == "users/2");
                }
            }
        }

        [Fact, Trait("Category", "Smuggler")]
        public async Task can_backup_and_restore()
        {
            var backupPath = NewDataPath(suffix: "BackupFolder");
            using (var store = GetDocumentStore())
            {
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "oren" }, "users/1");
                    session.CountersFor("users/1").Increment("likes", 100);
                    await session.SaveChangesAsync();
                }

                var config = Backup.CreateBackupConfiguration(backupPath);
                var backupTaskId = await Backup.UpdateConfigAndRunBackupAsync(Server, config, store);
                var operation = new GetPeriodicBackupStatusOperation(backupTaskId);

                var backupStatus = store.Maintenance.Send(operation);
                var backupOperationId = backupStatus.Status.LastOperationId;

                OperationState backupOperation = store.Maintenance.Send(new GetOperationStateOperation(backupOperationId.Value));
                Assert.Equal(OperationStatus.Completed, backupOperation.Status);

                var backupResult = backupOperation.Result as BackupResult;
                Assert.True(backupResult.Counters.Processed);
                Assert.Equal(1, backupResult.Counters.ReadCount);

                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "ayende" }, "users/2");
                    session.CountersFor("users/2").Increment("downloads", 200);

                    await session.SaveChangesAsync();
                }

                var lastEtag = store.Maintenance.Send(new GetStatisticsOperation()).LastDocEtag;
                await Backup.RunBackupAndReturnStatusAsync(Server, backupTaskId, store, isFullBackup: false, expectedEtag: lastEtag);

                // restore the database with a different name
                var databaseName = $"restored_database-{Guid.NewGuid()}";

                var backupLocation = Directory.GetDirectories(backupPath).First();

                using (ReadOnly(backupLocation))
                using (Backup.RestoreDatabase(store, new RestoreBackupConfiguration
                {
                    BackupLocation = backupLocation,
                    DatabaseName = databaseName
                }))
                {
                    using (var session = store.OpenAsyncSession(databaseName))
                    {
                        var users = await session.LoadAsync<User>(new[] { "users/1", "users/2" });
                        Assert.True(users.Any(x => x.Value.Name == "oren"));
                        Assert.True(users.Any(x => x.Value.Name == "ayende"));

                        var val = await session.CountersFor("users/1").GetAsync("likes");
                        Assert.Equal(100, val);
                        val = await session.CountersFor("users/2").GetAsync("downloads");
                        Assert.Equal(200, val);
                    }

                    var originalDatabase = await Server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(store.Database);
                    var restoredDatabase = await Server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(databaseName);
                    using (restoredDatabase.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext ctx))
                    using (ctx.OpenReadTransaction())
                    {
                        var databaseChangeVector = DocumentsStorage.GetDatabaseChangeVector(ctx);
                        Assert.Contains($"A:7-{originalDatabase.DbBase64Id}", databaseChangeVector);
                        Assert.Contains($"A:8-{restoredDatabase.DbBase64Id}", databaseChangeVector);
                    }
                }
            }
        }

        [Theory, Trait("Category", "Smuggler")]
        [InlineData("* * * * *", null)]
        [InlineData(null, "* * * * *")]
        [InlineData("0 0 1 * *", null)]
        [InlineData(null, "0 0 1 * *")]
        public async Task next_full_backup_time_calculated_correctly(string fullBackupFrequency, string incrementalBackupFrequency)
        {
            var backupPath = NewDataPath(suffix: "BackupFolder");
            using (var store = GetDocumentStore())
            {
                var config = Backup.CreateBackupConfiguration(backupPath, fullBackupFrequency: fullBackupFrequency, incrementalBackupFrequency: incrementalBackupFrequency);

                var backup = await store.Maintenance.SendAsync(new UpdatePeriodicBackupOperation(config));

                var documentDatabase = (await GetDocumentDatabaseInstanceFor(store));
                var record = await store.Maintenance.Server.SendAsync(new GetDatabaseRecordOperation(store.Database));
                var now = DateTime.UtcNow;
                var nextBackupDetails = documentDatabase.PeriodicBackupRunner.GetNextBackupDetails(record, record.PeriodicBackups.First(), new PeriodicBackupStatus
                {
                    LastFullBackupInternal = now.AddDays(-360)
                }, Server.ServerStore.NodeTag);

                Assert.Equal(backup.TaskId, nextBackupDetails.TaskId);
                Assert.Equal(TimeSpan.Zero, nextBackupDetails.TimeSpan);
                Assert.Equal(true, nextBackupDetails.IsFull);
                Assert.True(nextBackupDetails.DateTime >= now);
            }
        }

        [Theory, Trait("Category", "Smuggler")]
        [InlineData(CompressionLevel.Optimal)]
        [InlineData(CompressionLevel.Fastest)]
        [InlineData(CompressionLevel.NoCompression)]
        public async Task can_backup_and_restore_snapshot(CompressionLevel compressionLevel)
        {
            var backupPath = NewDataPath(suffix: "BackupFolder");
            using (var store = GetDocumentStore())
            {
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "oren" }, "users/1");
                    await session.SaveChangesAsync();
                }

                using (var session = store.OpenAsyncSession())
                {
                    await session
                        .Query<User>()
                        .Where(x => x.Name == "oren")
                        .ToListAsync(); // create an index to backup

                    await session
                        .Query<Order>()
                        .Where(x => x.Freight > 20)
                        .ToListAsync(); // create an index to backup

                    session.CountersFor("users/1").Increment("likes", 100); //create a counter to backup
                    await session.SaveChangesAsync();
                }

                var config = new PeriodicBackupConfiguration
                {
                    BackupType = BackupType.Snapshot,
                    SnapshotSettings = new SnapshotSettings
                    {
                        CompressionLevel = compressionLevel
                    },
                    LocalSettings = new LocalSettings
                    {
                        FolderPath = backupPath
                    },
                    IncrementalBackupFrequency = "* * * * *" //every minute
                };

                var backupTaskId = await Backup.UpdateConfigAndRunBackupAsync(Server, config, store);

                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "ayende" }, "users/2");
                    await session.StoreAsync(new User { Name = "ayende" }, "users/3");

                    session.CountersFor("users/2").Increment("downloads", 200);

                    await session.SaveChangesAsync();
                }

                var lastEtag = store.Maintenance.Send(new GetStatisticsOperation()).LastDocEtag;
                await Backup.RunBackupAndReturnStatusAsync(Server, backupTaskId, store, isFullBackup: false, expectedEtag: lastEtag);

                // restore the database with a different name
                string restoredDatabaseName = $"restored_database_snapshot-{Guid.NewGuid()}";
                var backupLocation = Directory.GetDirectories(backupPath).First();
                using (ReadOnly(backupLocation))
                using (Backup.RestoreDatabase(store, new RestoreBackupConfiguration
                {
                    BackupLocation = backupLocation,
                    DatabaseName = restoredDatabaseName
                }))
                {
                    using (var session = store.OpenAsyncSession(restoredDatabaseName))
                    {
                        var users = await session.LoadAsync<User>(new[] { "users/1", "users/2" });
                        Assert.NotNull(users["users/1"]);
                        Assert.NotNull(users["users/2"]);
                        Assert.True(users.Any(x => x.Value.Name == "oren"));
                        Assert.True(users.Any(x => x.Value.Name == "ayende"));

                        var val = await session.CountersFor("users/1").GetAsync("likes");
                        Assert.Equal(100, val);
                        val = await session.CountersFor("users/2").GetAsync("downloads");
                        Assert.Equal(200, val);
                    }

                    var stats = await store.Maintenance.SendAsync(new GetStatisticsOperation());
                    Assert.Equal(2, stats.CountOfIndexes);

                    var originalDatabase = await Server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(store.Database);
                    var restoredDatabase = await Server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(restoredDatabaseName);
                    using (restoredDatabase.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext ctx))
                    using (ctx.OpenReadTransaction())
                    {
                        var databaseChangeVector = DocumentsStorage.GetDatabaseChangeVector(ctx);
                        Assert.Contains($"A:8-{originalDatabase.DbBase64Id}", databaseChangeVector);
                        Assert.Contains($"A:10-{restoredDatabase.DbBase64Id}", databaseChangeVector);
                    }
                }
            }
        }

        [Fact, Trait("Category", "Smuggler")]
        public async Task restore_settings_tests()
        {
            var backupPath = NewDataPath(suffix: "BackupFolder");
            using (var store = GetDocumentStore(new Options
            {
                ModifyDatabaseName = s => $"{s}_2"
            }))
            {
                var restoreConfiguration = new RestoreBackupConfiguration();

                var restoreBackupTask = new RestoreBackupOperation(restoreConfiguration);
                var e = Assert.Throws<RavenException>(() => store.Maintenance.Server.Send(restoreBackupTask));
                Assert.Contains("Name cannot be null or empty.", e.InnerException.Message);

                restoreConfiguration.DatabaseName = "abc*^&.";
                restoreBackupTask = new RestoreBackupOperation(restoreConfiguration);
                e = Assert.Throws<RavenException>(() => store.Maintenance.Server.Send(restoreBackupTask));
                Assert.Contains("The name 'abc*^&.' is not permitted. Only letters, digits and characters ('_', '-', '.') are allowed.", e.InnerException.Message);

                restoreConfiguration.DatabaseName = store.Database;
                restoreBackupTask = new RestoreBackupOperation(restoreConfiguration);
                e = Assert.Throws<RavenException>(() => store.Maintenance.Server.Send(restoreBackupTask));
                Assert.Contains("Cannot restore data to an existing database", e.InnerException.Message);

                restoreConfiguration.DatabaseName = "test-" + Guid.NewGuid();
                restoreBackupTask = new RestoreBackupOperation(restoreConfiguration);
                e = Assert.Throws<RavenException>(() => store.Maintenance.Server.Send(restoreBackupTask));
                Assert.Contains("Backup location can't be null or empty", e.InnerException.Message);

                restoreConfiguration.BackupLocation = "this-path-doesn't-exist\\";
                restoreBackupTask = new RestoreBackupOperation(restoreConfiguration);
                e = Assert.Throws<RavenException>(() => store.Maintenance.Server.Send(restoreBackupTask));
                Assert.Contains("Backup location doesn't exist", e.InnerException.Message);

                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "oren" }, "users/1");
                    await session.SaveChangesAsync();
                }

                var config = Backup.CreateBackupConfiguration(backupPath);
                await Backup.UpdateConfigAndRunBackupAsync(Server, config, store);

                restoreConfiguration.BackupLocation = backupPath;
                restoreConfiguration.DataDirectory = backupPath;
                restoreBackupTask = new RestoreBackupOperation(restoreConfiguration);
                e = Assert.Throws<RavenException>(() => store.Maintenance.Server.Send(restoreBackupTask));
                Assert.Contains("New data directory must be empty of any files or folders", e.InnerException.Message);

                // perform restore with a valid db name
                var emptyFolder = NewDataPath(suffix: "BackupFolderRestore123");
                var validDbName = "日本語-שלום-cześć_Привет.123";

                using (Backup.RestoreDatabase(store, new RestoreBackupConfiguration
                {
                    BackupLocation = Directory.GetDirectories(backupPath).First(),
                    DataDirectory = emptyFolder,
                    DatabaseName = validDbName
                }))
                {
                    using (var session = store.OpenAsyncSession(validDbName))
                    {
                        var user = await session.LoadAsync<User>("users/1");
                        Assert.NotNull(user);
                    }
                };
            }
        }

        [Fact, Trait("Category", "Smuggler")]
        public async Task periodic_backup_should_export_starting_from_last_etag()
        {
            //https://issues.hibernatingrhinos.com/issue/RavenDB-11395

            var backupPath = NewDataPath(suffix: "BackupFolder");
            using (var store = GetDocumentStore())
            {
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "oren" }, "users/1");
                    await session.StoreAsync(new User { Name = "aviv" }, "users/2");

                    session.CountersFor("users/1").Increment("likes", 100);
                    session.CountersFor("users/2").Increment("downloads", 200);
                    await session.SaveChangesAsync();
                }

                var config = Backup.CreateBackupConfiguration(backupPath);
                var backupTaskId = await Backup.UpdateConfigAndRunBackupAsync(Server, config, store);

                var exportPath = GetBackupPath(store, backupTaskId, incremental: false);

                using (var store2 = GetDocumentStore())
                {
                    var op = await store2.Smuggler.ImportAsync(new DatabaseSmugglerImportOptions(), exportPath);
                    await op.WaitForCompletionAsync(TimeSpan.FromMinutes(1));

                    var stats = await store2.Maintenance.SendAsync(new GetStatisticsOperation());
                    Assert.Equal(2, stats.CountOfDocuments);
                    Assert.Equal(2, stats.CountOfCounterEntries);

                    using (var session = store2.OpenAsyncSession())
                    {
                        var user1 = await session.LoadAsync<User>("users/1");
                        var user2 = await session.LoadAsync<User>("users/2");

                        Assert.Equal("oren", user1.Name);
                        Assert.Equal("aviv", user2.Name);

                        var dic = await session.CountersFor(user1).GetAllAsync();
                        Assert.Equal(1, dic.Count);
                        Assert.Equal(100, dic["likes"]);

                        dic = await session.CountersFor(user2).GetAllAsync();
                        Assert.Equal(1, dic.Count);
                        Assert.Equal(200, dic["downloads"]);
                    }
                }

                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "ayende" }, "users/3");
                    session.CountersFor("users/3").Increment("votes", 300);
                    await session.SaveChangesAsync();
                }

                var lastEtag = store.Maintenance.Send(new GetStatisticsOperation()).LastDocEtag;
                await Backup.RunBackupAndReturnStatusAsync(Server, backupTaskId, store, isFullBackup: false, expectedEtag: lastEtag);

                exportPath = GetBackupPath(store, backupTaskId);

                using (var store3 = GetDocumentStore())
                {
                    // importing to a new database, in order to verify that
                    // periodic backup imports only the changed documents (and counters)

                    var op = await store3.Smuggler.ImportAsync(new DatabaseSmugglerImportOptions(), exportPath);
                    await op.WaitForCompletionAsync(TimeSpan.FromMinutes(1));

                    var stats = await store3.Maintenance.SendAsync(new GetStatisticsOperation());
                    Assert.Equal(1, stats.CountOfDocuments);

                    Assert.Equal(1, stats.CountOfCounterEntries);

                    using (var session = store3.OpenAsyncSession())
                    {
                        var user3 = await session.LoadAsync<User>("users/3");

                        Assert.Equal("ayende", user3.Name);

                        var dic = await session.CountersFor(user3).GetAllAsync();
                        Assert.Equal(1, dic.Count);
                        Assert.Equal(300, dic["votes"]);
                    }
                }
            }
        }

        [Fact, Trait("Category", "Smuggler")]
        public async Task BackupTaskShouldStayOnTheOriginalNode()
        {
            var backupPath = NewDataPath(suffix: "BackupFolder");
            var cluster = await CreateRaftCluster(5);

            using (var store = GetDocumentStore(new Options
            {
                ReplicationFactor = 5,
                Server = cluster.Leader
            }))
            {
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "oren" }, "users/1");
                    await session.SaveChangesAsync();
                    Assert.True(await WaitForDocumentInClusterAsync<User>(session.Advanced.RequestExecutor.TopologyNodes, "users/1", u => u.Name == "oren",
                        TimeSpan.FromSeconds(15)));
                }

                var operation = new UpdatePeriodicBackupOperation(Backup.CreateBackupConfiguration(backupPath));
                var result = await store.Maintenance.SendAsync(operation);
                var periodicBackupTaskId = result.TaskId;

                await WaitForRaftIndexToBeAppliedInCluster(periodicBackupTaskId, TimeSpan.FromSeconds(15));

                await Backup.RunBackupInClusterAsync(store, result.TaskId, isFullBackup: true);
                await ActionWithLeader(async x => await WaitForRaftCommandToBeAppliedInCluster(x, nameof(UpdatePeriodicBackupStatusCommand)), cluster.Nodes);

                var backupInfo = new GetOngoingTaskInfoOperation(result.TaskId, OngoingTaskType.Backup);
                var backupInfoResult = await store.Maintenance.SendAsync(backupInfo);
                var originalNode = backupInfoResult.ResponsibleNode.NodeTag;

                var toDelete = cluster.Nodes.First(n => n.ServerStore.NodeTag != originalNode);
                await store.Maintenance.Server.SendAsync(new DeleteDatabasesOperation(store.Database, hardDelete: true, fromNode: toDelete.ServerStore.NodeTag, timeToWaitForConfirmation: TimeSpan.FromSeconds(30)));

                var nodesCount = await WaitForValueAsync(async () =>
                {
                    var res = await store.Maintenance.Server.SendAsync(new GetDatabaseRecordOperation(store.Database));
                    if (res == null)
                    {
                        return -1;
                    }

                    return res.Topology.Count;
                }, 4);

                Assert.Equal(4, nodesCount);

                await Backup.RunBackupInClusterAsync(store, result.TaskId, isFullBackup: true);
                await ActionWithLeader(async x => await WaitForRaftCommandToBeAppliedInCluster(x, nameof(UpdatePeriodicBackupStatusCommand)), cluster.Nodes);

                backupInfoResult = await store.Maintenance.SendAsync(backupInfo);
                Assert.Equal(originalNode, backupInfoResult.ResponsibleNode.NodeTag);
            }
        }

        [Fact, Trait("Category", "Smuggler")]
        public async Task CreateFullBackupWithSeveralCompareExchange()
        {
            var backupPath = NewDataPath(suffix: "BackupFolder");
            using (var store = GetDocumentStore())
            {
                var user = new User
                {
                    Name = "💩"
                };
                await store.Operations.SendAsync(new PutCompareExchangeValueOperation<User>("emojis/poo", user, 0));

                var user2 = new User
                {
                    Name = "💩🤡"
                };
                await store.Operations.SendAsync(new PutCompareExchangeValueOperation<User>("emojis/pooclown", user2, 0));

                var config = Backup.CreateBackupConfiguration(backupPath, fullBackupFrequency: "* */6 * * *");
                await Backup.UpdateConfigAndRunBackupAsync(Server, config, store);

                var backupDirectory = Directory.GetDirectories(backupPath).First();
                var databaseName = GetDatabaseName() + "restore";

                var files = Directory.GetFiles(backupDirectory)
                    .Where(BackupUtils.IsBackupFile)
                    .OrderBackups()
                    .ToArray();

                var restoreConfig = new RestoreBackupConfiguration()
                {
                    BackupLocation = backupDirectory,
                    DatabaseName = databaseName,
                    LastFileNameToRestore = files.Last()
                };

                var restoreOperation = new RestoreBackupOperation(restoreConfig);
                store.Maintenance.Server.Send(restoreOperation)
                    .WaitForCompletion(TimeSpan.FromSeconds(30));

                using (var store2 = GetDocumentStore(new Options()
                {
                    CreateDatabase = false,
                    ModifyDatabaseName = s => databaseName
                }))
                {
                    using (var session = store2.OpenAsyncSession(new SessionOptions
                    {
                        TransactionMode = TransactionMode.ClusterWide
                    }))
                    {
                        var user1 = (await session.Advanced.ClusterTransaction.GetCompareExchangeValueAsync<User>("emojis/poo"));
                        var user3 = (await session.Advanced.ClusterTransaction.GetCompareExchangeValueAsync<User>("emojis/pooclown"));
                        Assert.Equal(user.Name, user1.Value.Name);
                        Assert.Equal(user2.Name, user3.Value.Name);
                    }
                }
            }
        }

        [Fact, Trait("Category", "Smuggler")]
        public async Task incremental_and_full_check_last_file_for_backup()
        {
            var backupPath = NewDataPath(suffix: "BackupFolder");
            using (var store = GetDocumentStore())
            {
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "users/1" }, "users/1");
                    await session.SaveChangesAsync();
                }

                var config = Backup.CreateBackupConfiguration(backupPath);
                var backupTaskId = await Backup.UpdateConfigAndRunBackupAsync(Server, config, store);

                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "users/2" }, "users/2");
                    await session.SaveChangesAsync();
                }

                var lastEtag = store.Maintenance.Send(new GetStatisticsOperation()).LastDocEtag;
                await Backup.RunBackupAndReturnStatusAsync(Server, backupTaskId, store, isFullBackup: false, expectedEtag: lastEtag);

                string backupFolder = Directory.GetDirectories(backupPath).OrderBy(Directory.GetCreationTime).Last();
                var lastBackupToRestore = Directory.GetFiles(backupFolder).Where(BackupUtils.IsBackupFile)
                    .OrderBackups()
                    .ToArray();

                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "users/3" }, "users/3");
                    await session.SaveChangesAsync();
                }

                lastEtag = store.Maintenance.Send(new GetStatisticsOperation()).LastDocEtag;
                await Backup.RunBackupAndReturnStatusAsync(Server, backupTaskId, store, isFullBackup: false, expectedEtag: lastEtag);

                var databaseName = GetDatabaseName() + "restore";

                using (Backup.RestoreDatabase(
                    store,
                    new RestoreBackupConfiguration()
                    {
                        BackupLocation = backupFolder,
                        DatabaseName = databaseName,
                        LastFileNameToRestore = lastBackupToRestore.Last()
                    },
                    TimeSpan.FromSeconds(60)))
                {
                    using (var session = store.OpenAsyncSession(databaseName))
                    {
                        var bestUser = await session.LoadAsync<User>("users/1");
                        var mediocreUser1 = await session.LoadAsync<User>("users/2");
                        var mediocreUser2 = await session.LoadAsync<User>("users/3");
                        Assert.NotNull(bestUser);
                        Assert.NotNull(mediocreUser1);
                        Assert.Null(mediocreUser2);
                    }
                }
            }
        }

        [Fact, Trait("Category", "Smuggler")]
        public async Task can_run_incremental_with_no_changes()
        {
            var backupPath = NewDataPath(suffix: "BackupFolder");
            using (var store = GetDocumentStore())
            {
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "users/1" }, "users/1");
                    await session.SaveChangesAsync();
                }

                var config = Backup.CreateBackupConfiguration(backupPath);
                var backupTaskId = await Backup.UpdateConfigAndRunBackupAsync(Server, config, store);

                var status = await Backup.RunBackupAndReturnStatusAsync(Server, backupTaskId, store, isFullBackup: false);
                var value = status.LocalBackup.IncrementalBackupDurationInMs;
                Assert.Equal(0, value);

                string backupFolder = Directory.GetDirectories(backupPath).OrderBy(Directory.GetCreationTime).Last();
                var backupsToRestore = Directory.GetFiles(backupFolder).Where(BackupUtils.IsBackupFile)
                    .OrderBackups()
                    .ToArray();

                Assert.Equal(1, backupsToRestore.Length);

                var databaseName = GetDatabaseName() + "restore";

                using (Backup.RestoreDatabase(
                    store,
                    new RestoreBackupConfiguration()
                    {
                        BackupLocation = backupFolder,
                        DatabaseName = databaseName,
                        LastFileNameToRestore = backupsToRestore.Last()
                    },
                    TimeSpan.FromSeconds(60)))
                {
                    using (var session = store.OpenAsyncSession(databaseName))
                    {
                        var user = await session.LoadAsync<User>("users/1");
                        Assert.NotNull(user);
                    }
                }
            }
        }

        [Fact, Trait("Category", "Smuggler")]
        public async Task FirstBackupWithClusterDownStatusShouldRearrangeTheTimer()
        {
            var backupPath = NewDataPath(suffix: "BackupFolder");
            using (var store = GetDocumentStore(new Options { DeleteDatabaseOnDispose = true, Path = NewDataPath() }))
            {
                var documentDatabase = await GetDatabase(store.Database);
                documentDatabase.PeriodicBackupRunner.ForTestingPurposesOnly().SimulateClusterDownStatus = true;

                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "EGR" }, "users/1");
                    await session.SaveChangesAsync();
                }

                var config = Backup.CreateBackupConfiguration(backupPath, incrementalBackupFrequency: "* * * * *");
                var result = await store.Maintenance.SendAsync(new UpdatePeriodicBackupOperation(config));
                var periodicBackupTaskId = result.TaskId;
                var val = WaitForValue(() => documentDatabase.PeriodicBackupRunner._forTestingPurposes.ClusterDownStatusSimulated, true, timeout: 66666, interval: 333);
                Assert.True(val, "Failed to simulate ClusterDown Status");
                documentDatabase.PeriodicBackupRunner._forTestingPurposes = null;
                var getPeriodicBackupStatus = new GetPeriodicBackupStatusOperation(periodicBackupTaskId);
                val = WaitForValue(() => store.Maintenance.Send(getPeriodicBackupStatus).Status?.LastFullBackup != null, true, timeout: 66666, interval: 333);
                Assert.True(val, "Failed to complete the backup in time");
            }
        }

        [Fact, Trait("Category", "Smuggler")]
        public async Task can_create_local_snapshot_and_restore_using_restore_point()
        {
            var backupPath = NewDataPath(suffix: "BackupFolder");
            using (var store = GetDocumentStore())
            {
                using (var session = store.OpenSession())
                {
                    session.Store(new User { Name = "oren" }, "users/1");
                    session.CountersFor("users/1").Increment("likes", 100);
                    session.SaveChanges();
                }
                var localSettings = new LocalSettings()
                {
                    FolderPath = backupPath
                };

                var config = Backup.CreateBackupConfiguration(backupPath, backupType: BackupType.Snapshot);
                var backupTaskId = await Backup.UpdateConfigAndRunBackupAsync(Server, config, store);
                var client = store.GetRequestExecutor().HttpClient;

                var data = new StringContent(JsonConvert.SerializeObject(localSettings), Encoding.UTF8, "application/json");
                var response = await client.PostAsync(store.Urls.First() + "/admin/restore/points?type=Local ", data);
                string result = response.Content.ReadAsStringAsync().Result;
                var restorePoints = JsonConvert.DeserializeObject<RestorePoints>(result);
                Assert.Equal(1, restorePoints.List.Count);
                var point = restorePoints.List.First();
                var backupDirectory = Directory.GetDirectories(backupPath).First();
                var databaseName = $"restored_database-{Guid.NewGuid()}";
                var restoreOperation = await store.Maintenance.Server.SendAsync(new RestoreBackupOperation(new RestoreBackupConfiguration()
                {
                    DatabaseName = databaseName,
                    BackupLocation = backupDirectory,
                    DisableOngoingTasks = true,
                    LastFileNameToRestore = point.FileName,
                }));

                await restoreOperation.WaitForCompletionAsync(TimeSpan.FromSeconds(60));
                using (var store2 = GetDocumentStore(new Options() { CreateDatabase = false, ModifyDatabaseName = s => databaseName }))
                {
                    using (var session = store2.OpenSession(databaseName))
                    {
                        var users = session.Load<User>(new[] { "users/1", "users/2" });
                        Assert.True(users.Any(x => x.Value.Name == "oren"));

                        var val = session.CountersFor("users/1").Get("likes");
                        Assert.Equal(100, val);
                    }

                    var originalDatabase = Server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(store.Database).Result;
                    var restoredDatabase = Server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(databaseName).Result;
                    using (restoredDatabase.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext ctx))
                    using (ctx.OpenReadTransaction())
                    {
                        var databaseChangeVector = DocumentsStorage.GetDatabaseChangeVector(ctx);
                        Assert.Equal($"A:4-{originalDatabase.DbBase64Id}", databaseChangeVector);
                    }
                }
            }
        }

        [Fact, Trait("Category", "Smuggler")]
        public async Task SuccessfulFullBackupAfterAnErrorOneShouldClearTheErrorStatesFromBackupStatusAndLocalBackup()
        {
            var backupPath = NewDataPath(suffix: "BackupFolder");
            using (var store = GetDocumentStore())
            {
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User
                    {
                        Name = "egr"
                    }, "users/1");

                    await session.SaveChangesAsync();
                }

                var config = Backup.CreateBackupConfiguration(backupPath, incrementalBackupFrequency: "0 0 1 1 *", backupEncryptionSettings: new BackupEncryptionSettings()
                {
                    EncryptionMode = EncryptionMode.UseDatabaseKey
                });
                var backupTaskId = await Backup.UpdateConfigAndRunBackupAsync(Server, config, store, opStatus: OperationStatus.Faulted);
                var operation = new GetPeriodicBackupStatusOperation(backupTaskId);
                PeriodicBackupStatus status = store.Maintenance.Send(operation).Status;
                Assert.NotNull(status.Error);
                Assert.NotNull(status.LocalBackup);
                Assert.NotNull(status.LocalBackup.Exception);

                // status.LastFullBackup is only saved if the backup ran successfully
                Assert.Null(status.LastFullBackup);
                Assert.NotNull(status.LastFullBackupInternal);
                var oldLastFullBackupInternal = status.LastFullBackupInternal;
                Assert.True(status.IsFull, "status.IsFull");
                Assert.Null(status.LastEtag);
                Assert.Null(status.FolderName);
                Assert.Null(status.LastIncrementalBackup);
                Assert.Null(status.LastIncrementalBackupInternal);
                // update LastOperationId even on the task error
                Assert.NotNull(status.LastOperationId);
                var oldOpId = status.LastOperationId;

                Assert.NotNull(status.LastRaftIndex);
                Assert.Null(status.LastRaftIndex.LastEtag);
                Assert.NotNull(status.LocalBackup.LastFullBackup);
                var oldLastFullBackup = status.LastFullBackup;

                Assert.Null(status.LocalBackup.LastIncrementalBackup);
                Assert.NotNull(status.NodeTag);
                Assert.True(status.DurationInMs >= 0, "status.DurationInMs >= 0");
                // update backup task
                config.TaskId = backupTaskId;
                config.BackupEncryptionSettings = null;
                var id = (await store.Maintenance.SendAsync(new UpdatePeriodicBackupOperation(config))).TaskId;
                Assert.Equal(backupTaskId, id);

                status = await Backup.RunBackupAndReturnStatusAsync(Server, backupTaskId, store, isFullBackup: true, expectedEtag: 1);

                Assert.Null(status.Error);
                Assert.NotNull(status.LocalBackup);
                Assert.Null(status.LocalBackup.Exception);

                // status.LastFullBackup is only saved if the backup ran successfully
                Assert.NotNull(status.LastFullBackup);
                Assert.NotNull(status.LastFullBackupInternal);
                Assert.NotEqual(oldLastFullBackupInternal, status.LastFullBackupInternal);

                Assert.True(status.IsFull, "status.IsFull");
                Assert.Equal(1, status.LastEtag);
                Assert.NotNull(status.FolderName);
                Assert.Null(status.LastIncrementalBackup);
                Assert.Null(status.LastIncrementalBackupInternal);
                Assert.NotNull(status.LastOperationId);
                Assert.NotEqual(oldOpId, status.LastOperationId);
                Assert.NotNull(status.LastRaftIndex);
                Assert.NotNull(status.LastRaftIndex.LastEtag);
                Assert.NotNull(status.LocalBackup.LastFullBackup);
                Assert.NotEqual(oldLastFullBackup, status.LocalBackup.LastFullBackup);
                Assert.Null(status.LocalBackup.LastIncrementalBackup);
                Assert.NotNull(status.NodeTag);
                Assert.True(status.DurationInMs > 0, "status.DurationInMs > 0");
            }
        }

        [Fact, Trait("Category", "Smuggler")]
        public async Task ShouldRearrangeTheTimeIfBackupAfterTimerCallbackGotActiveByOtherNode()
        {
            var backupPath = NewDataPath(suffix: "BackupFolder");
            using (var server = GetNewServer())
            using (var store = GetDocumentStore(new Options
            {
                Server = server
            }))
            {
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "EGR" }, "users/1");
                    await session.SaveChangesAsync();
                }

                while (DateTime.Now.Second > 55)
                    await Task.Delay(1000);

                await store.Maintenance.Server.SendAsync(new PutServerWideBackupConfigurationOperation(new ServerWideBackupConfiguration
                {
                    FullBackupFrequency = "*/1 * * * *",
                    LocalSettings = new LocalSettings { FolderPath = backupPath },
                }));

                var record1 = await store.Maintenance.Server.SendAsync(new GetDatabaseRecordOperation(store.Database));
                var backups1 = record1.PeriodicBackups;
                Assert.Equal(1, backups1.Count);

                var taskId = backups1.First().TaskId;
                var responsibleDatabase = await server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(store.Database).ConfigureAwait(false);
                Assert.NotNull(responsibleDatabase);
                var tag = responsibleDatabase.PeriodicBackupRunner.WhoseTaskIsIt(taskId);
                Assert.Equal(server.ServerStore.NodeTag, tag);

                responsibleDatabase.PeriodicBackupRunner.ForTestingPurposesOnly().SimulateActiveByOtherNodeStatus = true;
                var pb = responsibleDatabase.PeriodicBackupRunner.PeriodicBackups.First();
                Assert.NotNull(pb);

                var val = WaitForValue(() => pb.HasScheduledBackup(), false, timeout: 66666, interval: 444);
                Assert.False(val, "PeriodicBackup should cancel the ScheduledBackup if the task status is ActiveByOtherNode, " +
                                  "so when the task status is back to be ActiveByCurrentNode, UpdateConfigurations will be able to reassign the backup timer");

                responsibleDatabase.PeriodicBackupRunner._forTestingPurposes = null;
                responsibleDatabase.PeriodicBackupRunner.UpdateConfigurations(record1);
                var getPeriodicBackupStatus = new GetPeriodicBackupStatusOperation(taskId);

                val = WaitForValue(() => store.Maintenance.Send(getPeriodicBackupStatus).Status?.LastFullBackup != null, true, timeout: 66666, interval: 444);
                Assert.True(val, "Failed to complete the backup in time");
            }
        }

        [Fact, Trait("Category", "Smuggler")]
        public async Task can_move_database_with_backup()
        {
            DoNotReuseServer();

            var cluster = await CreateRaftCluster(2);
            var databaseName = GetDatabaseName();
            await CreateDatabaseInCluster(databaseName, 2, cluster.Nodes[0].WebUrl);

            var backupPath = NewDataPath(suffix: "BackupFolder");
            using (DocumentStore store = new DocumentStore
            {
                Urls = new[]
                {
                    cluster.Nodes[0].WebUrl,
                    cluster.Nodes[1].WebUrl
                },
                Database = databaseName
            })
            {
                store.Initialize();
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "Grisha" }, "users/1");
                    session.Advanced.WaitForReplicationAfterSaveChanges(replicas: 1);
                    await session.SaveChangesAsync();
                }

                var config = Backup.CreateBackupConfiguration(backupPath, incrementalBackupFrequency: "* * * * *");
                var backupTaskId = await Backup.CreateAndRunBackupInClusterAsync(config, store);
                var responsibleNode = await Backup.GetBackupResponsibleNode(cluster.Leader, backupTaskId, databaseName, keepTaskOnOriginalMemberNode: true);

                store.Maintenance.Server.Send(new DeleteDatabasesOperation(databaseName, hardDelete: true, fromNode: responsibleNode,
                    timeToWaitForConfirmation: TimeSpan.FromSeconds(30)));
                var value = await WaitForValueAsync(async () =>
                {
                    var dbRecord = await store.Maintenance.Server.SendAsync(new GetDatabaseRecordOperation(databaseName));
                    return dbRecord?.DeletionInProgress == null || dbRecord.DeletionInProgress.Count == 0;
                }, true, interval: 1000);

                Assert.True(value);
                var server = cluster.Nodes.FirstOrDefault(x => x.ServerStore.NodeTag == responsibleNode == false);
                server.ServerStore.LicenseManager.LicenseStatus.Attributes["highlyAvailableTasks"] = false;

                var newResponsibleNode = await Backup.GetBackupResponsibleNode(server, backupTaskId, databaseName, keepTaskOnOriginalMemberNode: true);
                Assert.Equal(server.ServerStore.NodeTag, newResponsibleNode);
                Assert.NotEqual(responsibleNode, newResponsibleNode);
            }
        }

        [Fact, Trait("Category", "Smuggler")]
        public async Task Backup_WhenContainRevisionWithoutConfiguration_ShouldBackupRevisions()
        {
            var backupPath = NewDataPath(suffix: "BackupFolder");

            var userForFullBackup = new User();
            var userForIncrementalBackup = new User();
            using (var src = GetDocumentStore())
            {
                using (var session = src.OpenAsyncSession())
                {
                    await session.StoreAsync(userForFullBackup);
                    await session.StoreAsync(userForIncrementalBackup);
                    await session.SaveChangesAsync();

                    session.Advanced.Revisions.ForceRevisionCreationFor(userForFullBackup.Id);
                    await session.SaveChangesAsync();
                }

                var config = Backup.CreateBackupConfiguration(backupPath, incrementalBackupFrequency: "* * * * *");
                var backupTaskId = await Backup.UpdateConfigAndRunBackupAsync(Server, config, src);

                using (var session = src.OpenAsyncSession())
                {
                    session.Advanced.Revisions.ForceRevisionCreationFor(userForIncrementalBackup.Id);
                    await session.SaveChangesAsync();
                }
                await Backup.RunBackupAsync(Server, backupTaskId, src, isFullBackup: false);
            }

            using (var dest = GetDocumentStore())
            {
                string fromDirectory = Directory.GetDirectories(backupPath).First();
                await dest.Smuggler.ImportIncrementalAsync(new DatabaseSmugglerImportOptions(), fromDirectory);
                using (var session = dest.OpenAsyncSession())
                {
                    await AssertRevisions(userForFullBackup.Id);
                    await AssertRevisions(userForIncrementalBackup.Id);

                    async Task AssertRevisions(string id)
                    {
                        var revision = await session.Advanced.Revisions.GetForAsync<User>(id);
                        Assert.NotNull(revision);
                        Assert.NotEmpty(revision);
                    }
                }
            }
        }

        [Fact, Trait("Category", "Smuggler")]
        public async Task Should_throw_on_document_with_changed_collection_when_no_tombstones_processed()
        {
            var backupPath = NewDataPath(suffix: "BackupFolder");
            using (var store = GetDocumentStore())
            {
                const string documentId = "Users/1";
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "Grisha" }, documentId);
                    await session.SaveChangesAsync();
                }

                var config = Backup.CreateBackupConfiguration(backupPath);
                var backupTaskId = await Backup.UpdateConfigAndRunBackupAsync(Server, config, store);
                var fullBackupOpId = (await store.Maintenance.SendAsync(new GetPeriodicBackupStatusOperation(backupTaskId))).Status.LastOperationId;
                Assert.NotNull(fullBackupOpId);

                using (var session = store.OpenAsyncSession())
                {
                    session.Delete(documentId);
                    await session.SaveChangesAsync();
                }

                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new Person { Name = "Grisha" }, documentId);
                    await session.SaveChangesAsync();
                }

                var status = await Backup.RunBackupAndReturnStatusAsync(Server, backupTaskId, store, isFullBackup: false);
                Assert.NotNull(status.LastOperationId);
                Assert.NotEqual(fullBackupOpId, status.LastOperationId);

                // to have a different count of docs in databases
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new Person { Name = "EGOR" }, "Users/2");
                    await session.SaveChangesAsync();
                }

                string backupFolder = Directory.GetDirectories(backupPath).OrderBy(Directory.GetCreationTime).Last();
                var backupFilesToRestore = Directory.GetFiles(backupFolder).Where(BackupUtils.IsBackupFile)
                    .OrderBackups()
                    .ToArray();

                var databaseName = GetDatabaseName() + "_restore";
                using (EnsureDatabaseDeletion(databaseName, store))
                {
                    var restoreOperation = new RestoreBackupOperation(new RestoreBackupConfiguration
                    {
                        BackupLocation = backupFolder,
                        DatabaseName = databaseName,
                        LastFileNameToRestore = backupFilesToRestore.First()
                    });

                    var operation = await store.Maintenance.Server.SendAsync(restoreOperation);
                    var res = (RestoreResult)await operation.WaitForCompletionAsync(TimeSpan.FromSeconds(60));
                    Assert.Equal(1, res.Documents.ReadCount);

                    using (var session = store.OpenAsyncSession(databaseName))
                    {
                        var bestUser = await session.LoadAsync<User>("users/1");
                        Assert.NotNull(bestUser);
                        Assert.Equal("Grisha", bestUser.Name);

                        var metadata = session.Advanced.GetMetadataFor(bestUser);
                        var expectedCollection = store.Conventions.GetCollectionName(typeof(User));
                        Assert.True(metadata.TryGetValue(Constants.Documents.Metadata.Collection, out string collection));
                        Assert.Equal(expectedCollection, collection);
                        var stats = store.Maintenance.ForDatabase(databaseName).Send(new GetStatisticsOperation());
                        Assert.Equal(1, stats.CountOfDocuments);
                    }

                    var options = new DatabaseSmugglerImportOptions();
                    options.OperateOnTypes &= ~DatabaseItemType.Tombstones;
                    var opRes = await store.Smuggler.ForDatabase(databaseName).ImportAsync(options, backupFilesToRestore.Last());
                    await Assert.ThrowsAsync<DocumentCollectionMismatchException>(async () => await opRes.WaitForCompletionAsync(TimeSpan.FromSeconds(60)));
                }
            }
        }

        [Fact, Trait("Category", "Smuggler")]
        public async Task Can_restore_backup_when_document_changed_collection_between_backups()
        {
            var backupPath = NewDataPath(suffix: "BackupFolder");
            using (var store = GetDocumentStore())
            {
                const string documentId = "Users/1";
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "Grisha" }, documentId);
                    await session.SaveChangesAsync();
                }

                var config = Backup.CreateBackupConfiguration(backupPath);
                var backupTaskId = await Backup.UpdateConfigAndRunBackupAsync(Server, config, store);
                var fullBackupOpId = (await store.Maintenance.SendAsync(new GetPeriodicBackupStatusOperation(backupTaskId))).Status.LastOperationId;
                Assert.NotNull(fullBackupOpId);

                using (var session = store.OpenAsyncSession())
                {
                    session.Delete(documentId);
                    await session.SaveChangesAsync();
                }

                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new Person { Name = "Grisha" }, documentId);
                    await session.SaveChangesAsync();
                }

                using (var session = store.OpenAsyncSession())
                {
                    session.Delete(documentId);
                    await session.SaveChangesAsync();
                }
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new PersonWithAddress { Name = "Grisha" }, documentId);
                    await session.SaveChangesAsync();
                }

                var status = await Backup.RunBackupAndReturnStatusAsync(Server, backupTaskId, store, isFullBackup: false);
                Assert.NotNull(status.LastOperationId);
                Assert.NotEqual(fullBackupOpId, status.LastOperationId);

                // to have a different count of docs in databases
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new Person { Name = "EGOR" }, "Users/2");
                    await session.SaveChangesAsync();
                }

                string backupFolder = Directory.GetDirectories(backupPath).OrderBy(Directory.GetCreationTime).Last();
                var lastBackupToRestore = Directory.GetFiles(backupFolder).Where(BackupUtils.IsBackupFile)
                    .OrderBackups()
                    .ToArray();

                var databaseName = GetDatabaseName() + "_restore";
                using (EnsureDatabaseDeletion(databaseName, store))
                {
                    var restoreOperation = new RestoreBackupOperation(new RestoreBackupConfiguration
                    {
                        BackupLocation = backupFolder,
                        DatabaseName = databaseName,
                        LastFileNameToRestore = lastBackupToRestore.Last()
                    });

                    var operation = await store.Maintenance.Server.SendAsync(restoreOperation);
                    var res = (RestoreResult)await operation.WaitForCompletionAsync(TimeSpan.FromSeconds(60));
                    Assert.Equal(2, res.Documents.ReadCount);
                    Assert.Equal(2, res.Tombstones.ReadCount);

                    using (var session = store.OpenAsyncSession(databaseName))
                    {
                        var bestUser = await session.LoadAsync<PersonWithAddress>("users/1");
                        Assert.NotNull(bestUser);
                        Assert.Equal("Grisha", bestUser.Name);

                        var metadata = session.Advanced.GetMetadataFor(bestUser);
                        var expectedCollection = store.Conventions.GetCollectionName(typeof(PersonWithAddress));
                        Assert.True(metadata.TryGetValue(Constants.Documents.Metadata.Collection, out string collection));
                        Assert.Equal(expectedCollection, collection);
                        var stats = store.Maintenance.ForDatabase(databaseName).Send(new GetStatisticsOperation());
                        Assert.Equal(1, stats.CountOfDocuments);
                    }
                }
            }
        }

        [Fact, Trait("Category", "Smuggler")]
        public async Task Can_restore_snapshot_when_document_changed_collection_between_backups()
        {
            var backupPath = NewDataPath(suffix: "BackupFolder");
            using (var store = GetDocumentStore())
            {
                const string documentId = "Users/1";
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "Grisha" }, documentId);
                    await session.SaveChangesAsync();
                }

                var config = Backup.CreateBackupConfiguration(backupPath, backupType: BackupType.Snapshot);
                var backupTaskId = await Backup.UpdateConfigAndRunBackupAsync(Server, config, store);
                var fullBackupOpId = (await store.Maintenance.SendAsync(new GetPeriodicBackupStatusOperation(backupTaskId))).Status.LastOperationId;
                Assert.NotNull(fullBackupOpId);

                using (var session = store.OpenAsyncSession())
                {
                    session.Delete(documentId);
                    await session.SaveChangesAsync();
                }

                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new Person { Name = "Grisha" }, documentId);
                    await session.SaveChangesAsync();
                }

                using (var session = store.OpenAsyncSession())
                {
                    session.Delete(documentId);
                    await session.SaveChangesAsync();
                }
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new PersonWithAddress { Name = "Grisha" }, documentId);
                    await session.SaveChangesAsync();
                }

                var status = await Backup.RunBackupAndReturnStatusAsync(Server, backupTaskId, store, isFullBackup: false);
                Assert.NotNull(status.LastOperationId);
                Assert.NotEqual(fullBackupOpId, status.LastOperationId);

                // to have a different count of docs in databases
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new Person { Name = "EGOR" }, "Users/2");
                    await session.SaveChangesAsync();
                }

                string backupFolder = Directory.GetDirectories(backupPath).OrderBy(Directory.GetCreationTime).Last();
                var lastBackupToRestore = Directory.GetFiles(backupFolder).Where(BackupUtils.IsBackupFile)
                    .OrderBackups()
                    .ToArray();

                var databaseName = GetDatabaseName() + "_restore";
                using (EnsureDatabaseDeletion(databaseName, store))
                {
                    var restoreOperation = new RestoreBackupOperation(new RestoreBackupConfiguration
                    {
                        BackupLocation = backupFolder,
                        DatabaseName = databaseName,
                        LastFileNameToRestore = lastBackupToRestore.Last()
                    });

                    var operation = await store.Maintenance.Server.SendAsync(restoreOperation);
                    var res = (RestoreResult)await operation.WaitForCompletionAsync(TimeSpan.FromSeconds(60));
                    Assert.Equal(2, res.Documents.ReadCount);
                    Assert.Equal(2, res.Tombstones.ReadCount);

                    using (var session = store.OpenAsyncSession(databaseName))
                    {
                        var bestUser = await session.LoadAsync<PersonWithAddress>("users/1");
                        Assert.NotNull(bestUser);
                        Assert.Equal("Grisha", bestUser.Name);

                        var metadata = session.Advanced.GetMetadataFor(bestUser);
                        var expectedCollection = store.Conventions.GetCollectionName(typeof(PersonWithAddress));
                        Assert.True(metadata.TryGetValue(Constants.Documents.Metadata.Collection, out string collection));
                        Assert.Equal(expectedCollection, collection);
                        var stats = store.Maintenance.ForDatabase(databaseName).Send(new GetStatisticsOperation());
                        Assert.Equal(1, stats.CountOfDocuments);
                    }
                }
            }
        }

        [Fact, Trait("Category", "Smuggler")]
        public async Task Can_restore_backup_when_document_with_attachment_changed_collection_between_backups()
        {
            var backupPath = NewDataPath(suffix: "BackupFolder");
            using (var store = GetDocumentStore())
            {
                const string documentId = "Users/1";
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "Grisha" }, documentId);
                    await session.SaveChangesAsync();
                }

                var config = Backup.CreateBackupConfiguration(backupPath, backupType: BackupType.Snapshot);
                var backupTaskId = await Backup.UpdateConfigAndRunBackupAsync(Server, config, store);
                var fullBackupOpId = (await store.Maintenance.SendAsync(new GetPeriodicBackupStatusOperation(backupTaskId))).Status.LastOperationId;
                Assert.NotNull(fullBackupOpId);

                using (var session = store.OpenAsyncSession())
                {
                    session.Delete(documentId);
                    await session.SaveChangesAsync();
                }

                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new Person { Name = "Grisha" }, documentId);
                    await session.SaveChangesAsync();
                }

                using (var profileStream = new MemoryStream(new byte[] {1, 2, 3}))
                {
                    var result = store.Operations.Send(new PutAttachmentOperation(documentId, "test_attachment", profileStream, "image/png"));
                    Assert.Equal("test_attachment", result.Name);
                }

                var status = await Backup.RunBackupAndReturnStatusAsync(Server, backupTaskId, store, isFullBackup: false);
                Assert.NotNull(status.LastOperationId);
                Assert.NotEqual(fullBackupOpId, status.LastOperationId);

                // to have a different count of docs in databases
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new Person { Name = "EGOR" }, "Users/2");
                    await session.SaveChangesAsync();
                }

                string backupFolder = Directory.GetDirectories(backupPath).OrderBy(Directory.GetCreationTime).Last();
                var lastBackupToRestore = Directory.GetFiles(backupFolder).Where(BackupUtils.IsBackupFile)
                    .OrderBackups()
                    .ToArray();

                var databaseName = GetDatabaseName() + "_restore";
                using (EnsureDatabaseDeletion(databaseName, store))
                {
                    var restoreOperation = new RestoreBackupOperation(new RestoreBackupConfiguration
                    {
                        BackupLocation = backupFolder,
                        DatabaseName = databaseName,
                        LastFileNameToRestore = lastBackupToRestore.Last()
                    });

                    var operation = await store.Maintenance.Server.SendAsync(restoreOperation);
                    var res = (RestoreResult)await operation.WaitForCompletionAsync(TimeSpan.FromSeconds(60));
                    Assert.Equal(2, res.Documents.ReadCount);
                    Assert.Equal(1, res.Tombstones.ReadCount);
                    WaitForUserToContinueTheTest(store);
                    using (var session = store.OpenAsyncSession(databaseName))
                    {
                        var bestUser = await session.LoadAsync<Person>("users/1");
                        Assert.NotNull(bestUser);
                        Assert.Equal("Grisha", bestUser.Name);

                        var metadata = session.Advanced.GetMetadataFor(bestUser);
                        var expectedCollection = store.Conventions.GetCollectionName(typeof(Person));
                        Assert.True(metadata.TryGetValue(Constants.Documents.Metadata.Collection, out string collection));
                        Assert.Equal(expectedCollection, collection);
                        var stats = store.Maintenance.ForDatabase(databaseName).Send(new GetStatisticsOperation());
                        Assert.Equal(1, stats.CountOfDocuments);
                        Assert.Equal(1, stats.CountOfAttachments);
                    }
                }
            }
        }

        private static string GetBackupPath(IDocumentStore store, long backTaskId, bool incremental = true)
        {
            var status = store.Maintenance.Send(new GetPeriodicBackupStatusOperation(backTaskId)).Status;

            var backupDirectory = status.LocalBackup.BackupDirectory;

            string datePrefix;
            if (incremental)
            {
                Debug.Assert(status.LastIncrementalBackup.HasValue);
                datePrefix = status.LastIncrementalBackup.Value.ToLocalTime().ToString("yyyy-MM-dd-HH-mm-ss");
            }
            else
            {
                var folderName = status.FolderName;
                var indexOf = folderName.IndexOf(".", StringComparison.OrdinalIgnoreCase);
                Debug.Assert(indexOf != -1);
                datePrefix = folderName.Substring(0, indexOf);
            }

            var fileExtension = incremental
                ? Constants.Documents.PeriodicBackup.IncrementalBackupExtension
                : Constants.Documents.PeriodicBackup.FullBackupExtension;

            return Path.Combine(backupDirectory, $"{datePrefix}{fileExtension}");
        }

        private static IDisposable ReadOnly(string path)
        {
            var files = Directory.GetFiles(path);
            var attributes = new FileInfo(files[0]).Attributes;
            foreach (string file in files)
            {
                File.SetAttributes(file, FileAttributes.ReadOnly);
            }

            return new DisposableAction(() =>
            {
                foreach (string file in files)
                {
                    File.SetAttributes(file, attributes);
                }
            });
        }
    }
}
