using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using FastTests.Blittable;
using FastTests.Client;
using FastTests.Server.Replication;
using Raven.Server.Documents.Replication;
using Raven.Server.Documents.Replication.ReplicationItems;
using SlowTests.Issues;
using SlowTests.MailingList;
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
                    /*using (var test = new ExternalReplicationTests(testOutputHelper))
                    {
                        await test.NetworkStreamCompressionInReplication(3000);
                    }*/
                    using (var test = new ReplicationBasicTests(testOutputHelper))
                    {
                        await test.Master_slave_replication_from_etag_zero_should_work();
                    }
                }
                catch (Exception e)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine(e);
                    Console.ForegroundColor = ConsoleColor.White;
                }
            }
        }
    }
}
