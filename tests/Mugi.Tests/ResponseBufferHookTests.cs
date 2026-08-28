using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Mugi.Tests;

public sealed class ResponseBufferHookTests
{
    [Fact]
    public async Task BufferedBodyCanBeReadAndReplaced()
    {
        string? observed = null;
        var app = new App();
        app.Use(async (context, next) =>
        {
            await next(context);
            Assert.True(context.TryGetBufferedResponse(out var body));
            observed = Encoding.UTF8.GetString(body.Span);
            context.ReplaceBufferedResponse("changed"u8.ToArray(), "application/octet-stream");
        });
        app.Get("/", context => context.Text("original"));

        await using var response = await TestApp.Send(app);

        Assert.Equal("original", observed);
        Assert.Equal("changed", response.BodyText);
        Assert.Equal("application/octet-stream", response.Response.Headers.ContentType.ToString());
        Assert.Equal(7, response.Response.Headers.ContentLength);
    }

    [Fact]
    public async Task HeadUsesReplacementLengthWithoutSendingTheBody()
    {
        var app = new App();
        app.Use(async (context, next) =>
        {
            await next(context);
            Assert.True(context.TryGetBufferedResponse(out var body));
            Assert.Equal("original", Encoding.UTF8.GetString(body.Span));
            context.ReplaceBufferedResponse("replacement"u8.ToArray());
        });
        app.Get("/", context => context.Text("original"));

        await using var response = await TestApp.Send(app, method: "HEAD");

        Assert.Empty(response.ResponseBody.Body.ToArray());
        Assert.Equal(11, response.Response.Headers.ContentLength);
    }

    [Fact]
    public async Task EmptyAndAutomaticallyPromotedResponsesAreNotExposed()
    {
        var emptyAvailable = true;
        var promotedAvailable = true;
        var empty = new App();
        empty.Use(async (context, next) =>
        {
            await next(context);
            emptyAvailable = context.TryGetBufferedResponse(out _);
        });
        empty.Get("/", context => context.Text(string.Empty));

        var promoted = new App();
        promoted.Use(async (context, next) =>
        {
            await next(context);
            promotedAvailable = context.TryGetBufferedResponse(out _);
        });
        promoted.Get("/", context => context.Bytes("12345"u8.ToArray(), "application/octet-stream"));

        await using var emptyResponse = await TestApp.Send(empty);
        await using var promotedResponse = await TestApp.Send(
            promoted,
            options: new AppOptions { MaxBufferedResponseBytes = 4 });

        Assert.False(emptyAvailable);
        Assert.False(promotedAvailable);
        Assert.Equal("12345", promotedResponse.BodyText);
    }

    [Fact]
    public async Task BufferedBodyCanBeReplacedMoreThanOnce()
    {
        var app = new App();
        app.Use(async (context, next) =>
        {
            await next(context);
            context.ReplaceBufferedResponse("first"u8.ToArray());
            context.ReplaceBufferedResponse("second"u8.ToArray());
        });
        app.Get("/", context => context.Text("original"));

        await using var response = await TestApp.Send(app);

        Assert.Equal("second", response.BodyText);
        Assert.Equal(6, response.Response.Headers.ContentLength);
    }

    [Fact]
    public async Task ReplacementAfterStreamingHasStartedThrows()
    {
        Exception? observed = null;
        var app = new App();
        app.Use(async (context, next) =>
        {
            await next(context);
            observed = Record.Exception(() => context.ReplaceBufferedResponse("late"u8.ToArray()));
        });
        app.Get("/", context => context.Stream(
            "text/plain; charset=utf-8",
            static (writer, _) =>
            {
                writer.Write("streamed"u8);
                return ValueTask.CompletedTask;
            }));

        await using var response = await TestApp.Send(app);

        Assert.IsType<InvalidOperationException>(observed);
        Assert.Equal("streamed", response.BodyText);
    }

    [Theory]
    [InlineData(StatusCodes.Status101SwitchingProtocols)]
    [InlineData(StatusCodes.Status204NoContent)]
    [InlineData(StatusCodes.Status304NotModified)]
    public async Task ReplacementDoesNotAddBodiesToForbiddenStatuses(int status)
    {
        var app = new App();
        app.Use(async (context, next) =>
        {
            await next(context);
            context.ReplaceBufferedResponse("forbidden"u8.ToArray());
            Assert.False(context.TryGetBufferedResponse(out _));
        });
        app.Get("/", context =>
        {
            context.Status(status);
            return context.Text("discarded");
        });

        await using var response = await TestApp.Send(app);

        Assert.Empty(response.ResponseBody.Body.ToArray());
        Assert.False(response.Response.Headers.ContainsKey("Content-Length"));
    }
}
