using System;
using System.Diagnostics;
using System.Threading.Tasks;
using FastTests;
using FastTests.Blittable;
using FastTests.Client;
using RachisTests;
using SlowTests.Client.Attachments;
using SlowTests.Client.TimeSeries.Replication;
using SlowTests.Issues;
using SlowTests.MailingList;
using SlowTests.Rolling;
using SlowTests.Server.Documents.ETL.Raven;
using SlowTests.Server.Replication;
using Tests.Infrastructure;

namespace Tryouts
{
    public static class Program
    {
        static Program()
        {
            XunitLogging.RedirectStreams = false;
        }

        public static async Task Main(string[] args)
        {
            Console.WriteLine(Process.GetCurrentProcess().Id);
            for (int i = 0; i < 10_000; i++)
            {
                Console.WriteLine($"Starting to run {i}");
                try
                {
                    using (var testOutputHelper = new ConsoleTestOutputHelper())
                    using (var test = new RavenDB_20940(testOutputHelper))
                    {
                        await test.ConflictBetweenDocumentAndTombstoneShouldUpdateMapIndex(RavenTestBase.Options.ForMode(RavenDatabaseMode.Single));
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
    }
}
