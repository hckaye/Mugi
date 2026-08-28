using Microsoft.AspNetCore.Http;

namespace Mugi.Tests;

public sealed class MiddlewareTests
{
    [Fact]
    public async Task MiddlewareRunsInOnionOrder()
    {
        var order = new List<string>();
        var app = new App();
        app.Use(async (context, next) =>
        {
            order.Add("before-1");
            await next(context);
            order.Add("after-1");
        });
        app.Use(async (context, next) =>
        {
            order.Add("before-2");
            await next(context);
            order.Add("after-2");
        });
        app.Get("/", context =>
        {
            order.Add("handler");
            return context.Text("ok");
        });

        await using var response = await TestApp.Send(app);

        Assert.Equal(
            ["before-1", "before-2", "handler", "after-2", "after-1"],
            order);
        Assert.Equal("ok", response.BodyText);
    }

    [Fact]
    public async Task CallingNextTwiceIsReportedToOnError()
    {
        Exception? observed = null;
        var app = new App();
        app.Use(async (context, next) =>
        {
            await next(context);
            await next(context);
        });
        app.Get("/", context => context.Text("discarded"));
        app.OnError((context, exception) =>
        {
            observed = exception;
            context.Status(598);
            return context.Text("caught");
        });

        await using var response = await TestApp.Send(app);

        Assert.IsType<InvalidOperationException>(observed);
        Assert.Contains("only once", observed.Message, StringComparison.Ordinal);
        Assert.Equal(598, response.Response.StatusCode);
        Assert.Equal("caught", response.BodyText);
    }

    [Fact]
    public async Task PatternMiddlewareOnlyWrapsMatchingPath()
    {
        var calls = 0;
        var app = new App();
        app.Use("/matched/:id", async (context, next) =>
        {
            calls++;
            await next(context);
        });
        app.Get("/matched/:id", context => context.Text(context.Param("id")));
        app.Get("/other", context => context.Text("other"));

        await using var matched = await TestApp.Send(app, path: "/matched/1");
        await using var other = await TestApp.Send(app, path: "/other");

        Assert.Equal(1, calls);
        Assert.Equal("1", matched.BodyText);
        Assert.Equal("other", other.BodyText);
    }

    [Fact]
    public async Task MiddlewareCanSetHeaderAfterNextForBufferedResponse()
    {
        var app = new App();
        app.Use(async (context, next) =>
        {
            await next(context);
            context.Header("X-After", "yes");
        });
        app.Get("/", context => context.Text("ok"));

        await using var response = await TestApp.Send(app);

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal("yes", response.Response.Headers["X-After"].ToString());
    }
}
