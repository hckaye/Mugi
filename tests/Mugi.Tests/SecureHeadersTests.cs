using System.Buffers;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Http;
using Mugi.Middleware;

namespace Mugi.Tests;

public sealed class SecureHeadersTests
{
    [Fact]
    public async Task SetsDefaultHeadersAndOmitsContentSecurityPolicy()
    {
        var app = new App();
        app.Use(SecureHeaders.Middleware());
        app.Get("/", context => context.Text("ok"));

        await using var response = await TestApp.Send(app);

        Assert.Equal("ok", response.BodyText);
        AssertDefaultHeaders(response, includeCsp: false);
    }

    [Fact]
    public async Task PerHeaderNullDisablesThatHeader()
    {
        var app = new App();
        app.Use(SecureHeaders.Middleware(new SecureHeadersOptions
        {
            XFrameOptions = null,
            StrictTransportSecurity = null,
        }));
        app.Get("/", context => context.Text("ok"));

        await using var response = await TestApp.Send(app);

        Assert.False(response.Response.Headers.ContainsKey("X-Frame-Options"));
        Assert.False(response.Response.Headers.ContainsKey("Strict-Transport-Security"));
        Assert.Equal("nosniff", response.Response.Headers["X-Content-Type-Options"].ToString());
        Assert.Equal("no-referrer", response.Response.Headers["Referrer-Policy"].ToString());
        Assert.False(response.Response.Headers.ContainsKey("Content-Security-Policy"));
    }

    [Fact]
    public async Task DisablingEveryHeaderSendsNoneOfThem()
    {
        var app = new App();
        app.Use(SecureHeaders.Middleware(new SecureHeadersOptions
        {
            XContentTypeOptions = null,
            XFrameOptions = null,
            ReferrerPolicy = null,
            StrictTransportSecurity = null,
            XXSSProtection = null,
            CrossOriginOpenerPolicy = null,
            CrossOriginResourcePolicy = null,
            XPermittedCrossDomainPolicies = null,
            XDownloadOptions = null,
            ContentSecurityPolicy = null,
        }));
        app.Get("/", context => context.Text("ok"));

        await using var response = await TestApp.Send(app);

        Assert.False(response.Response.Headers.ContainsKey("X-Content-Type-Options"));
        Assert.False(response.Response.Headers.ContainsKey("X-Frame-Options"));
        Assert.False(response.Response.Headers.ContainsKey("Referrer-Policy"));
        Assert.False(response.Response.Headers.ContainsKey("Strict-Transport-Security"));
        Assert.False(response.Response.Headers.ContainsKey("X-XSS-Protection"));
        Assert.False(response.Response.Headers.ContainsKey("Cross-Origin-Opener-Policy"));
        Assert.False(response.Response.Headers.ContainsKey("Cross-Origin-Resource-Policy"));
        Assert.False(response.Response.Headers.ContainsKey("X-Permitted-Cross-Domain-Policies"));
        Assert.False(response.Response.Headers.ContainsKey("X-Download-Options"));
        Assert.False(response.Response.Headers.ContainsKey("Content-Security-Policy"));
    }

    [Fact]
    public async Task HandlerSetValuesWin()
    {
        var app = new App();
        app.Use(SecureHeaders.Middleware());
        app.Get("/", context =>
        {
            context.Header("X-Frame-Options", "DENY");
            context.Header("Referrer-Policy", "same-origin");
            return context.Text("ok");
        });

        await using var response = await TestApp.Send(app);

        Assert.Equal("DENY", response.Response.Headers["X-Frame-Options"].ToString());
        Assert.Equal("same-origin", response.Response.Headers["Referrer-Policy"].ToString());
        Assert.Equal("nosniff", response.Response.Headers["X-Content-Type-Options"].ToString());
    }

    [Fact]
    public async Task HandlerSetValuesWinWhenTheHeaderNameCasingDiffers()
    {
        var app = new App();
        app.Use(SecureHeaders.Middleware());
        app.Get("/", context =>
        {
            context.Header("x-frame-options", "DENY");
            return context.Text("ok");
        });

        await using var response = await TestApp.Send(app);

        Assert.Equal("DENY", response.Response.Headers["X-Frame-Options"].ToString());
        Assert.Equal("DENY", response.Response.Headers["x-frame-options"].ToString());
    }

    [Fact]
    public async Task ContentSecurityPolicyIsPassedThroughWhenConfigured()
    {
        const string policy = "default-src 'self'; img-src https:";
        var app = new App();
        app.Use(SecureHeaders.Middleware(new SecureHeadersOptions
        {
            ContentSecurityPolicy = policy,
        }));
        app.Get("/", context => context.Text("ok"));

        await using var response = await TestApp.Send(app);

        Assert.Equal(policy, response.Response.Headers["Content-Security-Policy"].ToString());
        Assert.Equal("nosniff", response.Response.Headers["X-Content-Type-Options"].ToString());
    }

    [Fact]
    public async Task CustomHeaderValuesReplaceDefaults()
    {
        var app = new App();
        app.Use(SecureHeaders.Middleware(new SecureHeadersOptions
        {
            XFrameOptions = "DENY",
            StrictTransportSecurity = "max-age=31536000",
            XXSSProtection = "1; mode=block",
        }));
        app.Get("/", context => context.Text("ok"));

        await using var response = await TestApp.Send(app);

        Assert.Equal("DENY", response.Response.Headers["X-Frame-Options"].ToString());
        Assert.Equal("max-age=31536000", response.Response.Headers["Strict-Transport-Security"].ToString());
        Assert.Equal("1; mode=block", response.Response.Headers["X-XSS-Protection"].ToString());
    }

    [Fact]
    public async Task AppliesToNotFoundResponses()
    {
        var app = new App();
        app.Use(SecureHeaders.Middleware());
        app.Get("/", context => context.Text("ok"));

        await using var response = await TestApp.Send(app, path: "/missing");

        Assert.Equal(StatusCodes.Status404NotFound, response.Response.StatusCode);
        Assert.Equal("nosniff", response.Response.Headers["X-Content-Type-Options"].ToString());
        Assert.Equal("SAMEORIGIN", response.Response.Headers["X-Frame-Options"].ToString());
    }

    [Fact]
    public async Task DoesNotThrowAfterAStreamingResponseHasStarted()
    {
        var app = new App();
        app.Use(SecureHeaders.Middleware());
        app.Get("/", context => context.Stream("text/plain", static (writer, _) =>
        {
            writer.Write("streamed"u8);
            return ValueTask.CompletedTask;
        }));

        await using var response = await TestApp.Send(app);

        Assert.Equal("streamed", response.BodyText);
        Assert.True(response.ResponseBody.Started);
    }

    [Fact]
    public async Task HandlerContentTypeIsLeftUnchanged()
    {
        var app = new App();
        app.Use(SecureHeaders.Middleware());
        app.Get("/", context => context.Html("<p>ok</p>"));

        await using var response = await TestApp.Send(app);

        Assert.Equal("text/html; charset=utf-8", response.Response.Headers.ContentType.ToString());
        Assert.Equal("nosniff", response.Response.Headers["X-Content-Type-Options"].ToString());
    }

    [Fact]
    public void FactoryRejectsAHeaderValueWithCrLf()
    {
        Assert.Throws<ArgumentException>(() =>
            SecureHeaders.Middleware(new SecureHeadersOptions
            {
                ContentSecurityPolicy = "default-src 'self'\r\nX-Injected: yes",
            }));
    }

    [Fact]
    public void FactoryRejectsAHeaderValueWithANulCharacter()
    {
        Assert.Throws<ArgumentException>(() =>
            SecureHeaders.Middleware(new SecureHeadersOptions
            {
                XFrameOptions = "DENY\0",
            }));
    }

    [Fact]
    public async Task RunsOnACustomContextThroughTheAdapter()
    {
        var app = new App<CustomContext>();
        app.Use(SecureHeaders.Middleware());
        app.Get("/", context =>
        {
            context.Header("X-Frame-Options", "DENY");
            return context.Text("ok");
        });

        await using var response = await TestApp.Send(app);

        Assert.Equal("DENY", response.Response.Headers["X-Frame-Options"].ToString());
        Assert.Equal("nosniff", response.Response.Headers["X-Content-Type-Options"].ToString());
    }

    private static void AssertDefaultHeaders(TestExchange response, bool includeCsp)
    {
        Assert.Equal("nosniff", response.Response.Headers["X-Content-Type-Options"].ToString());
        Assert.Equal("SAMEORIGIN", response.Response.Headers["X-Frame-Options"].ToString());
        Assert.Equal("no-referrer", response.Response.Headers["Referrer-Policy"].ToString());
        Assert.Equal(
            "max-age=15552000; includeSubDomains",
            response.Response.Headers["Strict-Transport-Security"].ToString());
        Assert.Equal("0", response.Response.Headers["X-XSS-Protection"].ToString());
        Assert.Equal("same-origin", response.Response.Headers["Cross-Origin-Opener-Policy"].ToString());
        Assert.Equal("same-origin", response.Response.Headers["Cross-Origin-Resource-Policy"].ToString());
        Assert.Equal("none", response.Response.Headers["X-Permitted-Cross-Domain-Policies"].ToString());
        Assert.Equal("noopen", response.Response.Headers["X-Download-Options"].ToString());
        Assert.Equal(includeCsp, response.Response.Headers.ContainsKey("Content-Security-Policy"));
    }

    public sealed class CustomContext : Context;
}
