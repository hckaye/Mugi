using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Miya.IntegrationTests;

public sealed class KestrelIntegrationTests
{
    private const int TestTimeoutMilliseconds = 10_000;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task GetReturnsBodyAndMiddlewareCanAddLateHeader()
    {
        var app = new App();
        app.Use(async (context, next) =>
        {
            await next(context);
            context.Header("Server-Timing", "app;dur=1");
        });
        app.Get("/", context => context.Text("Hello"));

        await using var server = await StartAsync(app);
        using var client = CreateClient(server);
        using var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Hello", await response.Content.ReadAsStringAsync());
        Assert.Equal("app;dur=1", response.Headers.GetValues("Server-Timing").Single());
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task MissingRouteReturnsNotFound()
    {
        var app = new App();
        app.Get("/", context => context.Text("Hello"));

        await using var server = await StartAsync(app);
        using var client = CreateClient(server);
        using var response = await client.GetAsync("/missing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Not Found", await response.Content.ReadAsStringAsync());
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task MethodMismatchReturnsAllowHeader()
    {
        var app = new App();
        app.Get("/", context => context.Text("Hello"));

        await using var server = await StartAsync(app);
        using var client = CreateClient(server);
        using var response = await client.PostAsync("/", content: null);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("GET, HEAD, OPTIONS", response.Content.Headers.Allow.ToString());
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task HeadUsesGetHeadersAndSuppressesBody()
    {
        var app = new App();
        app.Get("/", context =>
        {
            context.Header("X-Route", "root");
            return context.Text("Hello");
        });

        await using var server = await StartAsync(app);
        using var client = CreateClient(server);
        using var get = await client.GetAsync("/");
        using var request = new HttpRequestMessage(HttpMethod.Head, "/");
        using var head = await client.SendAsync(request);

        Assert.Equal(get.StatusCode, head.StatusCode);
        Assert.Equal(get.Content.Headers.ContentLength, head.Content.Headers.ContentLength);
        Assert.Equal(get.Content.Headers.ContentType, head.Content.Headers.ContentType);
        Assert.Equal(get.Headers.GetValues("X-Route"), head.Headers.GetValues("X-Route"));
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task ImplicitOptionsReturnsAllowHeader()
    {
        var app = new App();
        app.Get("/", context => context.Text("Hello"));

        await using var server = await StartAsync(app);
        using var client = CreateClient(server);
        using var request = new HttpRequestMessage(HttpMethod.Options, "/");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("GET, HEAD, OPTIONS", response.Content.Headers.Allow.ToString());
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task ExpectContinueIsSentBeforeRequestBodyIsRead()
    {
        var app = CreateBodyReadingApp();
        await using var server = await StartAsync(app);
        await using var connection = await RawHttpConnection.ConnectAsync(server.Addresses[0]);

        await connection.WriteAsync(
            "POST /read HTTP/1.1\r\n" +
            $"Host: {connection.HostHeader}\r\n" +
            "Content-Length: 5\r\n" +
            "Expect: 100-continue\r\n" +
            "Connection: close\r\n\r\n");

        var interim = await connection.ReadResponseAsync();
        Assert.Equal(100, interim.StatusCode);

        await connection.WriteAsync("hello");
        var response = await connection.ReadResponseAsync();
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("5", response.Body);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task UnconsumedBodyAllowsKeepAliveReuse()
    {
        var app = new App();
        app.Post("/ignore", context => context.Text("ignored"));
        app.Get("/", context => context.Text("Hello"));

        await using var server = await StartAsync(app);
        await using var connection = await RawHttpConnection.ConnectAsync(server.Addresses[0]);
        await connection.WriteAsync(
            "POST /ignore HTTP/1.1\r\n" +
            $"Host: {connection.HostHeader}\r\n" +
            "Content-Length: 5\r\n" +
            "Connection: keep-alive\r\n\r\n" +
            "abcde");

        var first = await connection.ReadResponseAsync();
        Assert.Equal(200, first.StatusCode);
        Assert.Equal("ignored", first.Body);

        await connection.WriteAsync(
            "GET / HTTP/1.1\r\n" +
            $"Host: {connection.HostHeader}\r\n" +
            "Connection: close\r\n\r\n");
        var second = await connection.ReadResponseAsync();

        Assert.Equal(200, second.StatusCode);
        Assert.Equal("Hello", second.Body);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task ChunkedRequestBodyCanBeRead()
    {
        var app = CreateBodyReadingApp();
        await using var server = await StartAsync(app);
        await using var connection = await RawHttpConnection.ConnectAsync(server.Addresses[0]);
        await connection.WriteAsync(
            "POST /read HTTP/1.1\r\n" +
            $"Host: {connection.HostHeader}\r\n" +
            "Transfer-Encoding: chunked\r\n" +
            "Connection: close\r\n\r\n" +
            "5\r\nhello\r\n" +
            "6\r\n world\r\n" +
            "0\r\n\r\n");

        var response = await connection.ReadResponseAsync();

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("11", response.Body);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task ClientDisconnectCancelsRequestAborted()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var app = new App();
        app.Get("/wait", async context =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.Aborted);
            }
            catch (OperationCanceledException) when (context.Aborted.IsCancellationRequested)
            {
                aborted.TrySetResult();
                throw;
            }
        });

        await using var server = await StartAsync(app);
        await using (var connection = await RawHttpConnection.ConnectAsync(server.Addresses[0]))
        {
            await connection.WriteAsync(
                "GET /wait HTTP/1.1\r\n" +
                $"Host: {connection.HostHeader}\r\n\r\n");
            await entered.Task.WaitAsync(OperationTimeout);
        }

        await aborted.Task.WaitAsync(OperationTimeout);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task GracefulShutdownWaitsForActiveRequest()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var app = new App();
        app.Get("/slow", async context =>
        {
            entered.TrySetResult();
            await release.Task;
            await context.Text("done");
        });

        await using var server = await StartAsync(app);
        using var client = CreateClient(server);
        var responseTask = client.GetAsync("/slow");

        try
        {
            await entered.Task.WaitAsync(OperationTimeout);
            var stopTask = server.StopAsync();
            await Task.Delay(100);
            Assert.False(stopTask.IsCompleted);

            release.TrySetResult();
            using var response = await responseTask.WaitAsync(OperationTimeout);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("done", await response.Content.ReadAsStringAsync());
            await stopTask.WaitAsync(OperationTimeout);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task PortCollisionMakesStartFail()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var app = new App();
        app.Get("/", context => context.Text("Hello"));

        await Assert.ThrowsAnyAsync<IOException>(async () =>
        {
            await app.StartAsync(new MiyaOptions { Port = port });
        });
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task PortZeroReportsActualListeningAddress()
    {
        var app = new App();
        app.Get("/", context => context.Text("Hello"));

        await using var server = await StartAsync(app);

        var address = Assert.Single(server.Addresses);
        var uri = new Uri(address);
        Assert.Equal("http", uri.Scheme);
        Assert.Equal(IPAddress.Loopback, IPAddress.Parse(uri.Host));
        Assert.InRange(uri.Port, 1, 65535);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task EnvironmentPortIsUsedWhenOptionIsOmitted()
    {
        var previousPort = Environment.GetEnvironmentVariable("PORT");
        MiyaServer? server = null;
        try
        {
            Environment.SetEnvironmentVariable("PORT", "0");
            var app = new App();
            app.Get("/", context => context.Text("Hello"));
            server = await app.StartAsync(new MiyaOptions
            {
                ShutdownTimeout = TimeSpan.FromSeconds(2),
            });

            var address = Assert.Single(server.Addresses);
            Assert.InRange(new Uri(address).Port, 1, 65535);
        }
        finally
        {
            if (server is not null)
            {
                await server.DisposeAsync();
            }

            Environment.SetEnvironmentVariable("PORT", previousPort);
        }
    }

    private static App CreateBodyReadingApp()
    {
        var app = new App();
        app.Post("/read", async context =>
        {
            var body = await context.Req.Text();
            await context.Text(body.Length.ToString(CultureInfo.InvariantCulture));
        });
        return app;
    }

    private static Task<MiyaServer> StartAsync(App app) => app.StartAsync(
        new MiyaOptions
        {
            Port = 0,
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        });

    private static HttpClient CreateClient(MiyaServer server) => new()
    {
        BaseAddress = new Uri(server.Addresses[0]),
        Timeout = OperationTimeout,
    };
}

internal sealed class RawHttpConnection : IAsyncDisposable
{
    private const int MaximumHeaderBytes = 32 * 1024;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;

    private RawHttpConnection(TcpClient client, Uri address)
    {
        _client = client;
        _stream = client.GetStream();
        HostHeader = $"{address.Host}:{address.Port}";
    }

    public string HostHeader { get; }

    public static async Task<RawHttpConnection> ConnectAsync(string address)
    {
        var uri = new Uri(address);
        var client = new TcpClient
        {
            NoDelay = true,
        };

        try
        {
            using var timeout = new CancellationTokenSource(OperationTimeout);
            await client.ConnectAsync(uri.Host, uri.Port, timeout.Token);
            return new RawHttpConnection(client, uri);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task WriteAsync(string data)
    {
        using var timeout = new CancellationTokenSource(OperationTimeout);
        await _stream.WriteAsync(Encoding.ASCII.GetBytes(data), timeout.Token);
        await _stream.FlushAsync(timeout.Token);
    }

    public async Task<RawHttpResponse> ReadResponseAsync()
    {
        using var timeout = new CancellationTokenSource(OperationTimeout);
        var headerBytes = new List<byte>();
        var oneByte = new byte[1];
        while (!EndsWithHeaderTerminator(headerBytes))
        {
            if (headerBytes.Count >= MaximumHeaderBytes)
            {
                throw new InvalidDataException("The HTTP response headers exceeded the test limit.");
            }

            var read = await _stream.ReadAsync(oneByte, timeout.Token);
            if (read == 0)
            {
                throw new EndOfStreamException("The connection closed before the HTTP response headers completed.");
            }

            headerBytes.Add(oneByte[0]);
        }

        var headerText = Encoding.ASCII.GetString([.. headerBytes]);
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        var statusParts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (statusParts.Length < 2
            || !int.TryParse(statusParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var statusCode))
        {
            throw new InvalidDataException($"Invalid HTTP status line: {lines[0]}");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length && lines[i].Length > 0; i++)
        {
            var separator = lines[i].IndexOf(':');
            if (separator <= 0)
            {
                throw new InvalidDataException($"Invalid HTTP header: {lines[i]}");
            }

            headers[lines[i][..separator]] = lines[i][(separator + 1)..].Trim();
        }

        var contentLength = headers.TryGetValue("Content-Length", out var rawLength)
            ? int.Parse(rawLength, NumberStyles.None, CultureInfo.InvariantCulture)
            : 0;
        var body = new byte[contentLength];
        var offset = 0;
        while (offset < body.Length)
        {
            var read = await _stream.ReadAsync(body.AsMemory(offset), timeout.Token);
            if (read == 0)
            {
                throw new EndOfStreamException("The connection closed before the HTTP response body completed.");
            }

            offset += read;
        }

        return new RawHttpResponse(statusCode, headers, Encoding.UTF8.GetString(body));
    }

    public ValueTask DisposeAsync()
    {
        _stream.Dispose();
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private static bool EndsWithHeaderTerminator(List<byte> bytes)
    {
        var count = bytes.Count;
        return count >= 4
            && bytes[count - 4] == '\r'
            && bytes[count - 3] == '\n'
            && bytes[count - 2] == '\r'
            && bytes[count - 1] == '\n';
    }
}

internal sealed record RawHttpResponse(
    int StatusCode,
    IReadOnlyDictionary<string, string> Headers,
    string Body);
