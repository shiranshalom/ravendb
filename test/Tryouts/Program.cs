using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using FastTests;
using Tests.Infrastructure;
using Raven.Server.Utils;
using SlowTests.Corax;
using SlowTests.Sharding.Cluster;
using Xunit;
using FastTests.Voron.Util;
using SlowTests.Sharding.Replication;

namespace Tryouts;

public static class Program
{
    static Program()
    {
        XunitLogging.RedirectStreams = false;
    }

    public static async Task Main(string[] args)
    {
        Console.WriteLine(Process.GetCurrentProcess().Id);

        for (int i = 0; i < 1000; i++)
        {
            Console.WriteLine($"Starting to run {i}");

            try
            {
                using (var testOutputHelper = new ConsoleTestOutputHelper())
                using (var test = new ShardedExternalReplicationTests(testOutputHelper))
                {
                    DebuggerAttachedTimeout.DisableLongTimespan = true;
                    //test.CanRoundTripSmallContainer("GreaterThan42B");
                    await test.BidirectionalReplicationWithReshardingShouldWork(RavenTestBase.Options.ForMode(RavenDatabaseMode.Sharded), RavenTestBase.Options.ForMode(RavenDatabaseMode.Sharded));
                }
            }
            catch (Exception e)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(e);
                Console.ForegroundColor = ConsoleColor.White;
                return;
            }
        }
    }

    private static void TryRemoveDatabasesFolder()
    {
        var p = System.AppDomain.CurrentDomain.BaseDirectory;
        var dbPath = Path.Combine(p, "Databases");
        if (Directory.Exists(dbPath))
        {
            try
            {
                Directory.Delete(dbPath, true);
                Assert.False(Directory.Exists(dbPath), "Directory.Exists(dbPath)");
            }
            catch
            {
                Console.WriteLine($"Could not remove Databases folder on path '{dbPath}'");
            }
        }
    }
}
