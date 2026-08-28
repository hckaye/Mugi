using System.Globalization;
using System.Net;
using System.Text;

namespace Miya.IntegrationTests;

public sealed class SseIntegrationTests
{
    private const int TestTimeoutMilliseconds = 10_000;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task EventsAreFlushedToTheClientBeforeTheHandlerCompletes()
    {
        var releaseRemaining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var app = new App();
        app.Get("/sse", context => context.EventStream(async (sse, token) =>
        {
            await sse.Send("one", "tick", "1");
            await releaseRemaining.Task.WaitAsync(token);
            await sse.Send("two", "tick", "2");
            await sse.Send("three", "tick", "3");
        }));

        await using var server = await StartAsync(app);
        await using var connection = await RawHttpConnection.ConnectAsync(server.Addresses[0]);
        await connection.WriteAsync(
            "GET /sse HTTP/1.1\r\n" +
            $"Host: {connection.HostHeader}\r\n" +
            "Connection: close\r\n\r\n");

        var headers = await connection.ReadHeadersAsync();
        Assert.Equal(200, headers.StatusCode);
        Assert.Equal("text/event-stream", headers.Headers["Content-Type"]);
        Assert.Equal("no-cache", headers.Headers["Cache-Control"]);
        Assert.Equal("no", headers.Headers["X-Accel-Buffering"]);

        var reader = new ChunkedSseReader(connection);
        Assert.Equal("event: tick\nid: 1\ndata: one\n\n", await reader.ReadEventAsync());

        releaseRemaining.TrySetResult();
        Assert.Equal("event: tick\nid: 2\ndata: two\n\n", await reader.ReadEventAsync());
        Assert.Equal("event: tick\nid: 3\ndata: three\n\n", await reader.ReadEventAsync());
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task ClientDisconnectCancelsTheNextSendWithoutAServerError()
    {
        var firstFlushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendException = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var app = new App();
        app.Get("/ok", context => context.Text("alive"));
        app.Get("/sse", context => context.EventStream(async (sse, token) =>
        {
            await sse.Send("one");
            firstFlushed.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
            }

            try
            {
                await sse.Send("two");
                sendException.TrySetResult(null);
            }
            catch (Exception exception)
            {
                sendException.TrySetResult(exception);
                throw;
            }
        }));

        await using var server = await StartAsync(app);
        await using (var connection = await RawHttpConnection.ConnectAsync(server.Addresses[0]))
        {
            await connection.WriteAsync(
                "GET /sse HTTP/1.1\r\n" +
                $"Host: {connection.HostHeader}\r\n" +
                "Connection: close\r\n\r\n");

            var headers = await connection.ReadHeadersAsync();
            Assert.Equal(200, headers.StatusCode);
            var reader = new ChunkedSseReader(connection);
            Assert.Equal("data: one\n\n", await reader.ReadEventAsync());
            await firstFlushed.Task.WaitAsync(OperationTimeout);
        }

        var observed = await sendException.Task.WaitAsync(OperationTimeout);
        Assert.IsAssignableFrom<OperationCanceledException>(observed);

        using var client = new HttpClient
        {
            BaseAddress = new Uri(server.Addresses[0]),
            Timeout = OperationTimeout,
        };
        using var response = await client.GetAsync("/ok");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("alive", await response.Content.ReadAsStringAsync());
    }

    private static Task<Server> StartAsync(App app) => app.StartAsync(
        new AppOptions
        {
            Port = 0,
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        });

    private sealed class ChunkedSseReader(RawHttpConnection connection)
    {
        private readonly List<byte> _decoded = [];
        private int _chunkRemaining;
        private bool _inChunk;
        private bool _ended;

        public async Task<string> ReadEventAsync()
        {
            while (true)
            {
                var payload = Encoding.UTF8.GetString([.. _decoded]);
                var terminator = payload.IndexOf("\n\n", StringComparison.Ordinal);
                if (terminator >= 0)
                {
                    var frame = payload[..(terminator + 2)];
                    _decoded.Clear();
                    var remainder = Encoding.UTF8.GetBytes(payload[(terminator + 2)..]);
                    _decoded.AddRange(remainder);
                    return frame;
                }

                if (_ended)
                {
                    throw new EndOfStreamException("The SSE stream ended before a complete event arrived.");
                }

                await ReadMoreAsync();
            }
        }

        private async Task ReadMoreAsync()
        {
            if (!_inChunk)
            {
                var sizeLine = await ReadLineAsync();
                if (sizeLine.Length == 0)
                {
                    throw new InvalidDataException("Unexpected empty chunk-size line.");
                }

                var extension = sizeLine.IndexOf(';');
                var hex = extension >= 0 ? sizeLine[..extension] : sizeLine;
                _chunkRemaining = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (_chunkRemaining == 0)
                {
                    await ReadLineAsync();
                    _ended = true;
                    return;
                }

                _inChunk = true;
            }

            var buffer = new byte[Math.Min(_chunkRemaining, 1024)];
            var read = await connection.ReadBodyAsync(buffer);
            if (read == 0)
            {
                throw new EndOfStreamException("The connection closed before the chunk completed.");
            }

            _decoded.AddRange(buffer.AsSpan(0, read).ToArray());
            _chunkRemaining -= read;
            if (_chunkRemaining == 0)
            {
                var trailer = await ReadLineAsync();
                if (trailer.Length != 0)
                {
                    throw new InvalidDataException("A chunk trailer was not empty.");
                }

                _inChunk = false;
            }
        }

        private async Task<string> ReadLineAsync()
        {
            var bytes = new List<byte>();
            var one = new byte[1];
            while (true)
            {
                var read = await connection.ReadBodyAsync(one);
                if (read == 0)
                {
                    throw new EndOfStreamException("The connection closed before a chunk line completed.");
                }

                bytes.Add(one[0]);
                if (bytes.Count >= 2 && bytes[^2] == (byte)'\r' && bytes[^1] == (byte)'\n')
                {
                    return Encoding.ASCII.GetString(bytes.ToArray(), 0, bytes.Count - 2);
                }
            }
        }
    }
}
