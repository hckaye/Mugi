using Microsoft.AspNetCore.Http;
using Miya.Middleware;

namespace Miya.Tests;

public sealed class MiddlewareAdapterTests
{
    [Fact]
    public async Task AllFourCallShapesCompileAndRun()
    {
        Middleware<Context> shared = async (context, next) =>
        {
            context.Header("X-Shared", "1");
            await next(context);
        };

        var app = new App();
        app.Use(async (context, next) =>
        {
            context.Header("X-Lambda-App", "1");
            await next(context);
        });
        app.Use(shared);
        app.Get("/", context => context.Text("app"));

        var custom = new App<CustomContext>();
        custom.Use(async (CustomContext context, Handler<CustomContext> next) =>
        {
            context.Header("X-Lambda-Custom", "1");
            await next(context);
        });
        custom.Use(shared);
        custom.Get("/", context => context.Text("custom"));

        await using var appResponse = await TestApp.Send(app);
        await using var customResponse = await TestApp.Send(custom);

        Assert.Equal("app", appResponse.BodyText);
        Assert.Equal("1", appResponse.Response.Headers["X-Lambda-App"].ToString());
        Assert.Equal("1", appResponse.Response.Headers["X-Shared"].ToString());
        Assert.Equal("custom", customResponse.BodyText);
        Assert.Equal("1", customResponse.Response.Headers["X-Lambda-Custom"].ToString());
        Assert.Equal("1", customResponse.Response.Headers["X-Shared"].ToString());
    }

    [Fact]
    public async Task AdapterRejectsASubstitutedContext()
    {
        Exception? observed = null;
        var app = new App<CustomContext>();
        app.Use(async (Context context, Handler<Context> next) =>
        {
            await next(new CustomContext());
        });
        app.Get("/", context => context.Text("ok"));
        app.OnError((context, exception) =>
        {
            observed = exception;
            context.Status(598);
            return context.Text("caught");
        });

        await using var response = await TestApp.Send(app);

        var invalid = Assert.IsType<InvalidOperationException>(observed);
        Assert.Contains("same context instance", invalid.Message, StringComparison.Ordinal);
        Assert.Equal(598, response.Response.StatusCode);
        Assert.Equal("caught", response.BodyText);
    }

    [Fact]
    public async Task AdapterRejectsABaseContextSubstitute()
    {
        Exception? observed = null;
        var app = new App<CustomContext>();
        app.Use(async (Context context, Handler<Context> next) =>
        {
            await next(new Context());
        });
        app.Get("/", context => context.Text("ok"));
        app.OnError((context, exception) =>
        {
            observed = exception;
            context.Status(598);
            return context.Text("caught");
        });

        await using var response = await TestApp.Send(app);

        Assert.IsType<InvalidOperationException>(observed);
        Assert.Equal(598, response.Response.StatusCode);
    }

    [Fact]
    public async Task AdapterRejectsANullContext()
    {
        Exception? observed = null;
        var app = new App<CustomContext>();
        app.Use(async (Context context, Handler<Context> next) =>
        {
            await next(null!);
        });
        app.Get("/", context => context.Text("ok"));
        app.OnError((context, exception) =>
        {
            observed = exception;
            context.Status(598);
            return context.Text("caught");
        });

        await using var response = await TestApp.Send(app);

        Assert.IsType<InvalidOperationException>(observed);
        Assert.Equal(598, response.Response.StatusCode);
    }

    [Fact]
    public async Task AdapterAcceptsTheOriginalContext()
    {
        var sawHandler = false;
        var app = new App<CustomContext>();
        app.Use(async (Context context, Handler<Context> next) =>
        {
            context.Header("X-Before", "1");
            await next(context);
            context.Header("X-After", "1");
        });
        app.Get("/", context =>
        {
            sawHandler = true;
            return context.Text("ok");
        });

        await using var response = await TestApp.Send(app);

        Assert.True(sawHandler);
        Assert.Equal("ok", response.BodyText);
        Assert.Equal("1", response.Response.Headers["X-Before"].ToString());
        Assert.Equal("1", response.Response.Headers["X-After"].ToString());
    }

    [Fact]
    public async Task PathScopedAdapterOnlyRunsForMatchingPattern()
    {
        var calls = 0;
        Middleware<Context> middleware = async (context, next) =>
        {
            calls++;
            context.Header("X-Matched", "1");
            await next(context);
        };

        var app = new App<CustomContext>();
        app.Use("/matched/:id", middleware);
        app.Get("/matched/:id", context => context.Text(context.Param("id")));
        app.Get("/other", context => context.Text("other"));

        await using var matched = await TestApp.Send(app, path: "/matched/1");
        await using var other = await TestApp.Send(app, path: "/other");

        Assert.Equal(1, calls);
        Assert.Equal("1", matched.BodyText);
        Assert.Equal("1", matched.Response.Headers["X-Matched"].ToString());
        Assert.Equal("other", other.BodyText);
        Assert.False(other.Response.Headers.ContainsKey("X-Matched"));
    }

    [Fact]
    public async Task PathScopedAdapterDoesNotMatchAPrefixAlone()
    {
        var calls = 0;
        var app = new App<CustomContext>();
        app.Use("/admin", async (Context context, Handler<Context> next) =>
        {
            calls++;
            await next(context);
        });
        app.Get("/admin", context => context.Text("exact"));
        app.Get("/admin/users", context => context.Text("nested"));

        await using var exact = await TestApp.Send(app, path: "/admin");
        await using var nested = await TestApp.Send(app, path: "/admin/users");

        Assert.Equal(1, calls);
        Assert.Equal("exact", exact.BodyText);
        Assert.Equal("nested", nested.BodyText);
    }

    [Fact]
    public async Task BuiltInContextMiddlewareRunsOnACustomAppThroughTheAdapter()
    {
        var log = new StringWriter();
        var app = new App<CustomContext>();
        app.Use(RequestLogger.Middleware(new RequestLoggerOptions { Writer = log }));
        app.Get("/users/:id", context => context.Text(context.Param("id")));

        await using var response = await TestApp.Send(app, path: "/users/42");

        Assert.Equal("42", response.BodyText);
        Assert.StartsWith("GET /users/42 200 ", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NestedOverlappingMountsRunTheSameAdaptedMiddlewareTwiceInOnionOrder()
    {
        var order = new List<string>();
        Middleware<Context> middleware = async (context, next) =>
        {
            order.Add("before");
            context.AppendHeader("X-Marker", "m");
            await next(context);
            order.Add("after");
        };

        var sub = new App<CustomContext>();
        sub.Use(middleware);
        sub.Get("/x", context =>
        {
            order.Add("handler");
            return context.Text("ok");
        });

        var app = new App<CustomContext>();
        app.Route("/a", sub);
        app.Route("/a/b", sub);

        await using var response = await TestApp.Send(app, path: "/a/b/x");

        Assert.Equal("ok", response.BodyText);
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal(["before", "before", "handler", "after", "after"], order);
        var markers = response.Response.Headers["X-Marker"];
        Assert.Equal(2, markers.Count);
        Assert.Equal("m", markers[0]);
        Assert.Equal("m", markers[1]);
    }

    [Fact]
    public void UseRejectsANullApp()
    {
        Middleware<Context> middleware = static (context, next) => next(context);
        Assert.Throws<ArgumentNullException>(() => AppMiddlewareExtensions.Use<CustomContext>(null!, middleware));
        Assert.Throws<ArgumentNullException>(
            () => AppMiddlewareExtensions.Use<CustomContext>(null!, "/admin", middleware));
    }

    [Fact]
    public void UseRejectsNullMiddleware()
    {
        var app = new App<CustomContext>();
        Assert.Throws<ArgumentNullException>(
            () => AppMiddlewareExtensions.Use(app, (Middleware<Context>)null!));
        Assert.Throws<ArgumentNullException>(
            () => AppMiddlewareExtensions.Use(app, "/admin", (Middleware<Context>)null!));
    }

    public sealed class CustomContext : Context;
}
