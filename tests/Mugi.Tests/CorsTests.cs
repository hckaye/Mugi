using System.Buffers;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Http;
using Mugi.Middleware;

namespace Mugi.Tests;

public sealed class CorsTests
{
    [Fact]
    public async Task RequestWithoutOriginPassesThroughUntouched()
    {
        var app = CreateApp(new CorsOptions { Origins = ["https://app.example"] });

        await using var response = await TestApp.Send(app);

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal("ok", response.BodyText);
        Assert.False(response.Response.Headers.ContainsKey("Access-Control-Allow-Origin"));
        Assert.False(response.Response.Headers.ContainsKey("Vary"));
    }

    [Fact]
    public async Task ExactOriginMatchAddsAllowOriginAndVaryAfterNext()
    {
        var app = CreateApp(new CorsOptions
        {
            Origins = ["https://app.example", "https://admin.example"],
            ExposeHeaders = ["X-Count", "X-Request-Id"],
        });

        await using var response = await TestApp.Send(
            app,
            headers: new Dictionary<string, string>
            {
                ["origin"] = "https://app.example",
            });

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal("ok", response.BodyText);
        Assert.Equal("yes", response.Response.Headers["X-Handler"].ToString());
        Assert.Equal("https://app.example", response.Response.Headers["Access-Control-Allow-Origin"].ToString());
        Assert.Equal("X-Count, X-Request-Id", response.Response.Headers["Access-Control-Expose-Headers"].ToString());
        Assert.Equal("Origin", response.Response.Headers["Vary"].ToString());
        Assert.False(response.Response.Headers.ContainsKey("Access-Control-Allow-Credentials"));
        Assert.False(response.Response.Headers.ContainsKey("Access-Control-Allow-Methods"));
    }

    [Fact]
    public async Task AllowedOriginCompletesAStreamingResponseWithCorsHeaders()
    {
        var app = new App();
        app.Use(Cors.Middleware(new CorsOptions
        {
            Origins = ["https://app.example"],
            Credentials = true,
            ExposeHeaders = ["X-Count"],
        }));
        app.Get("/", context => context.Stream(
            "text/plain",
            static (writer, _) =>
            {
                writer.Write("streamed"u8);
                return ValueTask.CompletedTask;
            }));

        await using var response = await TestApp.Send(
            app,
            headers: new Dictionary<string, string> { ["Origin"] = "https://app.example" });

        Assert.Equal("streamed", response.BodyText);
        Assert.True(response.ResponseBody.Completed);
        Assert.False(response.Lifetime.WasAborted);
        Assert.Equal("https://app.example", response.Response.Headers["Access-Control-Allow-Origin"].ToString());
        Assert.Equal("true", response.Response.Headers["Access-Control-Allow-Credentials"].ToString());
        Assert.Equal("X-Count", response.Response.Headers["Access-Control-Expose-Headers"].ToString());
        Assert.Equal("Origin", response.Response.Headers.Vary.ToString());
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("https://APP.example")]
    [InlineData("https://app.example/")]
    [InlineData("http://app.example")]
    public async Task OriginMismatchPassesThroughWithoutCorsHeaders(string origin)
    {
        var called = false;
        var app = new App();
        app.Use(Cors.Middleware(new CorsOptions { Origins = ["https://app.example"] }));
        app.Get("/", context =>
        {
            called = true;
            return context.Text("ok");
        });

        await using var response = await TestApp.Send(
            app,
            headers: new Dictionary<string, string> { ["Origin"] = origin });

        Assert.True(called);
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal("ok", response.BodyText);
        Assert.False(response.Response.Headers.ContainsKey("Access-Control-Allow-Origin"));
        Assert.False(response.Response.Headers.ContainsKey("Vary"));
    }

    [Fact]
    public async Task WildcardAllowsAnyOriginWithoutVary()
    {
        var app = CreateApp(new CorsOptions { Origins = ["*"] });

        await using var response = await TestApp.Send(
            app,
            headers: new Dictionary<string, string> { ["ORIGIN"] = "https://any.example" });

        Assert.Equal("*", response.Response.Headers["Access-Control-Allow-Origin"].ToString());
        Assert.False(response.Response.Headers.ContainsKey("Vary"));
        Assert.Equal("ok", response.BodyText);
    }

    [Fact]
    public void WildcardWithCredentialsThrowsAtFactory()
    {
        var exception = Assert.Throws<ArgumentException>(() => Cors.Middleware(new CorsOptions
        {
            Origins = ["*"],
            Credentials = true,
        }));
        Assert.Contains("credentials", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WildcardAmongOtherOriginsWithCredentialsThrowsAtFactory()
    {
        Assert.Throws<ArgumentException>(() => Cors.Middleware(new CorsOptions
        {
            Origins = ["https://app.example", "*"],
            Credentials = true,
        }));
    }

    [Fact]
    public void NegativeMaxAgeThrowsAtFactory()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Cors.Middleware(new CorsOptions
        {
            Origins = ["https://app.example"],
            MaxAge = TimeSpan.FromSeconds(-1),
        }));
    }

    [Fact]
    public void NullOptionsThrowsAtFactory()
    {
        Assert.Throws<ArgumentNullException>(() => Cors.Middleware(null!));
    }

    [Fact]
    public async Task PreflightReturnsFullHeaderSetAndDoesNotCallNext()
    {
        var nextCalled = false;
        var app = new App();
        app.Use(Cors.Middleware(new CorsOptions
        {
            Origins = ["https://app.example"],
            Methods = ["GET", "POST"],
            MaxAge = TimeSpan.FromSeconds(600),
            Credentials = true,
        }));
        app.Use(async (context, next) =>
        {
            nextCalled = true;
            await next(context);
        });
        app.Post("/items", context => context.Text("created"));

        await using var response = await TestApp.Send(
            app,
            method: "OPTIONS",
            path: "/items",
            headers: new Dictionary<string, string>
            {
                ["Origin"] = "https://app.example",
                ["Access-Control-Request-Method"] = "POST",
                ["Access-Control-Request-Headers"] = "content-type, x-request-id",
            });

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status204NoContent, response.Response.StatusCode);
        Assert.Equal(string.Empty, response.BodyText);
        Assert.Equal("https://app.example", response.Response.Headers["Access-Control-Allow-Origin"].ToString());
        Assert.Equal("GET, POST", response.Response.Headers["Access-Control-Allow-Methods"].ToString());
        Assert.Equal("content-type, x-request-id", response.Response.Headers["Access-Control-Allow-Headers"].ToString());
        Assert.Equal("600", response.Response.Headers["Access-Control-Max-Age"].ToString());
        Assert.Equal("true", response.Response.Headers["Access-Control-Allow-Credentials"].ToString());
        Assert.Equal(
            "Origin, Access-Control-Request-Method, Access-Control-Request-Headers",
            response.Response.Headers["Vary"].ToString());
        Assert.False(response.Response.Headers.ContainsKey("Allow"));
        Assert.False(response.Response.Headers.ContainsKey("Access-Control-Expose-Headers"));
    }

    [Fact]
    public async Task PreflightEchoesRequestedHeadersWhenAllowHeadersIsEmpty()
    {
        var app = new App();
        app.Use(Cors.Middleware(new CorsOptions { Origins = ["https://app.example"] }));
        app.Get("/", context => context.Text("ok"));

        await using var response = await TestApp.Send(
            app,
            method: "OPTIONS",
            headers: new Dictionary<string, string>
            {
                ["Origin"] = "https://app.example",
                ["access-control-request-method"] = "GET",
                ["ACCESS-CONTROL-REQUEST-HEADERS"] = "X-Custom, Content-Type",
            });

        Assert.Equal(StatusCodes.Status204NoContent, response.Response.StatusCode);
        Assert.Equal("X-Custom, Content-Type", response.Response.Headers["Access-Control-Allow-Headers"].ToString());
        Assert.Equal(
            "GET, POST, PUT, DELETE, PATCH, HEAD, OPTIONS",
            response.Response.Headers["Access-Control-Allow-Methods"].ToString());
    }

    [Fact]
    public async Task PreflightUsesConfiguredAllowHeadersInsteadOfEcho()
    {
        var app = new App();
        app.Use(Cors.Middleware(new CorsOptions
        {
            Origins = ["https://app.example"],
            Headers = ["X-Token", "Content-Type"],
        }));
        app.Get("/", context => context.Text("ok"));

        await using var response = await TestApp.Send(
            app,
            method: "OPTIONS",
            headers: new Dictionary<string, string>
            {
                ["Origin"] = "https://app.example",
                ["Access-Control-Request-Method"] = "GET",
                ["Access-Control-Request-Headers"] = "X-Ignored",
            });

        Assert.Equal("X-Token, Content-Type", response.Response.Headers["Access-Control-Allow-Headers"].ToString());
    }

    [Fact]
    public async Task WildcardPreflightDoesNotSetVary()
    {
        var app = new App();
        app.Use(Cors.Middleware(new CorsOptions { Origins = ["*"] }));
        app.Get("/", context => context.Text("ok"));

        await using var response = await TestApp.Send(
            app,
            method: "OPTIONS",
            headers: new Dictionary<string, string>
            {
                ["Origin"] = "https://any.example",
                ["Access-Control-Request-Method"] = "GET",
            });

        Assert.Equal(StatusCodes.Status204NoContent, response.Response.StatusCode);
        Assert.Equal("*", response.Response.Headers["Access-Control-Allow-Origin"].ToString());
        Assert.False(response.Response.Headers.ContainsKey("Vary"));
    }

    [Fact]
    public async Task PlainOptionsFallsThroughToRouterAllowWithAllowOrigin()
    {
        var nextCalled = false;
        var app = new App();
        app.Use(Cors.Middleware(new CorsOptions { Origins = ["https://app.example"] }));
        app.Use(async (context, next) =>
        {
            nextCalled = true;
            await next(context);
        });
        app.Get("/items", context => context.Text("ok"));

        await using var response = await TestApp.Send(
            app,
            method: "OPTIONS",
            path: "/items",
            headers: new Dictionary<string, string>
            {
                ["Origin"] = "https://app.example",
            });

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status204NoContent, response.Response.StatusCode);
        Assert.Equal("GET, HEAD, OPTIONS", response.Response.Headers["Allow"].ToString());
        Assert.Equal("https://app.example", response.Response.Headers["Access-Control-Allow-Origin"].ToString());
        Assert.Equal("Origin", response.Response.Headers["Vary"].ToString());
        Assert.False(response.Response.Headers.ContainsKey("Access-Control-Allow-Methods"));
    }

    [Fact]
    public async Task CredentialsAddsAllowCredentialsOnActualRequest()
    {
        var app = CreateApp(new CorsOptions
        {
            Origins = ["https://app.example"],
            Credentials = true,
        });

        await using var response = await TestApp.Send(
            app,
            headers: new Dictionary<string, string> { ["Origin"] = "https://app.example" });

        Assert.Equal("https://app.example", response.Response.Headers["Access-Control-Allow-Origin"].ToString());
        Assert.Equal("true", response.Response.Headers["Access-Control-Allow-Credentials"].ToString());
        Assert.Equal("Origin", response.Response.Headers["Vary"].ToString());
    }

    [Fact]
    public async Task DisallowedPreflightFallsThroughWithoutCorsHeaders()
    {
        var nextCalled = false;
        var app = new App();
        app.Use(Cors.Middleware(new CorsOptions { Origins = ["https://app.example"] }));
        app.Use(async (context, next) =>
        {
            nextCalled = true;
            await next(context);
        });
        app.Get("/", context => context.Text("ok"));

        await using var response = await TestApp.Send(
            app,
            method: "OPTIONS",
            headers: new Dictionary<string, string>
            {
                ["Origin"] = "https://evil.example",
                ["Access-Control-Request-Method"] = "GET",
            });

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status204NoContent, response.Response.StatusCode);
        Assert.False(response.Response.Headers.ContainsKey("Access-Control-Allow-Origin"));
        Assert.Equal("GET, HEAD, OPTIONS", response.Response.Headers["Allow"].ToString());
    }

    [Fact]
    public async Task PreflightDoesNotEchoControlCharactersInRequestedHeaders()
    {
        var app = new App();
        app.Use(Cors.Middleware(new CorsOptions { Origins = ["https://app.example"] }));
        app.Get("/", context => context.Text("ok"));

        await using var response = await TestApp.Send(
            app,
            method: "OPTIONS",
            headers: new Dictionary<string, string>
            {
                ["Origin"] = "https://app.example",
                ["Access-Control-Request-Method"] = "GET",
                ["Access-Control-Request-Headers"] = "content-type\r\nX-Injected: yes",
            });

        Assert.Equal(StatusCodes.Status204NoContent, response.Response.StatusCode);
        Assert.Equal("https://app.example", response.Response.Headers["Access-Control-Allow-Origin"].ToString());
        Assert.False(response.Response.Headers.ContainsKey("Access-Control-Allow-Headers"));
    }

    [Fact]
    public async Task EmptyOriginsAllowListNeverEmitsCorsHeaders()
    {
        var app = CreateApp(new CorsOptions());

        await using var response = await TestApp.Send(
            app,
            headers: new Dictionary<string, string> { ["Origin"] = "https://app.example" });

        Assert.Equal("ok", response.BodyText);
        Assert.False(response.Response.Headers.ContainsKey("Access-Control-Allow-Origin"));
    }

    [Theory]
    [InlineData("https://app.example\r")]
    [InlineData("https://app.example\n")]
    public void OriginsWithControlCharactersThrowAtFactory(string origin)
    {
        Assert.Throws<ArgumentException>(() => Cors.Middleware(new CorsOptions
        {
            Origins = [origin],
        }));
    }

    [Fact]
    public void NullOriginEntryThrowsAtFactory()
    {
        Assert.Throws<ArgumentException>(() => Cors.Middleware(new CorsOptions
        {
            Origins = [null!],
        }));
    }

    private static App CreateApp(CorsOptions options)
    {
        var app = new App();
        app.Use(Cors.Middleware(options));
        app.Get("/", context =>
        {
            context.Header("X-Handler", "yes");
            return context.Text("ok");
        });
        return app;
    }
}
