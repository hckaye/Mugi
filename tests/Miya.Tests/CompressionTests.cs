using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Http;
using CompressionFeature = Miya.Middleware.Compression;
using ETagFeature = Miya.Middleware.ETag;

namespace Miya.Tests;

public sealed class CompressionTests
{
    private static readonly string CompressibleText = new('a', 4096);

    [Theory]
    [InlineData("br", "br")]
    [InlineData("gzip", "gzip")]
    [InlineData("gzip;q=0.5, br;q=0.5", "br")]
    [InlineData("br;q=0.2, gzip;q=0.8", "gzip")]
    [InlineData("*;q=0.4", "br")]
    [InlineData("BR;Q=1", "br")]
    public async Task NegotiatesSupportedEncodings(string acceptEncoding, string expectedEncoding)
    {
        var app = CreateCompressionApp();

        await using var response = await TestApp.Send(
            app,
            headers: new Dictionary<string, string> { ["Accept-Encoding"] = acceptEncoding });

        Assert.Equal(expectedEncoding, response.Response.Headers.ContentEncoding.ToString());
        Assert.Equal(CompressibleText, Decompress(response.ResponseBody.Body.ToArray(), expectedEncoding));
    }

    [Theory]
    [InlineData("br;q=2")]
    [InlineData("gzip;q=0.1234")]
    [InlineData("br;level=1")]
    [InlineData(",gzip")]
    [InlineData("gzip,")]
    public async Task UnsupportedOrMalformedNegotiationLeavesTheResponseUnchanged(string acceptEncoding)
    {
        var app = CreateCompressionApp();

        await using var response = await TestApp.Send(
            app,
            headers: new Dictionary<string, string> { ["Accept-Encoding"] = acceptEncoding });

        Assert.False(response.Response.Headers.ContainsKey("Content-Encoding"));
        Assert.Equal(CompressibleText, response.BodyText);
    }

    [Fact]
    public async Task IdentityForbiddenForcesAllowedCompressionEvenWhenItIsLarger()
    {
        var app = new App();
        app.Use(CompressionFeature.Middleware());
        app.Get("/", context => context.Text("x"));

        await using var response = await TestApp.Send(
            app,
            headers: new Dictionary<string, string>
            {
                ["Accept-Encoding"] = "identity;q=0, gzip",
            });

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal("gzip", response.Response.Headers.ContentEncoding.ToString());
        Assert.True(response.ResponseBody.Body.Length > 1);
        Assert.Equal("x", Decompress(response.ResponseBody.Body.ToArray(), "gzip"));
    }

    [Theory]
    [InlineData("identity;q=0")]
    [InlineData("br;q=0, gzip;q=0, identity;q=0")]
    [InlineData("*;q=0")]
    public async Task NoAcceptableContentCodingReturnsNotAcceptable(string acceptEncoding)
    {
        var app = CreateCompressionApp();

        await using var response = await TestApp.Send(
            app,
            headers: new Dictionary<string, string> { ["Accept-Encoding"] = acceptEncoding });

        Assert.Equal(StatusCodes.Status406NotAcceptable, response.Response.StatusCode);
        Assert.Empty(response.ResponseBody.Body.ToArray());
        Assert.False(response.Response.Headers.ContainsKey("Content-Encoding"));
        Assert.Contains("Accept-Encoding", response.Response.Headers.Vary.ToArray());
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("text/css; charset=utf-8")]
    [InlineData("application/json")]
    [InlineData("application/javascript")]
    [InlineData("image/svg+xml")]
    [InlineData("application/xml")]
    [InlineData("application/wasm")]
    public async Task CompressesAllowedContentTypes(string contentType)
    {
        var app = new App();
        app.Use(CompressionFeature.Middleware(new() { MinBytes = 0 }));
        app.Get("/", context => context.Bytes(Encoding.UTF8.GetBytes(CompressibleText), contentType));

        await using var response = await TestApp.Send(
            app,
            headers: new Dictionary<string, string> { ["Accept-Encoding"] = "gzip" });

        Assert.Equal("gzip", response.Response.Headers.ContentEncoding.ToString());
    }

    [Theory]
    [InlineData("application/octet-stream")]
    [InlineData("image/png")]
    [InlineData("")]
    public async Task SkipsContentTypesOutsideTheAllowlist(string contentType)
    {
        var app = new App();
        app.Use(CompressionFeature.Middleware(new() { MinBytes = 0 }));
        app.Get("/", context => contentType.Length == 0
            ? context.Bytes(Encoding.UTF8.GetBytes(CompressibleText), "application/octet-stream")
            : context.Bytes(Encoding.UTF8.GetBytes(CompressibleText), contentType));

        await using var response = await TestApp.Send(
            app,
            headers: new Dictionary<string, string> { ["Accept-Encoding"] = "gzip" });

        Assert.False(response.Response.Headers.ContainsKey("Content-Encoding"));
    }

    [Fact]
    public async Task MinimumSizeAndLargerCompressedOutputKeepOriginalBytes()
    {
        var belowMinimum = new App();
        belowMinimum.Use(CompressionFeature.Middleware(new() { MinBytes = 10 }));
        belowMinimum.Get("/", context => context.Text("short"));

        var largerWhenCompressed = new App();
        largerWhenCompressed.Use(CompressionFeature.Middleware(new() { MinBytes = 0 }));
        largerWhenCompressed.Get("/", context => context.Text("x"));

        var headers = new Dictionary<string, string> { ["Accept-Encoding"] = "gzip" };
        await using var below = await TestApp.Send(belowMinimum, headers: headers);
        await using var larger = await TestApp.Send(largerWhenCompressed, headers: headers);

        Assert.Equal("short", below.BodyText);
        Assert.Equal("x", larger.BodyText);
        Assert.False(below.Response.Headers.ContainsKey("Content-Encoding"));
        Assert.False(larger.Response.Headers.ContainsKey("Content-Encoding"));
    }

    [Fact]
    public async Task ExistingRepresentationHeadersSkipCompression()
    {
        foreach (var headerName in new[] { "Content-Encoding", "Content-Range", "ETag" })
        {
            var app = new App();
            app.Use(CompressionFeature.Middleware(new() { MinBytes = 0 }));
            app.Get("/", context =>
            {
                context.Header(headerName, headerName == "Content-Encoding" ? "custom" : "value");
                return context.Text(CompressibleText);
            });

            await using var response = await TestApp.Send(
                app,
                headers: new Dictionary<string, string> { ["Accept-Encoding"] = "gzip" });

            Assert.Equal(CompressibleText, response.BodyText);
            if (headerName != "Content-Encoding")
            {
                Assert.False(response.Response.Headers.ContainsKey("Content-Encoding"));
            }
        }
    }

    [Fact]
    public async Task VaryIsAppendedWithoutClobberingExistingValues()
    {
        var app = new App();
        app.Use(CompressionFeature.Middleware(new() { MinBytes = 0 }));
        app.Get("/", context =>
        {
            context.Header("Vary", "Accept-Language");
            return context.Text(CompressibleText);
        });

        await using var response = await TestApp.Send(
            app,
            headers: new Dictionary<string, string> { ["Accept-Encoding"] = "gzip" });

        Assert.Equal(2, response.Response.Headers.Vary.Count);
        Assert.Equal("Accept-Language", response.Response.Headers.Vary[0]);
        Assert.Equal("Accept-Encoding", response.Response.Headers.Vary[1]);
    }

    [Fact]
    public async Task WildcardVaryIsNotExpanded()
    {
        var app = new App();
        app.Use(CompressionFeature.Middleware(new() { MinBytes = 0 }));
        app.Get("/", context =>
        {
            context.Header("Vary", "*");
            return context.Text(CompressibleText);
        });

        await using var response = await TestApp.Send(
            app,
            headers: new Dictionary<string, string> { ["Accept-Encoding"] = "gzip" });

        Assert.Equal("*", response.Response.Headers.Vary.ToString());
    }

    [Theory]
    [InlineData(StatusCodes.Status101SwitchingProtocols)]
    [InlineData(StatusCodes.Status204NoContent)]
    [InlineData(StatusCodes.Status304NotModified)]
    public async Task BodyForbiddenStatusesAreNotCompressed(int status)
    {
        var app = new App();
        app.Use(CompressionFeature.Middleware(new() { MinBytes = 0 }));
        app.Get("/", context =>
        {
            context.Status(status);
            return context.Text(CompressibleText);
        });

        await using var response = await TestApp.Send(
            app,
            headers: new Dictionary<string, string> { ["Accept-Encoding"] = "gzip" });

        Assert.False(response.Response.Headers.ContainsKey("Content-Encoding"));
        Assert.Empty(response.ResponseBody.Body.ToArray());
    }

    [Fact]
    public async Task HeadUsesTheSameCompressedLengthAsGet()
    {
        var app = CreateCompressionApp();
        var headers = new Dictionary<string, string> { ["Accept-Encoding"] = "gzip" };

        await using var get = await TestApp.Send(app, headers: headers);
        await using var head = await TestApp.Send(app, method: "HEAD", headers: headers);

        Assert.Equal("gzip", head.Response.Headers.ContentEncoding.ToString());
        Assert.Equal(get.Response.Headers.ContentLength, head.Response.Headers.ContentLength);
        Assert.Empty(head.ResponseBody.Body.ToArray());
    }

    [Fact]
    public async Task ETagRevalidatesTheCompressedRepresentation()
    {
        var app = new App();
        app.Use(ETagFeature.Middleware());
        app.Use(CompressionFeature.Middleware(new() { MinBytes = 0 }));
        app.Get("/", context => context.Text(CompressibleText));
        var requestHeaders = new Dictionary<string, string> { ["Accept-Encoding"] = "gzip" };

        await using var first = await TestApp.Send(app, headers: requestHeaders);
        var entityTag = first.Response.Headers.ETag.ToString();
        requestHeaders["If-None-Match"] = entityTag;
        await using var second = await TestApp.Send(app, headers: requestHeaders);

        Assert.Equal(StatusCodes.Status304NotModified, second.Response.StatusCode);
        Assert.Equal(entityTag, second.Response.Headers.ETag.ToString());
        Assert.Contains("Accept-Encoding", second.Response.Headers.Vary.ToArray());
        Assert.Empty(second.ResponseBody.Body.ToArray());
    }

    [Fact]
    public void OptionsAreValidatedWhenMiddlewareIsCreated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CompressionFeature.Middleware(new() { MinBytes = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CompressionFeature.Middleware(new() { Level = (CompressionLevel)99 }));
    }

    private static App CreateCompressionApp()
    {
        var app = new App();
        app.Use(CompressionFeature.Middleware(new() { MinBytes = 0 }));
        app.Get("/", context => context.Text(CompressibleText));
        return app;
    }

    private static string Decompress(byte[] body, string encoding)
    {
        using var input = new MemoryStream(body);
        using Stream decompressor = encoding == "br"
            ? new BrotliStream(input, CompressionMode.Decompress)
            : new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        decompressor.CopyTo(output);
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
