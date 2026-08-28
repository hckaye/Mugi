using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using CompressionFeature = Miya.Middleware.Compression;
using ETagFeature = Miya.Middleware.ETag;
using TimeoutFeature = Miya.Middleware.RequestTimeout;

namespace Miya.IntegrationTests;

public sealed class ResponseMiddlewareIntegrationTests
{
    private const int TestTimeoutMilliseconds = 10_000;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);
    private static readonly string CompressibleText = new('z', 8192);

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [InlineData(DecompressionMethods.GZip)]
    [InlineData(DecompressionMethods.Brotli)]
    public async Task HttpClientAutomaticallyDecompressesNegotiatedResponses(DecompressionMethods method)
    {
        var app = CreateCompressionApp();
        await using var server = await StartAsync(app);
        using var handler = new HttpClientHandler { AutomaticDecompression = method };
        using var client = CreateClient(server, handler);

        using var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(CompressibleText, await response.Content.ReadAsStringAsync());
        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Contains("Accept-Encoding", response.Headers.Vary);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task CompressedRepresentationRoundTripsThroughNotModified()
    {
        var app = new App();
        app.Use(ETagFeature.Middleware());
        app.Use(CompressionFeature.Middleware(new() { MinBytes = 0 }));
        app.Get("/", context =>
        {
            context.Header("Cache-Control", "public, max-age=60");
            return context.Text(CompressibleText);
        });

        await using var server = await StartAsync(app);
        using var client = CreateClient(server);
        using var firstRequest = CreateCompressedRequest(HttpMethod.Get);
        using var first = await client.SendAsync(firstRequest);
        var entityTag = first.Headers.ETag;
        Assert.NotNull(entityTag);
        Assert.Equal("gzip", first.Content.Headers.ContentEncoding.Single());

        using var secondRequest = CreateCompressedRequest(HttpMethod.Get);
        secondRequest.Headers.IfNoneMatch.Add(entityTag);
        using var second = await client.SendAsync(secondRequest);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Equal(entityTag, second.Headers.ETag);
        Assert.Equal("public, max-age=60", second.Headers.CacheControl?.ToString());
        Assert.Contains("Accept-Encoding", second.Headers.Vary);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync());
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task TimeoutIsSentWhileTheHandlerContinuesRunning()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateWrite = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var app = new App();
        app.Use(TimeoutFeature.Middleware(TimeSpan.FromMilliseconds(30)));
        app.Get("/", async context =>
        {
            await release.Task;
            lateWrite.TrySetResult(await Record.ExceptionAsync(async () => await context.Text("late")));
        });

        await using var server = await StartAsync(app);
        using var client = CreateClient(server);
        using var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal("Gateway Timeout", await response.Content.ReadAsStringAsync());
        release.TrySetResult();
        Assert.IsType<InvalidOperationException>(
            await lateWrite.Task.WaitAsync(OperationTimeout));
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task HeadAfterCompressionUsesTheGetRepresentationLength()
    {
        var app = CreateCompressionApp();
        await using var server = await StartAsync(app);
        using var client = CreateClient(server);

        using var getRequest = CreateCompressedRequest(HttpMethod.Get);
        using var get = await client.SendAsync(getRequest);
        using var headRequest = CreateCompressedRequest(HttpMethod.Head);
        using var head = await client.SendAsync(headRequest);

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal("gzip", get.Content.Headers.ContentEncoding.Single());
        Assert.Equal("gzip", head.Content.Headers.ContentEncoding.Single());
        Assert.Equal(get.Content.Headers.ContentLength, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    private static App CreateCompressionApp()
    {
        var app = new App();
        app.Use(CompressionFeature.Middleware(new() { MinBytes = 0 }));
        app.Get("/", context => context.Text(CompressibleText));
        return app;
    }

    private static HttpRequestMessage CreateCompressedRequest(HttpMethod method)
    {
        var request = new HttpRequestMessage(method, "/");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        return request;
    }

    private static Task<Server> StartAsync(App app) => app.StartAsync(
        new AppOptions
        {
            Port = 0,
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        });

    private static HttpClient CreateClient(Server server, HttpMessageHandler? handler = null)
    {
        var client = handler is null ? new HttpClient() : new HttpClient(handler);
        client.BaseAddress = new Uri(server.Addresses[0]);
        client.Timeout = OperationTimeout;
        return client;
    }
}
