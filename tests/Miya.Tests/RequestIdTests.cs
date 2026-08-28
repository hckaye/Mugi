using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Miya.Middleware;

namespace Miya.Tests;

public sealed class RequestIdTests
{
    private static readonly Regex GeneratedId = new(
        "^[0-9a-f]{32}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    [Theory]
    [InlineData("a")]
    [InlineData("ok")]
    [InlineData("AZaz09._-")]
    [InlineData("req-42")]
    [InlineData("ABC.def_123-xyz")]
    public async Task AcceptsATrustedIncomingRequestId(string incoming)
    {
        var app = new App();
        app.Use(RequestId.Middleware());
        app.Get("/", context => context.Text("ok"));

        await using var response = await TestApp.Send(
            app,
            headers: new Dictionary<string, string> { ["X-Request-Id"] = incoming });

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal(incoming, response.Response.Headers["X-Request-Id"].ToString());
    }

    [Fact]
    public async Task AcceptsARequestIdOfMaximumLength()
    {
        var incoming = new string('a', 128);
        var app = new App();
        app.Use(RequestId.Middleware());
        app.Get("/", context => context.Text("ok"));

        await using var response = await TestApp.Send(
            app,
            headers: new Dictionary<string, string> { ["X-Request-Id"] = incoming });

        Assert.Equal(incoming, response.Response.Headers["X-Request-Id"].ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("plus+sign")]
    [InlineData("slash/value")]
    [InlineData("at@sign")]
    [InlineData("comma,separated")]
    [InlineData("colon:value")]
    [InlineData("unicode-å")]
    public async Task RejectsAnUntrustedIncomingRequestId(string incoming)
    {
        var app = new App();
        app.Use(RequestId.Middleware());
        app.Get("/", context => context.Text("ok"));

        await using var response = await TestApp.Send(
            app,
            headers: new Dictionary<string, string> { ["X-Request-Id"] = incoming });

        var assigned = response.Response.Headers["X-Request-Id"].ToString();
        Assert.NotEqual(incoming, assigned);
        Assert.Matches(GeneratedId, assigned);
    }

    [Fact]
    public async Task RejectsAnIncomingRequestIdThatExceedsTheLengthLimit()
    {
        var incoming = new string('a', 129);
        var app = new App();
        app.Use(RequestId.Middleware());
        app.Get("/", context => context.Text("ok"));

        await using var response = await TestApp.Send(
            app,
            headers: new Dictionary<string, string> { ["X-Request-Id"] = incoming });

        var assigned = response.Response.Headers["X-Request-Id"].ToString();
        Assert.NotEqual(incoming, assigned);
        Assert.Matches(GeneratedId, assigned);
    }

    [Fact]
    public async Task RejectsIncomingHeaderInjectionWithCrLf()
    {
        var incoming = "ok\r\nX-Injected: yes";
        var app = new App();
        app.Use(RequestId.Middleware());
        app.Get("/", context => context.Text("ok"));

        await using var response = await TestApp.Send(
            app,
            headers: new Dictionary<string, string> { ["X-Request-Id"] = incoming });

        var assigned = response.Response.Headers["X-Request-Id"].ToString();
        Assert.NotEqual(incoming, assigned);
        Assert.DoesNotContain('\r', assigned);
        Assert.DoesNotContain('\n', assigned);
        Assert.False(response.Response.Headers.ContainsKey("X-Injected"));
        Assert.Matches(GeneratedId, assigned);
    }

    [Fact]
    public async Task RejectsIncomingHeaderInjectionWithLfOnly()
    {
        var incoming = "ok\nX-Injected: yes";
        var app = new App();
        app.Use(RequestId.Middleware());
        app.Get("/", context => context.Text("ok"));

        await using var response = await TestApp.Send(
            app,
            headers: new Dictionary<string, string> { ["X-Request-Id"] = incoming });

        Assert.Matches(GeneratedId, response.Response.Headers["X-Request-Id"].ToString());
        Assert.False(response.Response.Headers.ContainsKey("X-Injected"));
    }

    [Fact]
    public async Task GeneratesAnIdWhenTheIncomingHeaderIsMissing()
    {
        var app = new App();
        app.Use(RequestId.Middleware());
        app.Get("/", context => context.Text("ok"));

        await using var response = await TestApp.Send(app);

        Assert.Matches(GeneratedId, response.Response.Headers["X-Request-Id"].ToString());
    }

    [Fact]
    public async Task GeneratedIdsAreUnique()
    {
        var app = new App();
        app.Use(RequestId.Middleware());
        app.Get("/", context => context.Text("ok"));

        await using var first = await TestApp.Send(app);
        await using var second = await TestApp.Send(app);

        var left = first.Response.Headers["X-Request-Id"].ToString();
        var right = second.Response.Headers["X-Request-Id"].ToString();
        Assert.Matches(GeneratedId, left);
        Assert.Matches(GeneratedId, right);
        Assert.NotEqual(left, right);
    }

    [Fact]
    public async Task TrustIncomingFalseIgnoresAValidIncomingValue()
    {
        var app = new App();
        app.Use(RequestId.Middleware(new RequestIdOptions { TrustIncoming = false }));
        app.Get("/", context => context.Text("ok"));

        await using var response = await TestApp.Send(
            app,
            headers: new Dictionary<string, string> { ["X-Request-Id"] = "client-supplied" });

        var assigned = response.Response.Headers["X-Request-Id"].ToString();
        Assert.NotEqual("client-supplied", assigned);
        Assert.Matches(GeneratedId, assigned);
    }

    [Fact]
    public async Task CustomHeaderNameIsReadAndWritten()
    {
        var app = new App();
        app.Use(RequestId.Middleware(new RequestIdOptions { HeaderName = "X-Correlation-Id" }));
        app.Get("/", context => context.Text("ok"));

        await using var response = await TestApp.Send(
            app,
            headers: new Dictionary<string, string> { ["X-Correlation-Id"] = "corr-1" });

        Assert.Equal("corr-1", response.Response.Headers["X-Correlation-Id"].ToString());
        Assert.False(response.Response.Headers.ContainsKey("X-Request-Id"));
    }

    [Fact]
    public async Task IncomingHeaderLookupIsCaseInsensitive()
    {
        var app = new App();
        app.Use(RequestId.Middleware());
        app.Get("/", context => context.Text("ok"));

        await using var response = await TestApp.Send(
            app,
            headers: new Dictionary<string, string> { ["x-request-id"] = "abc-123" });

        Assert.Equal("abc-123", response.Response.Headers["X-Request-Id"].ToString());
    }

    [Fact]
    public async Task TypedContextStoresTheAssignedIdBeforeTheHandlerRuns()
    {
        string? seenByHandler = null;
        var app = new App<RequestIdTestContext>();
        app.Use(RequestId.Middleware<RequestIdTestContext>());
        app.Get("/", context =>
        {
            seenByHandler = context.RequestId;
            return context.Text(context.RequestId!);
        });

        await using var response = await TestApp.Send(
            app,
            headers: new Dictionary<string, string> { ["X-Request-Id"] = "typed-1" });

        Assert.Equal("typed-1", seenByHandler);
        Assert.Equal("typed-1", response.BodyText);
        Assert.Equal("typed-1", response.Response.Headers["X-Request-Id"].ToString());
    }

    [Fact]
    public async Task TypedContextStoresAGeneratedId()
    {
        string? seenByHandler = null;
        var app = new App<RequestIdTestContext>();
        app.Use(RequestId.Middleware<RequestIdTestContext>(new RequestIdOptions { TrustIncoming = false }));
        app.Get("/", context =>
        {
            seenByHandler = context.RequestId;
            return context.Text("ok");
        });

        await using var response = await TestApp.Send(
            app,
            headers: new Dictionary<string, string> { ["X-Request-Id"] = "ignored" });

        Assert.NotNull(seenByHandler);
        Assert.Matches(GeneratedId, seenByHandler);
        Assert.Equal(seenByHandler, response.Response.Headers["X-Request-Id"].ToString());
        Assert.NotEqual("ignored", seenByHandler);
    }

    [Fact]
    public async Task SetsTheResponseHeaderBeforeNextSoLaterMiddlewareCanSeeIt()
    {
        var seenByInner = false;
        var app = new App();
        app.Use(RequestId.Middleware());
        app.Use(async (context, next) =>
        {
            seenByInner = context.ContainsResponseHeader("X-Request-Id");
            await next(context);
        });
        app.Get("/", context => context.Text("ok"));

        await using var response = await TestApp.Send(
            app,
            headers: new Dictionary<string, string> { ["X-Request-Id"] = "visible" });

        Assert.True(seenByInner);
        Assert.Equal("visible", response.Response.Headers["X-Request-Id"].ToString());
    }

    [Theory]
    [InlineData("Bad Name")]
    [InlineData("X:Request")]
    [InlineData("X Request")]
    public void FactoryRejectsAnInvalidHeaderName(string headerName)
    {
        Assert.Throws<ArgumentException>(() =>
            RequestId.Middleware(new RequestIdOptions { HeaderName = headerName }));
    }

    [Fact]
    public void FactoryRejectsAnEmptyHeaderName()
    {
        Assert.Throws<ArgumentException>(() =>
            RequestId.Middleware(new RequestIdOptions { HeaderName = "" }));
    }

    [Fact]
    public void FactoryRejectsANullHeaderName()
    {
        Assert.Throws<ArgumentNullException>(() =>
            RequestId.Middleware(new RequestIdOptions { HeaderName = null! }));
    }

    [Fact]
    public void FactoryRejectsAFrameworkManagedHeaderName()
    {
        Assert.Throws<InvalidOperationException>(() =>
            RequestId.Middleware(new RequestIdOptions { HeaderName = "Content-Length" }));
    }

    public sealed class RequestIdTestContext : Context, IRequestIdContext
    {
        public string? RequestId { get; set; }
    }
}
