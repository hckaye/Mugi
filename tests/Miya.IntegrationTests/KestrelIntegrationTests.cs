using System.Globalization;
using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    public async Task ConfigureServicesUsesServiceBackedHostForCleartext()
    {
        var app = new App();
        app.Get("/", context => context.Text("Hello"));

        var configured = false;
        await using var server = await app.StartAsync(new MiyaOptions
        {
            Port = 0,
            ShutdownTimeout = TimeSpan.FromSeconds(2),
            ConfigureServices = _ => configured = true,
        });
        using var client = CreateClient(server);
        using var response = await client.GetAsync("/");

        Assert.True(configured);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Hello", await response.Content.ReadAsStringAsync());
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
    public async Task ChunkedBodyReaderStopsAtConfiguredLimit()
    {
        var app = new App();
        app.Post("/read", async context =>
        {
            var reader = context.Req.BodyReader;
            while (true)
            {
                var result = await reader.ReadAsync(context.Aborted);
                reader.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted)
                {
                    break;
                }
            }

            await context.Text("unreachable");
        });

        await using var server = await app.StartAsync(new MiyaOptions
        {
            Port = 0,
            MaxRequestBodyBytes = 4,
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        });
        await using var connection = await RawHttpConnection.ConnectAsync(server.Addresses[0]);
        await connection.WriteAsync(
            "POST /read HTTP/1.1\r\n" +
            $"Host: {connection.HostHeader}\r\n" +
            "Transfer-Encoding: chunked\r\n" +
            "Connection: close\r\n\r\n" +
            "3\r\nabc\r\n" +
            "2\r\nde\r\n" +
            "0\r\n\r\n");

        var response = await connection.ReadResponseAsync();

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, response.StatusCode);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task HeadCountsLargeBodyWithoutRetainingResponseBuffer()
    {
        const int bodyLength = 2 * 1024 * 1024;
        var body = new string('x', bodyLength);
        Context? captured = null;
        var app = new App();
        app.Get("/large", context =>
        {
            captured = context;
            return context.Text(body);
        });

        await using var server = await app.StartAsync(new MiyaOptions
        {
            Port = 0,
            MaxBufferedResponseBytes = 32,
            MaxRetainedBufferBytes = bodyLength * 2,
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        });
        using var client = CreateClient(server);
        using var request = new HttpRequestMessage(HttpMethod.Head, "/large");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((long)bodyLength, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.NotNull(captured);
        Assert.Equal(0, GetRetainedResponseBufferLength(captured));
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task ReusedContextRejectsOperationsFromPreviousRequestGeneration()
    {
        var mutate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leakedHeaderResult = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var leakedRequestResult = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Context? firstContext = null;
        var app = new App();
        app.Get("/first", context =>
        {
            firstContext = context;
            _ = Task.Run(async () =>
            {
                await mutate.Task;
                leakedHeaderResult.TrySetResult(Record.Exception(
                    () => context.Header("X-Leaked", "true")));
                leakedRequestResult.TrySetResult(Record.Exception(
                    () => _ = context.Req.Method));
            });
            return context.Text("first");
        });
        app.Get("/second", async context =>
        {
            secondEntered.TrySetResult(ReferenceEquals(firstContext, context));
            await releaseSecond.Task;
            await context.Text("second");
        });

        await using var server = await StartAsync(app);
        using var client = CreateClient(server);
        using var firstResponse = await client.GetAsync("/first");
        Assert.Equal("first", await firstResponse.Content.ReadAsStringAsync());

        var secondResponseTask = client.GetAsync("/second");
        try
        {
            Assert.True(await secondEntered.Task.WaitAsync(OperationTimeout));
            mutate.TrySetResult();
            var headerException = await leakedHeaderResult.Task.WaitAsync(OperationTimeout);
            var requestException = await leakedRequestResult.Task.WaitAsync(OperationTimeout);
            releaseSecond.TrySetResult();

            using var secondResponse = await secondResponseTask.WaitAsync(OperationTimeout);
            Assert.IsType<ObjectDisposedException>(headerException);
            Assert.IsType<ObjectDisposedException>(requestException);
            Assert.False(secondResponse.Headers.Contains("X-Leaked"));
            Assert.Equal("second", await secondResponse.Content.ReadAsStringAsync());
        }
        finally
        {
            mutate.TrySetResult();
            releaseSecond.TrySetResult();
        }
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task RouteParametersRejectInvalidUtf8WithoutDoubleDecoding()
    {
        var app = new App();
        app.Get("/users/:id", context => context.Text(context.Param("id")));
        app.Get("/query", context => context.Text(context.Query("value")!));

        await using var server = await StartAsync(app);
        await using (var connection = await RawHttpConnection.ConnectAsync(server.Addresses[0]))
        {
            await connection.WriteAsync(
                "GET /users/%FF HTTP/1.1\r\n" +
                $"Host: {connection.HostHeader}\r\n" +
                "Connection: close\r\n\r\n");
            var invalid = await connection.ReadResponseAsync();
            Assert.Equal(StatusCodes.Status400BadRequest, invalid.StatusCode);
        }

        await using (var connection = await RawHttpConnection.ConnectAsync(server.Addresses[0]))
        {
            await connection.WriteAsync(
                "GET /query?value=%FF HTTP/1.1\r\n" +
                $"Host: {connection.HostHeader}\r\n" +
                "Connection: close\r\n\r\n");
            var invalid = await connection.ReadResponseAsync();
            Assert.Equal(StatusCodes.Status400BadRequest, invalid.StatusCode);
        }

        using var client = CreateClient(server);
        using var escapedPercent = await client.GetAsync("/users/%25FF");
        using var utf8 = await client.GetAsync("/users/%E6%97%A5%E6%9C%AC");
        using var query = await client.GetAsync("/query?value=%25FF");

        Assert.Equal(HttpStatusCode.OK, escapedPercent.StatusCode);
        Assert.Equal("%FF", await escapedPercent.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, utf8.StatusCode);
        Assert.Equal("日本", await utf8.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, query.StatusCode);
        Assert.Equal("%FF", await query.Content.ReadAsStringAsync());
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task HttpMethodTokensAreCaseSensitive()
    {
        var app = new App();
        app.Get("/", context => context.Text("Hello"));

        await using var server = await StartAsync(app);
        await using (var connection = await RawHttpConnection.ConnectAsync(server.Addresses[0]))
        {
            await connection.WriteAsync(
                "get / HTTP/1.1\r\n" +
                $"Host: {connection.HostHeader}\r\n" +
                "Connection: close\r\n\r\n");
            var lowerCase = await connection.ReadResponseAsync();
            Assert.Equal(StatusCodes.Status405MethodNotAllowed, lowerCase.StatusCode);
        }

        using var client = CreateClient(server);
        using var upperCase = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, upperCase.StatusCode);
        Assert.Equal("Hello", await upperCase.Content.ReadAsStringAsync());
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task RedirectValidatesUriReferencesOnKestrel()
    {
        Exception? invalidException = null;
        var app = new App();
        app.Get("/invalid", context =>
        {
            invalidException = Record.Exception(() => context.Redirect("/bad path"));
            return context.Text("caught");
        });
        app.Get("/relative", context => context.Redirect("../next?value=1#part"));
        app.Get("/absolute", context => context.Redirect("https://example.com/users/42"));

        await using var server = await StartAsync(app);
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(server.Addresses[0]),
            Timeout = OperationTimeout,
        };
        using var invalid = await client.GetAsync("/invalid");
        using var relative = await client.GetAsync("/relative");
        using var absolute = await client.GetAsync("/absolute");

        Assert.IsType<ArgumentException>(invalidException);
        Assert.Equal("caught", await invalid.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Found, relative.StatusCode);
        Assert.Equal("../next?value=1#part", relative.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Found, absolute.StatusCode);
        Assert.Equal("https://example.com/users/42", absolute.Headers.Location?.OriginalString);
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
    public async Task CleartextHttp2SupportsPriorKnowledge()
    {
        var app = new App();
        app.Get("/", context => context.Text("Hello"));

        await using var server = await app.StartAsync(new MiyaOptions
        {
            Port = 0,
            Protocols = MiyaProtocols.Http2,
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        });
        using var client = CreateClient(server);
        using var request = CreateGetRequest(HttpVersion.Version20);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpVersion.Version20, response.Version);
        Assert.Equal("Hello", await response.Content.ReadAsStringAsync());
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task CertificateEnablesHttp1AndHttp2WithAlpn()
    {
        using var certificate = CreateCertificate();
        var app = new App();
        app.Get("/", context => context.Text("Hello"));

        await using var server = await app.StartAsync(new MiyaOptions
        {
            Port = 0,
            Certificate = certificate,
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        });
        using var client = CreateTlsClient(server);

        using var http1Request = CreateGetRequest(HttpVersion.Version11);
        using var http1Response = await client.SendAsync(http1Request);
        Assert.Equal(HttpStatusCode.OK, http1Response.StatusCode);
        Assert.Equal(HttpVersion.Version11, http1Response.Version);
        Assert.Equal("Hello", await http1Response.Content.ReadAsStringAsync());

        using var http2Request = CreateGetRequest(HttpVersion.Version20);
        using var http2Response = await client.SendAsync(http2Request);
        Assert.Equal(HttpStatusCode.OK, http2Response.StatusCode);
        Assert.Equal(HttpVersion.Version20, http2Response.Version);
        Assert.Equal("Hello", await http2Response.Content.ReadAsStringAsync());
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task TlsLogsServicesRequestedThroughKestrelApplicationServices()
    {
        using var certificate = CreateCertificate();
        using var loggerFactory = new RecordingLoggerFactory();
        var app = new App();
        app.Get("/", context => context.Text("Hello"));

        await using var server = await app.StartAsync(new MiyaOptions
        {
            Port = 0,
            Certificate = certificate,
            LoggerFactory = loggerFactory,
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        });

        Assert.Equal(
            [
                "Microsoft.AspNetCore.Server.Kestrel.Core.IHttpsConfigurationService",
                "Microsoft.Extensions.Logging.ILoggerFactory",
                "Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Infrastructure.KestrelMetrics",
            ],
            loggerFactory.ApplicationServiceRequests);
        Assert.Contains("Microsoft.AspNetCore.Server.Kestrel", loggerFactory.Categories);
    }

    [QuicFact(Timeout = TestTimeoutMilliseconds)]
    public async Task Http3UsesTlsAndAdvertisesAltSvcWhenQuicIsSupported()
    {
        using var certificate = CreateCertificate();
        var app = new App();
        app.Get("/", context => context.Text("Hello"));

        await using var server = await app.StartAsync(new MiyaOptions
        {
            Port = 0,
            Certificate = certificate,
            Protocols = MiyaProtocols.Http1AndHttp2AndHttp3,
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        });
        using var client = CreateTlsClient(server);

        using var http2Request = CreateGetRequest(HttpVersion.Version20);
        using var http2Response = await client.SendAsync(http2Request);
        Assert.Equal(HttpVersion.Version20, http2Response.Version);
        Assert.True(http2Response.Headers.TryGetValues("Alt-Svc", out var altSvcValues));
        Assert.Contains(altSvcValues, value => value.Contains("h3=", StringComparison.Ordinal));

        using var http3Request = CreateGetRequest(HttpVersion.Version30);
        using var http3Response = await client.SendAsync(http3Request);
        Assert.Equal(HttpStatusCode.OK, http3Response.StatusCode);
        Assert.Equal(HttpVersion.Version30, http3Response.Version);
        Assert.Equal("Hello", await http3Response.Content.ReadAsStringAsync());
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
    public async Task GracefulShutdownWaitsForActiveHttp2Request()
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

        await using var server = await app.StartAsync(new MiyaOptions
        {
            Port = 0,
            Protocols = MiyaProtocols.Http2,
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        });
        using var client = CreateClient(server);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/slow")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        var responseTask = client.SendAsync(request);

        try
        {
            await entered.Task.WaitAsync(OperationTimeout);
            var stopTask = server.StopAsync();
            await Task.Delay(100);
            Assert.False(stopTask.IsCompleted);

            release.TrySetResult();
            using var response = await responseTask.WaitAsync(OperationTimeout);
            Assert.Equal(HttpVersion.Version20, response.Version);
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

    private static int GetRetainedResponseBufferLength(Context context)
    {
        var writerField = typeof(Context).GetField("_buffer", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Context response buffer field was not found.");
        var writer = writerField.GetValue(context)
            ?? throw new InvalidOperationException("Context response buffer was null.");
        var bufferField = writer.GetType().GetField("_buffer", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Pooled response array field was not found.");
        return bufferField.GetValue(writer) is byte[] buffer ? buffer.Length : 0;
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

    private static HttpClient CreateTlsClient(MiyaServer server)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        return new HttpClient(handler)
        {
            BaseAddress = new Uri(server.Addresses[0]),
            Timeout = OperationTimeout,
        };
    }

    private static HttpRequestMessage CreateGetRequest(Version version) => new(HttpMethod.Get, "/")
    {
        Version = version,
        VersionPolicy = HttpVersionPolicy.RequestVersionExact,
    };

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        subjectAlternativeNames.AddDnsName("localhost");
        subjectAlternativeNames.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(subjectAlternativeNames.Build());

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), password: null);
    }
}

internal sealed class QuicFactAttribute : FactAttribute
{
    public QuicFactAttribute()
    {
        if (!QuicListener.IsSupported)
        {
            Skip = "System.Net.Quic.QuicListener.IsSupported is false on this platform.";
        }
    }
}

internal sealed class RecordingLoggerFactory : ILoggerFactory
{
    private const string CategoryName = "Miya.Kestrel.ApplicationServices";
    private const string MessagePrefix = "Kestrel ApplicationServices requested ";
    private readonly List<string> _applicationServiceRequests = [];
    private readonly List<string> _categories = [];

    public IReadOnlyList<string> ApplicationServiceRequests => _applicationServiceRequests;

    public IReadOnlyList<string> Categories => _categories;

    public void AddProvider(ILoggerProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        provider.Dispose();
    }

    public ILogger CreateLogger(string categoryName)
    {
        _categories.Add(categoryName);
        return categoryName == CategoryName ? new RecordingLogger(_applicationServiceRequests) : NullLogger.Instance;
    }

    public void Dispose()
    {
    }

    private sealed class RecordingLogger(List<string> applicationServiceRequests) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Debug;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (message.StartsWith(MessagePrefix, StringComparison.Ordinal)
                && message.EndsWith(".", StringComparison.Ordinal))
            {
                applicationServiceRequests.Add(message[MessagePrefix.Length..^1]);
            }
        }
    }
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
