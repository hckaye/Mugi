using System.Net;

namespace Mugi.IntegrationTests;

public sealed class HtmlInterpolationIntegrationTests
{
    private const int TestTimeoutMilliseconds = 10_000;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task InterpolatedHtmlIsEscapedOnTheWire()
    {
        var name = "<script>alert(1)</script>";
        var app = new App();
        app.Get("/", context => context.Html($"<h1>{name}</h1>"));

        await using var server = await StartAsync(app);
        using var client = CreateClient(server);
        using var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal("<h1>&lt;script&gt;alert(1)&lt;/script&gt;</h1>", await response.Content.ReadAsStringAsync());
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task StringHtmlStaysRawOnTheWire()
    {
        var markup = "<b>ok</b>";
        var app = new App();
        app.Get("/raw", context => context.Html(markup));
        app.Get("/escaped", context => context.Html($"{markup}"));

        await using var server = await StartAsync(app);
        using var client = CreateClient(server);
        using var raw = await client.GetAsync("/raw");
        using var escaped = await client.GetAsync("/escaped");

        Assert.Equal("<b>ok</b>", await raw.Content.ReadAsStringAsync());
        Assert.Equal("&lt;b&gt;ok&lt;/b&gt;", await escaped.Content.ReadAsStringAsync());
        Assert.Equal(raw.Content.Headers.ContentType?.ToString(), escaped.Content.Headers.ContentType?.ToString());
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
