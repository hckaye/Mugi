using Microsoft.AspNetCore.Http;

namespace Miya.Tests;

public sealed class RoutingTests
{
    [Theory]
    [InlineData("/users/me", "static")]
    [InlineData("/users/42", "param:42")]
    [InlineData("/users/42/photos", "wildcard:42/photos")]
    public async Task RoutesUseSegmentPriority(string path, string expected)
    {
        var app = new App();
        app.Get("/users/*rest", c => c.Text($"wildcard:{c.Param("rest")}"));
        app.Get("/users/:id", c => c.Text($"param:{c.Param("id")}"));
        app.Get("/users/me", c => c.Text("static"));

        await using var response = await TestApp.Send(app, path: path);

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal(expected, response.BodyText);
    }

    [Fact]
    public async Task RoutesOfEqualPriorityUseRegistrationOrder()
    {
        var app = new App();
        app.Get("/:first", c => c.Text($"first:{c.Param("first")}"));
        app.Get("/:second", c => c.Text($"second:{c.Param("second")}"));

        await using var response = await TestApp.Send(app, path: "/value");

        Assert.Equal("first:value", response.BodyText);
    }

    [Fact]
    public async Task ParametersDecodeEncodedSlashesLazily()
    {
        var app = new App();
        app.Get("/items/:id", c => c.Text(c.Param("id")));

        await using var response = await TestApp.Send(app, path: "/items/a%2Fb");

        Assert.Equal("a/b", response.BodyText);
    }

    [Fact(Timeout = 10_000)]
    public async Task ParametersDecodeTheRawTargetExactlyOnce()
    {
        var app = new App();
        app.Get("/users/:id", context => context.Text(context.Param("id")));

        await using var response = await TestApp.Send(
            app,
            path: "/users/%FF",
            rawTarget: "/users/%25FF");

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal("%FF", response.BodyText);
    }

    [Fact(Timeout = 10_000)]
    public async Task InvalidUtf8RouteParameterReturnsBadRequest()
    {
        var app = new App();
        app.Get("/users/:id", context => context.Text(context.Param("id")));

        await using var response = await TestApp.Send(app, path: "/users/%FF");

        Assert.Equal(StatusCodes.Status400BadRequest, response.Response.StatusCode);
        Assert.Equal("Bad Request", response.BodyText);
    }

    [Fact(Timeout = 10_000)]
    public async Task Utf8RouteParameterIsDecoded()
    {
        var app = new App();
        app.Get("/users/:id", context => context.Text(context.Param("id")));

        await using var response = await TestApp.Send(
            app,
            path: "/users/日本",
            rawTarget: "/users/%E6%97%A5%E6%9C%AC");

        Assert.Equal("日本", response.BodyText);
    }

    [Fact]
    public async Task InvalidPathEscapeReturnsBadRequest()
    {
        var app = new App();
        app.Get("/items/:id", c => c.Text(c.Param("id")));

        await using var response = await TestApp.Send(app, path: "/items/%ZZ");

        Assert.Equal(StatusCodes.Status400BadRequest, response.Response.StatusCode);
        Assert.Equal("Bad Request", response.BodyText);
    }

    [Fact]
    public async Task TrailingSlashIsASeparateRoute()
    {
        var app = new App();
        app.Get("/users", c => c.Text("without"));

        await using var withoutSlash = await TestApp.Send(app, path: "/users");
        await using var withSlash = await TestApp.Send(app, path: "/users/");

        Assert.Equal("without", withoutSlash.BodyText);
        Assert.Equal(StatusCodes.Status404NotFound, withSlash.Response.StatusCode);
    }

    [Fact]
    public async Task MethodMismatchReturnsAllowHeader()
    {
        var app = new App();
        app.Get("/resource", c => c.Text("get"));
        app.Post("/resource", c => c.Text("post"));

        await using var response = await TestApp.Send(app, method: "DELETE", path: "/resource");

        Assert.Equal(StatusCodes.Status405MethodNotAllowed, response.Response.StatusCode);
        Assert.Equal("GET, HEAD, POST, OPTIONS", response.Response.Headers.Allow.ToString());
        Assert.Empty(response.BodyText);
    }

    [Fact(Timeout = 10_000)]
    public async Task MethodTokensAreCaseSensitive()
    {
        var app = new App();
        app.Get("/resource", context => context.Text("get"));

        await using var lowerCase = await TestApp.Send(app, method: "get", path: "/resource");
        await using var upperCase = await TestApp.Send(app, method: "GET", path: "/resource");

        Assert.Equal(StatusCodes.Status405MethodNotAllowed, lowerCase.Response.StatusCode);
        Assert.Equal("get", upperCase.BodyText);
    }

    [Fact]
    public async Task HeadUsesGetRouteAndSuppressesBody()
    {
        var app = new App();
        app.Get("/resource", c => c.Text("hello"));

        await using var response = await TestApp.Send(app, method: "HEAD", path: "/resource");

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal("5", response.Response.Headers.ContentLength.ToString());
        Assert.Empty(response.BodyText);
    }

    [Fact]
    public async Task ExplicitHeadRouteOverridesImplicitHead()
    {
        var app = new App();
        app.Get("/resource", c => c.Text("get"));
        app.Head("/resource", c =>
        {
            c.Header("X-Route", "head");
            return c.Text("explicit");
        });

        await using var response = await TestApp.Send(app, method: "HEAD", path: "/resource");

        Assert.Equal("head", response.Response.Headers["X-Route"].ToString());
        Assert.Equal("8", response.Response.Headers.ContentLength.ToString());
        Assert.Empty(response.BodyText);
    }

    [Fact]
    public async Task OptionsIsGeneratedWhenNoExplicitRouteExists()
    {
        var app = new App();
        app.Get("/resource", c => c.Text("get"));
        app.Post("/resource", c => c.Text("post"));

        await using var response = await TestApp.Send(app, method: "OPTIONS", path: "/resource");

        Assert.Equal(StatusCodes.Status204NoContent, response.Response.StatusCode);
        Assert.Equal("GET, HEAD, POST, OPTIONS", response.Response.Headers.Allow.ToString());
        Assert.False(response.Response.Headers.ContainsKey("Content-Length"));
        Assert.Empty(response.BodyText);
    }

    [Fact]
    public async Task RouteMountNormalizesSlashes()
    {
        var sub = new App();
        sub.Get("/", c => c.Text("root"));
        sub.Get("/child", c => c.Text("child"));
        var app = new App();
        app.Route("/api/", sub);

        await using var root = await TestApp.Send(app, path: "/api/");
        await using var child = await TestApp.Send(app, path: "/api/child");

        Assert.Equal("root", root.BodyText);
        Assert.Equal("child", child.BodyText);
    }
}
