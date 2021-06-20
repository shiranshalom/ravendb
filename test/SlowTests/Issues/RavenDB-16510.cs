﻿using System;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using System.Threading;
using FastTests.Server.Replication;
using Newtonsoft.Json.Linq;
using Raven.Client.Extensions;
using Raven.Tests.Core.Utils.Entities;
using Sparrow;

namespace SlowTests.Issues
{
    public class RavenDB_16510 : ReplicationTestBase
    {
        public RavenDB_16510(ITestOutputHelper output) : base(output)
        {
        }

        [Fact]
        public async Task CheckTcpTrafficWatch()
        {
            DoNotReuseServer();

            using (var store1 = GetDocumentStore())
            using (var store2 = GetDocumentStore())
            {
                var cts = new CancellationTokenSource();

                var readFromSocketTask = Task.Run(async () =>
                {
                    using (var clientWebSocket = new ClientWebSocket())
                    {
                        string url = store1.Urls.First().Replace("http://", "ws://");
                        await clientWebSocket.ConnectAsync(new Uri($"{url}/admin/traffic-watch"), cts.Token);
                        Assert.Equal(WebSocketState.Open, clientWebSocket.State);

                        var arraySegment = new ArraySegment<byte>(new byte[512]);
                        var buffer = new StringBuilder();
                        var charBuffer = new char[Encodings.Utf8.GetMaxCharCount(arraySegment.Count)];

                        while (cts.IsCancellationRequested == false)
                        {
                            buffer.Length = 0;
                            WebSocketReceiveResult recvResult;

                            do
                            {
                                recvResult = await clientWebSocket.ReceiveAsync(arraySegment, cts.Token);
                                var chars = Encodings.Utf8.GetChars(arraySegment.Array, 0, recvResult.Count, charBuffer, 0);
                                buffer.Append(charBuffer, 0, chars);
                            } while (!recvResult.EndOfMessage);

                            if (recvResult.Count > 2) // --> ignore "\r\n" messages
                            {
                                var msg = buffer.ToString();
                                JObject json = JObject.Parse(msg);

                                if (json.HasValues && json.Value<string>("TrafficWatchType").Equals("Tcp"))
                                {
                                    Assert.True(json.Value<string>("DatabaseName").Equals(store1.Database));
                                    Assert.True(json.Value<string>("Operation").Equals("Replication"));
                                    cts.Cancel();
                                }
                            }
                        }
                    }
                });

                await SetupReplicationAsync(store2, store1);
                using (var session = store2.OpenSession())
                {
                    session.Store(new User { Name = "Jane Dow", Age = 31 }, "users/2");
                    session.SaveChanges();
                }

                cts.CancelAfter(TimeSpan.FromMinutes(1));

                Assert.True(await readFromSocketTask.WaitWithTimeout(TimeSpan.FromMinutes(2)));
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task CheckTcpTrafficWatchExceptionMessage(bool exceptionType)
        {
            //var server = GetNewServer();
            DoNotReuseServer();

            using (var store1 = GetDocumentStore())
            using (var store2 = GetDocumentStore())
            {
                var cts = new CancellationTokenSource();

                var readFromSocketTask = Task.Run(async () =>
                {
                    using (var clientWebSocket = new ClientWebSocket())
                    {
                        string url = store1.Urls.First().Replace("http://", "ws://");
                        await clientWebSocket.ConnectAsync(new Uri($"{url}/admin/traffic-watch"), cts.Token);
                        Assert.Equal(WebSocketState.Open, clientWebSocket.State);

                        var arraySegment = new ArraySegment<byte>(new byte[512]);
                        var buffer = new StringBuilder();
                        var charBuffer = new char[Encodings.Utf8.GetMaxCharCount(arraySegment.Count)];

                        while (cts.IsCancellationRequested == false)
                        {
                            buffer.Length = 0;
                            WebSocketReceiveResult result;

                            do
                            {
                                result = await clientWebSocket.ReceiveAsync(arraySegment, cts.Token);
                                var chars = Encodings.Utf8.GetChars(arraySegment.Array, 0, result.Count, charBuffer, 0);
                                buffer.Append(charBuffer, 0, chars);
                            } while (!result.EndOfMessage);

                            if (result.Count > 2)
                            {
                                var msg = buffer.ToString();
                                JObject json = JObject.Parse(msg);
                                var msgType = json.Value<string>("TrafficWatchType");
                                var customInfo = json.Value<string>("CustomInfo");
                                if (msgType.Equals("Tcp") && customInfo != null)
                                {
                                    Assert.True(customInfo.Contains("Simulated TCP failure."));
                                    return;
                                }
                            }
                        }
                    }
                });

                if (exceptionType)
                    Server.ForTestingPurposesOnly().ThrowExceptionInListenToNewTcpConnection = true;
                else
                    Server.ForTestingPurposesOnly().ThrowExceptionInTrafficWatchTcp = true;

                await SetupReplicationAsync(store2, store1);

                using (var session = store2.OpenSession())
                {
                    session.Store(new User { Name = "Jane Dow", Age = 31 }, "users/2");
                    session.SaveChanges();
                }

                cts.CancelAfter(TimeSpan.FromMinutes(1));

                Assert.True(await readFromSocketTask.WaitWithTimeout(TimeSpan.FromMinutes(2)));
            }
        }
    }
}
