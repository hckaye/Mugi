using System.Net;
using Microsoft.AspNetCore.Http;
using Miya.Middleware;

namespace Miya.IntegrationTests;

public sealed class CorsIntegrationTests
{
    private const int TestTimeoutMilliseconds = 10_000;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task BrowserLikePreflightAndActualExchangeSendsCorsHeaders()
    {
        var app = new App();
        app.Use(Cors.Middleware(new CorsOptions
        {
            Origins = ["https://app.example"],
            ExposeHeaders = ["X-Count"],
            MaxAge = TimeSpan.FromSeconds(600),
        }));
        app.Post("/items", context =>
        {
            context.Header("X-Count", "1");
            return context.Text("created");
        });

        await using var server = await StartAsync(app);
        using var client = CreateClient(server);

        using var preflightRequest = new HttpRequestMessage(HttpMethod.Options, "/items");
        preflightRequest.Headers.TryAddWithoutValidation("Origin", "https://app.example");
        preflightRequest.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
        preflightRequest.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", "content-type, x-request-id");
        using var preflight = await client.SendAsync(preflightRequest);

        Assert.Equal(HttpStatusCode.NoContent, preflight.StatusCode);
        Assert.Equal("https://app.example", GetHeader(preflight, "Access-Control-Allow-Origin"));
        Assert.Equal(
            "GET, POST, PUT, DELETE, PATCH, HEAD, OPTIONS",
            GetHeader(preflight, "Access-Control-Allow-Methods"));
        Assert.Equal("content-type, x-request-id", GetHeader(preflight, "Access-Control-Allow-Headers"));
        Assert.Equal("600", GetHeader(preflight, "Access-Control-Max-Age"));
        Assert.Contains("Origin", GetHeader(preflight, "Vary"), StringComparison.Ordinal);
        Assert.Contains("Access-Control-Request-Method", GetHeader(preflight, "Vary"), StringComparison.Ordinal);
        Assert.Contains("Access-Control-Request-Headers", GetHeader(preflight, "Vary"), StringComparison.Ordinal);
        Assert.Null(GetHeader(preflight, "Allow"));
        Assert.Empty(await preflight.Content.ReadAsByteArrayAsync());

        using var actualRequest = new HttpRequestMessage(HttpMethod.Post, "/items")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        actualRequest.Headers.TryAddWithoutValidation("Origin", "https://app.example");
        using var actual = await client.SendAsync(actualRequest);

        Assert.Equal(HttpStatusCode.OK, actual.StatusCode);
        Assert.Equal("created", await actual.Content.ReadAsStringAsync());
        Assert.Equal("https://app.example", GetHeader(actual, "Access-Control-Allow-Origin"));
        Assert.Equal("X-Count", GetHeader(actual, "Access-Control-Expose-Headers"));
        Assert.Equal("Origin", GetHeader(actual, "Vary"));
        Assert.Equal("1", GetHeader(actual, "X-Count"));
        Assert.Null(GetHeader(actual, "Access-Control-Allow-Methods"));
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task PlainOptionsOnKestrelAddsAllowOriginAfterRouterAllow()
    {
        var app = new App();
        app.Use(Cors.Middleware(new CorsOptions { Origins = ["https://app.example"] }));
        app.Get("/items", context => context.Text("ok"));

        await using var server = await StartAsync(app);
        using var client = CreateClient(server);
        using var request = new HttpRequestMessage(HttpMethod.Options, "/items");
        request.Headers.TryAddWithoutValidation("Origin", "https://app.example");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("GET, HEAD, OPTIONS", response.Content.Headers.Allow.ToString());
        Assert.Equal("https://app.example", GetHeader(response, "Access-Control-Allow-Origin"));
        Assert.Equal(StatusCodes.Status204NoContent, (int)response.StatusCode);
    }

    private static string? GetHeader(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values))
        {
            return string.Join(", ", values);
        }

        if (response.Content.Headers.TryGetValues(name, out values))
        {
            return string.Join(", ", values);
        }

        return null;
    }

    private static Task<Server> StartAsync(App app) => app.StartAsync(
        new AppOptions
        {
            Port = 0,
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        });

    private static HttpClient CreateClient(Server server) => new()
    {
        BaseAddress = new Uri(server.Addresses[0]),
        Timeout = OperationTimeout,
    };
}
