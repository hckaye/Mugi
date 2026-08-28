using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using ETagFeature = Mugi.Middleware.ETag;

namespace Mugi.Tests;

public sealed class ETagTests
{
    [Fact]
    public async Task GeneratedTagUsesTheStableTruncatedSha256Value()
    {
        var app = CreateApp();

        await using var first = await TestApp.Send(app);
        await using var second = await TestApp.Send(app);

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData("response"u8, hash);
        var encoded = Convert.ToBase64String(hash[..20])
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        var expected = $"\"{encoded}\"";
        Assert.Equal(expected, first.Response.Headers.ETag.ToString());
        Assert.Equal(expected, second.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task WeakOptionAddsTheWeakPrefix()
    {
        var app = new App();
        app.Use(ETagFeature.Middleware(new() { Weak = true }));
        app.Get("/", context => context.Text("response"));

        await using var response = await TestApp.Send(app);

        Assert.StartsWith("W/\"", response.Response.Headers.ETag.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"other\", W/\"fixed\"")]
    [InlineData("W/\"other\", \"fixed\"")]
    [InlineData("*")]
    public async Task IfNoneMatchUsesWeakComparisonAndLists(string ifNoneMatch)
    {
        var app = CreateAppWithExistingTag();

        await using var response = await TestApp.Send(
            app,
            headers: new Dictionary<string, string> { ["If-None-Match"] = ifNoneMatch });

        Assert.Equal(StatusCodes.Status304NotModified, response.Response.StatusCode);
        Assert.Equal("\"fixed\"", response.Response.Headers.ETag.ToString());
        Assert.Empty(response.ResponseBody.Body.ToArray());
    }

    [Theory]
    [InlineData("\"unterminated")]
    [InlineData("weak/\"fixed\"")]
    [InlineData("\"other\" garbage")]
    [InlineData("\"fixed\" garbage")]
    [InlineData("\"fixed\",")]
    [InlineData("\"other\", *")]
    public async Task MalformedIfNoneMatchDoesNotProduceNotModified(string ifNoneMatch)
    {
        var app = CreateAppWithExistingTag();

        await using var response = await TestApp.Send(
            app,
            headers: new Dictionary<string, string> { ["If-None-Match"] = ifNoneMatch });

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal("response", response.BodyText);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    public async Task NonGetMethodsAreSkipped(string method)
    {
        var app = new App();
        app.Use(ETagFeature.Middleware());
        app.On(method, "/", context => context.Text("response"));

        await using var response = await TestApp.Send(app, method: method);

        Assert.False(response.Response.Headers.ContainsKey("ETag"));
    }

    [Fact]
    public async Task ExistingTagIsPreservedAndUsedForRevalidation()
    {
        var app = CreateAppWithExistingTag();

        await using var response = await TestApp.Send(
            app,
            headers: new Dictionary<string, string> { ["If-None-Match"] = "W/\"fixed\"" });

        Assert.Equal(StatusCodes.Status304NotModified, response.Response.StatusCode);
        Assert.Equal("\"fixed\"", response.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task NotModifiedRetainsCacheAndVaryHeaders()
    {
        var app = new App();
        app.Use(ETagFeature.Middleware());
        app.Get("/", context =>
        {
            context.Header("Cache-Control", "public, max-age=60");
            context.Header("Vary", "Accept-Encoding");
            return context.Text("response");
        });

        await using var first = await TestApp.Send(app);
        await using var second = await TestApp.Send(
            app,
            headers: new Dictionary<string, string>
            {
                ["If-None-Match"] = first.Response.Headers.ETag.ToString(),
            });

        Assert.Equal(StatusCodes.Status304NotModified, second.Response.StatusCode);
        Assert.Equal("public, max-age=60", second.Response.Headers.CacheControl.ToString());
        Assert.Equal("Accept-Encoding", second.Response.Headers.Vary.ToString());
        Assert.Equal(first.Response.Headers.ETag.ToString(), second.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task HeadReceivesTheSameTagAsGet()
    {
        var app = CreateApp();

        await using var get = await TestApp.Send(app);
        await using var head = await TestApp.Send(app, method: "HEAD");

        Assert.Equal(get.Response.Headers.ETag.ToString(), head.Response.Headers.ETag.ToString());
        Assert.Empty(head.ResponseBody.Body.ToArray());
    }

    private static App CreateApp()
    {
        var app = new App();
        app.Use(ETagFeature.Middleware());
        app.Get("/", context => context.Text("response"));
        return app;
    }

    private static App CreateAppWithExistingTag()
    {
        var app = new App();
        app.Use(ETagFeature.Middleware());
        app.Get("/", context =>
        {
            context.Header("ETag", "\"fixed\"");
            return context.Text("response");
        });
        return app;
    }
}
