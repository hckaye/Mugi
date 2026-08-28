using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using Mugi.Middleware;

namespace Mugi.IntegrationTests;

public sealed class MiddlewareIntegrationTests
{
    private const int TestTimeoutMilliseconds = 10_000;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);
    private static readonly Regex AccessLine = new(
        @"^GET /users/42 200 \d+\.\dms",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task RequestLoggerRequestIdAndSecureHeadersRunTogetherOnKestrel()
    {
        var log = new StringWriter { NewLine = "\n" };
        var app = new App();
        app.Use(RequestLogger.Middleware(new RequestLoggerOptions { Writer = log }));
        app.Use(RequestId.Middleware());
        app.Use(SecureHeaders.Middleware(new SecureHeadersOptions
        {
            ContentSecurityPolicy = "default-src 'self'",
        }));
        app.Get("/users/:id", context => context.Text(context.Param("id")));

        await using var server = await StartAsync(app);
        using var client = CreateClient(server);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/users/42");
        request.Headers.TryAddWithoutValidation("X-Request-Id", "kestrel-42");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("42", await response.Content.ReadAsStringAsync());
        Assert.Equal("kestrel-42", GetHeader(response, "X-Request-Id"));
        Assert.Equal("nosniff", GetHeader(response, "X-Content-Type-Options"));
        Assert.Equal("SAMEORIGIN", GetHeader(response, "X-Frame-Options"));
        Assert.Equal("no-referrer", GetHeader(response, "Referrer-Policy"));
        Assert.Equal("max-age=15552000; includeSubDomains", GetHeader(response, "Strict-Transport-Security"));
        Assert.Equal("0", GetHeader(response, "X-XSS-Protection"));
        Assert.Equal("same-origin", GetHeader(response, "Cross-Origin-Opener-Policy"));
        Assert.Equal("same-origin", GetHeader(response, "Cross-Origin-Resource-Policy"));
        Assert.Equal("none", GetHeader(response, "X-Permitted-Cross-Domain-Policies"));
        Assert.Equal("noopen", GetHeader(response, "X-Download-Options"));
        Assert.Equal("default-src 'self'", GetHeader(response, "Content-Security-Policy"));

        var line = log.ToString().TrimEnd('\n');
        Assert.True(AccessLine.IsMatch(line), line);
    }

    private static string? GetHeader(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values))
        {
            return string.Join(',', values);
        }

        if (response.Content.Headers.TryGetValues(name, out values))
        {
            return string.Join(',', values);
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
