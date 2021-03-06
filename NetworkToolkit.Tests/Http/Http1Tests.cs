﻿using NetworkToolkit.Connections;
using NetworkToolkit.Http.Primitives;
using NetworkToolkit.Tests.Http.Servers;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace NetworkToolkit.Tests.Http
{
    public class Http1Tests : HttpGenericTests
    {
        [Fact]
        public async Task Receive_LWS_Success()
        {
            await RunSingleStreamTest(
                async (clientRequest, serverUri) =>
                {
                    clientRequest.ConfigureRequest(contentLength: 0, hasTrailingHeaders: false);
                    clientRequest.WriteRequest(HttpMethod.Get, serverUri);
                    await clientRequest.CompleteRequestAsync();

                    TestHeadersSink responseHeaders = await clientRequest.ReadAllHeadersAsync();
                    Assert.Equal("foo   bar   baz", responseHeaders.GetSingleValue("X-Test-Header"));
                },
                async serverStream =>
                {
                    HttpTestFullRequest request = await serverStream.ReceiveFullRequestAsync();
                    await ((Http1TestStream)serverStream).SendRawResponseAsync("HTTP/1.1 200 OK\r\nX-Test-Header: foo\r\n\tbar\r\n baz\r\n\r\n");
                });
        }

        /// <summary>
        /// Tests that requests are not returned until the prior request finishes writing.
        /// </summary>
        [Fact]
        public async Task Pipelining_PausedWrites_Success()
        {
            const int PipelineLength = 10;

            await RunMultiStreamTest(
                async (client, serverUri) =>
                {
                    ValueHttpRequest prev = (await client.CreateNewRequestAsync(Version, HttpVersionPolicy.RequestVersionExact)).Value;

                    for (int i = 1; i < PipelineLength; ++i)
                    {
                        ValueTask<ValueHttpRequest?> nextTask = client.CreateNewRequestAsync(Version, HttpVersionPolicy.RequestVersionExact);

                        prev.ConfigureRequest(contentLength: 0, hasTrailingHeaders: false);
                        prev.WriteRequest(HttpMethod.Get, serverUri);

                        Assert.False(nextTask.IsCompleted);
                        await prev.CompleteRequestAsync();
                        Assert.True(nextTask.IsCompleted);

                        await prev.DisposeAsync();
                        prev = (await nextTask).Value;
                    }

                    prev.ConfigureRequest(contentLength: 0, hasTrailingHeaders: false);
                    prev.WriteRequest(HttpMethod.Get, serverUri);
                    await prev.CompleteRequestAsync();
                    await prev.DisposeAsync();
                },
                async server =>
                {
                    for (int i = 0; i < PipelineLength; ++i)
                    {
                        await server.ReceiveAndSendSingleRequestAsync();
                    }
                });
        }

        /// <summary>
        /// Tests that requests can not read until the prior request is disposed.
        /// </summary>
        [Fact]
        public async Task Pipelining_PausedReads_Success()
        {
            if (TrickleForceAsync)
            {
                // This test depends on synchronous completion of reads, so will not work when async completion is forced.
                return;
            }

            const int PipelineLength = 10;

            using var semaphore = new SemaphoreSlim(0);

            await RunMultiStreamTest(
                async (client, serverUri) =>
                {
                    ValueHttpRequest prev = (await client.CreateNewRequestAsync(Version, HttpVersionPolicy.RequestVersionExact)).Value;
                    prev.ConfigureRequest(contentLength: 0, hasTrailingHeaders: false);
                    prev.WriteRequest(HttpMethod.Get, serverUri);
                    await prev.CompleteRequestAsync();
                    await semaphore.WaitAsync();

                    for (int i = 1; i < PipelineLength; ++i)
                    {
                        ValueHttpRequest next = (await client.CreateNewRequestAsync(Version, HttpVersionPolicy.RequestVersionExact)).Value;
                        next.ConfigureRequest(contentLength: 0, hasTrailingHeaders: false);
                        next.WriteRequest(HttpMethod.Get, serverUri);
                        await next.CompleteRequestAsync();
                        await semaphore.WaitAsync();

                        ValueTask<HttpReadType> nextReadTask = next.ReadAsync();

                        // wait for write to complete to guarantee DisposeAsync() will complete synchronously.

                        Assert.False(nextReadTask.IsCompleted);
                        await prev.DisposeAsync();
                        Assert.True(nextReadTask.IsCompleted);
                        Assert.Equal(HttpReadType.FinalResponse, await nextReadTask);

                        prev = next;
                    }

                    await prev.DisposeAsync();
                },
                async server =>
                {
                    for (int i = 0; i < PipelineLength; ++i)
                    {
                        await server.ReceiveAndSendSingleRequestAsync();
                        semaphore.Release();
                    }
                });
        }

        /// <summary>
        /// Queue up 10 requests before writing any responses.
        /// </summary>
        [Fact]
        public async Task Pipelining_Success()
        {
            const int PipelineLength = 10;

            await RunMultiStreamTest(
                async (client, serverUri) =>
                {
                    var tasks = new Task[PipelineLength];

                    for (int i = 0; i < tasks.Length; ++i)
                    {
                        tasks[i] = MakeRequest(i);
                    }

                    await tasks.WhenAllOrAnyFailed(10_000);

                    async Task MakeRequest(int requestNo)
                    {
                        await using ValueHttpRequest request = (await client.CreateNewRequestAsync(Version, HttpVersionPolicy.RequestVersionExact)).Value;

                        request.ConfigureRequest(contentLength: 0, hasTrailingHeaders: false);
                        request.WriteRequest(HttpMethod.Get, serverUri);
                        request.WriteHeader("X-Request-No", requestNo.ToString(CultureInfo.InvariantCulture));
                        await request.CompleteRequestAsync();

                        TestHeadersSink headers = await request.ReadAllHeadersAsync();

                        Assert.Equal(requestNo.ToString(CultureInfo.InvariantCulture), headers.GetSingleValue("X-Response-No"));
                    }
                },
                async server =>
                {
                    var streams = new (HttpTestStream, HttpTestFullRequest)[PipelineLength];

                    for (int i = 0; i < streams.Length; ++i)
                    {
                        HttpTestStream stream = await server.AcceptStreamAsync();
                        HttpTestFullRequest request = await stream.ReceiveFullRequestAsync();
                        Assert.Equal(i.ToString(CultureInfo.InvariantCulture), request.Headers.GetSingleValue("X-Request-No"));
                        streams[i] = (stream, request);
                    }

                    for (int i = 0; i < streams.Length; ++i)
                    {
                        (HttpTestStream stream, HttpTestFullRequest request) = streams[i];

                        var responseHeaders = new TestHeadersSink()
                        {
                            { "X-Response-No", i.ToString(CultureInfo.InvariantCulture) }
                        };

                        await stream.SendResponseAsync(headers: responseHeaders);
                        await stream.DisposeAsync();
                    }
                });
        }

        internal override HttpPrimitiveVersion Version => HttpPrimitiveVersion.Version11;

        internal override async Task<HttpTestServer> CreateTestServerAsync(ConnectionFactory connectionFactory) =>
            new Http1TestServer(await connectionFactory.ListenAsync(options: CreateListenerProperties()).ConfigureAwait(false));

        internal override async Task<HttpConnection> CreateTestClientAsync(ConnectionFactory connectionFactory, EndPoint endPoint) =>
            new Http1Connection(await connectionFactory.ConnectAsync(endPoint, options: CreateConnectProperties()), Version);
    }

    public class Http1SslTests : Http1Tests
    {
        internal override bool UseSsl => true;
    }

    public class Http1TrickleTests : Http1Tests
    {
        internal override bool Trickle => true;
    }

    public class Http1TrickleAsyncTests : Http1TrickleTests
    {
        internal override bool TrickleForceAsync => true;
    }
}
