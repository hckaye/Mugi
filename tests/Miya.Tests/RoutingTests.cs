using Microsoft.AspNetCore.Http;

namespace Miya.Tests;

public sealed class RoutingTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("connect-udp")]
    public async Task ConnectDoesNotFallBackToGetWithoutWebSocketExtendedConnect(string? protocol)
    {
        var handlerCalled = false;
        var app = new App();
        app.Get("/resource", context =>
        {
            handlerCalled = true;
            return context.Text("get");
        });

        await using var response = await TestApp.Send(
            app,
            method: "CONNECT",
            path: "/resource",
            extendedConnectProtocol: protocol);

        Assert.Equal(StatusCodes.Status405MethodNotAllowed, response.Response.StatusCode);
        Assert.Equal("GET, HEAD, OPTIONS", response.Response.Headers.Allow.ToString());
        Assert.False(handlerCalled);
    }

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
    public async Task AllowIncludesEveryPatternMatchingThePathInProtocolOrder()
    {
        var app = new App();
        app.Get("/users/:id", c => c.Text("get"));
        app.Post("/users/me", c => c.Text("post"));
        app.Put("/users/*rest", c => c.Text("put"));
        app.Delete("/users/:name", c => c.Text("delete"));
        app.Patch("/users/me", c => c.Text("patch"));
        app.On("CONNECT", "/users/:user", c => c.Text("connect"));

        await using var mismatch = await TestApp.Send(app, method: "TRACE", path: "/users/me");
        await using var options = await TestApp.Send(app, method: "OPTIONS", path: "/users/me");

        const string expected = "GET, HEAD, POST, PUT, DELETE, PATCH, OPTIONS, CONNECT";
        Assert.Equal(StatusCodes.Status405MethodNotAllowed, mismatch.Response.StatusCode);
        Assert.Equal(expected, mismatch.Response.Headers.Allow.ToString());
        Assert.Equal(StatusCodes.Status204NoContent, options.Response.StatusCode);
        Assert.Equal(expected, options.Response.Headers.Allow.ToString());
    }

    [Fact]
    public async Task RootWildcardPreservesExistingAllowBehavior()
    {
        var app = new App();
        app.Get("/*rest", c => c.Text(c.Param("rest")));

        await using var response = await TestApp.Send(app, path: "/");

        Assert.Equal(StatusCodes.Status405MethodNotAllowed, response.Response.StatusCode);
        Assert.Equal("GET, HEAD, OPTIONS", response.Response.Headers.Allow.ToString());
    }

    [Fact]
    public async Task TrieBacktracksAcrossMethodAndSegmentSpecificity()
    {
        var app = new App();
        app.Post("/a/b/:value", c => c.Text($"static:{c.Param("value")}"));
        app.Get("/a/:value/c", c => c.Text($"parameter:{c.Param("value")}"));
        app.Get("/tree/*rest", c => c.Text($"wildcard:{c.Param("rest")}"));
        app.Get("/tree/deep/static/end", c => c.Text("deep-static"));

        await using var get = await TestApp.Send(app, path: "/a/b/c");
        await using var post = await TestApp.Send(app, method: "POST", path: "/a/b/c");
        await using var deepStatic = await TestApp.Send(app, path: "/tree/deep/static/end");
        await using var wildcard = await TestApp.Send(app, path: "/tree/deep/other");

        Assert.Equal("parameter:b", get.BodyText);
        Assert.Equal("static:c", post.BodyText);
        Assert.Equal("deep-static", deepStatic.BodyText);
        Assert.Equal("wildcard:deep/other", wildcard.BodyText);
    }

    [Fact]
    public async Task TriePreservesAllRouteRegistrationOrderAndHeadPrecedence()
    {
        var app = new App();
        app.All("/tie/:all", c =>
        {
            c.Header("X-Route", "all");
            return c.Text(c.Param("all"));
        });
        app.Get("/tie/:get", c =>
        {
            c.Header("X-Route", "get");
            return c.Text(c.Param("get"));
        });
        app.Get("/head/specific", c =>
        {
            c.Header("X-Route", "get");
            return c.Text("get");
        });
        app.All("/head/:value", c =>
        {
            c.Header("X-Route", "all");
            return c.Text(c.Param("value"));
        });

        await using var tied = await TestApp.Send(app, path: "/tie/value");
        await using var head = await TestApp.Send(app, method: "HEAD", path: "/head/specific");

        Assert.Equal("all", tied.Response.Headers["X-Route"].ToString());
        Assert.Equal("value", tied.BodyText);
        Assert.Equal("all", head.Response.Headers["X-Route"].ToString());
        Assert.Equal("8", head.Response.Headers.ContentLength.ToString());
        Assert.Empty(head.BodyText);
    }

    [Fact]
    public async Task LargeMixedRouteTableSelectsRoutesAndMethodResponses()
    {
        var app = new App();
        for (var index = 0; index < 100; index++)
        {
            var routeIndex = index;
            app.Get(
                $"/scale/shared/v1/zone-{index}/fixed",
                c => c.Text($"static:{routeIndex}"));
            app.Get(
                $"/scale/shared/v1/zone-{index}/items/:id/detail",
                c => c.Text($"parameter:{routeIndex}:{c.Param("id")}"));
            app.Get(
                $"/scale/shared/v1/zone-{index}/assets/*rest",
                c => c.Text($"wildcard:{routeIndex}:{c.Param("rest")}"));
        }

        await using var first = await TestApp.Send(app, path: "/scale/shared/v1/zone-0/fixed");
        await using var middle = await TestApp.Send(
            app,
            path: "/scale/shared/v1/zone-49/items/item%2F49/detail");
        await using var last = await TestApp.Send(
            app,
            path: "/scale/shared/v1/zone-99/assets/css/site.css");
        await using var miss = await TestApp.Send(app, path: "/scale/shared/v1/zone-100/fixed");
        await using var mismatch = await TestApp.Send(
            app,
            method: "POST",
            path: "/scale/shared/v1/zone-73/items/value/detail");
        await using var options = await TestApp.Send(
            app,
            method: "OPTIONS",
            path: "/scale/shared/v1/zone-21/assets/scripts/app.js");
        await using var head = await TestApp.Send(
            app,
            method: "HEAD",
            path: "/scale/shared/v1/zone-88/fixed");

        Assert.Equal("static:0", first.BodyText);
        Assert.Equal("parameter:49:item/49", middle.BodyText);
        Assert.Equal("wildcard:99:css/site.css", last.BodyText);
        Assert.Equal(StatusCodes.Status404NotFound, miss.Response.StatusCode);
        Assert.Equal(StatusCodes.Status405MethodNotAllowed, mismatch.Response.StatusCode);
        Assert.Equal("GET, HEAD, OPTIONS", mismatch.Response.Headers.Allow.ToString());
        Assert.Equal(StatusCodes.Status204NoContent, options.Response.StatusCode);
        Assert.Equal("GET, HEAD, OPTIONS", options.Response.Headers.Allow.ToString());
        Assert.Equal(StatusCodes.Status200OK, head.Response.StatusCode);
        Assert.Equal("9", head.Response.Headers.ContentLength.ToString());
        Assert.Empty(head.BodyText);
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
