using System.Reflection;
using Microsoft.AspNetCore.Http;

namespace Mugi.Tests;

public sealed class StaticEmbeddedTests
{
    [Fact]
    public async Task MapsLogicalNamesAfterTheResourcePrefix()
    {
        var app = new App();
        app.Static("/assets", new StaticOptions
        {
            Source = StaticSource.Embedded(typeof(StaticEmbeddedTests).Assembly, "StaticFixture"),
        });

        await using var index = await TestApp.Send(app, path: "/assets/");
        await using var nested = await TestApp.Send(app, path: "/assets/nested.txt");

        Assert.Equal(StatusCodes.Status200OK, index.Response.StatusCode);
        Assert.Contains("logical index", index.BodyText);
        Assert.Equal("logical nested resource\n", nested.BodyText);
        Assert.Equal("text/html; charset=utf-8", index.Response.Headers.ContentType.ToString());
        Assert.False(index.Response.Headers.ContainsKey("Accept-Ranges"));
        Assert.False(index.Response.Headers.ContainsKey("Last-Modified"));
    }

    [Fact]
    public async Task MapsDefaultDottedNamesToDirectoriesAndFiles()
    {
        var app = new App();
        app.Static("/assets", new StaticOptions
        {
            Source = StaticSource.Embedded(
                typeof(StaticEmbeddedTests).Assembly,
                "Mugi.Tests.StaticAssets.Dotted"),
        });

        await using var index = await TestApp.Send(app, path: "/assets");
        await using var css = await TestApp.Send(app, path: "/assets/css/site.css");

        Assert.Equal(StatusCodes.Status200OK, index.Response.StatusCode);
        Assert.Contains("dotted index", index.BodyText);
        Assert.Equal(StatusCodes.Status200OK, css.Response.StatusCode);
        Assert.Equal("body { color: black; }\n", css.BodyText);
        Assert.Equal("text/css; charset=utf-8", css.Response.Headers.ContentType.ToString());
    }

    [Fact]
    public async Task EmbeddedEtagsAreStableAndSupportConditionalRequests()
    {
        var app = new App();
        app.Static("/assets", new StaticOptions
        {
            Source = StaticSource.Embedded(typeof(StaticEmbeddedTests).Assembly, "StaticFixture"),
            CacheControl = "public, max-age=60",
        });

        await using var first = await TestApp.Send(app, path: "/assets/index.html");
        var etag = first.Response.Headers.ETag.ToString();
        await first.DisposeAsync();
        await using var second = await TestApp.Send(app, path: "/assets/index.html");
        await using var conditional = await TestApp.Send(
            app,
            path: "/assets/index.html",
            headers: new Dictionary<string, string> { ["If-None-Match"] = etag });

        Assert.Matches("^\"[0-9a-f]{32}-[0-9a-f]{8}\"$", etag);
        Assert.Equal(etag, second.Response.Headers.ETag.ToString());
        Assert.Equal(StatusCodes.Status304NotModified, conditional.Response.StatusCode);
        Assert.Empty(conditional.BodyText);
        Assert.Equal(etag, conditional.Response.Headers.ETag.ToString());
        Assert.Equal("public, max-age=60", conditional.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task EmbeddedRangeHeadersAreIgnoredAndMissesReturn404()
    {
        var app = new App();
        app.Static("/assets", new StaticOptions
        {
            Source = StaticSource.Embedded(typeof(StaticEmbeddedTests).Assembly, "StaticFixture"),
        });

        await using var range = await TestApp.Send(
            app,
            path: "/assets/index.html",
            headers: new Dictionary<string, string> { ["Range"] = "bytes=0-1" });
        await using var miss = await TestApp.Send(app, path: "/assets/nope.txt");
        await using var traversal = await TestApp.Send(app, path: "/assets/%2e%2e/index.html");

        Assert.Equal(StatusCodes.Status200OK, range.Response.StatusCode);
        Assert.Contains("logical index", range.BodyText);
        Assert.False(range.Response.Headers.ContainsKey("Content-Range"));
        Assert.Equal(StatusCodes.Status404NotFound, miss.Response.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, traversal.Response.StatusCode);
    }

    [Fact]
    public async Task EmbeddedIndexCanBeDisabled()
    {
        var app = new App();
        app.Static("/assets", new StaticOptions
        {
            Source = StaticSource.Embedded(typeof(StaticEmbeddedTests).Assembly, "StaticFixture"),
            Index = "",
        });

        await using var response = await TestApp.Send(app, path: "/assets/");

        Assert.Equal(StatusCodes.Status404NotFound, response.Response.StatusCode);
    }

    [Fact]
    public void EmbeddedSourceCanBeCreatedFromAnAssembly()
    {
        var source = StaticSource.Embedded(Assembly.GetExecutingAssembly(), "StaticFixture");
        Assert.NotNull(source);
    }
}
