using System.Buffers;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Http;

namespace Miya.Tests;

public sealed class ResponseTests
{
    [Fact]
    public async Task LastBufferedBodyWins()
    {
        var app = new App();
        app.Get("/", async context =>
        {
            await context.Text("first");
            await context.Text("second");
        });

        await using var response = await TestApp.Send(app);

        Assert.Equal("second", response.BodyText);
        Assert.Equal("6", response.Response.Headers.ContentLength.ToString());
    }

    [Fact]
    public async Task ExplicitStreamingLocksHeadersAndCompletesWriter()
    {
        InvalidOperationException? observed = null;
        var app = new App();
        app.Get("/", async context =>
        {
            await context.Stream("text/plain", static (writer, _) =>
            {
                writer.Write("streamed"u8);
                return ValueTask.CompletedTask;
            });

            observed = Assert.Throws<InvalidOperationException>(() => context.Header("X-Late", "no"));
        });

        await using var response = await TestApp.Send(app);

        Assert.NotNull(observed);
        Assert.True(response.ResponseBody.Started);
        Assert.True(response.ResponseBody.Completed);
        Assert.Equal("streamed", response.BodyText);
    }

    [Fact]
    public async Task BufferLimitAutomaticallySwitchesToStreaming()
    {
        var headerWasRejected = false;
        var app = new App();
        app.Get("/", async context =>
        {
            await context.Text("12345");
            headerWasRejected = Assert.Throws<InvalidOperationException>(
                () => context.Header("X-Late", "no")) is not null;
        });

        await using var response = await TestApp.Send(
            app,
            options: new Options { MaxBufferedResponseBytes = 4 });

        Assert.True(headerWasRejected);
        Assert.True(response.ResponseBody.Completed);
        Assert.Equal("12345", response.BodyText);
        Assert.False(response.Response.Headers.ContainsKey("Content-Length"));
    }

    [Fact]
    public async Task OnErrorReplacesBufferedResponse()
    {
        var app = new App();
        app.Get("/", async context =>
        {
            await context.Text("discard me");
            throw new InvalidOperationException("boom");
        });
        app.OnError((context, exception) =>
        {
            Assert.Equal("boom", exception.Message);
            context.Status(599);
            context.Header("X-Error", "handled");
            return context.Text("replacement");
        });

        await using var response = await TestApp.Send(app);

        Assert.Equal(599, response.Response.StatusCode);
        Assert.Equal("handled", response.Response.Headers["X-Error"].ToString());
        Assert.Equal("replacement", response.BodyText);
    }

    [Theory]
    [InlineData(101)]
    [InlineData(204)]
    [InlineData(304)]
    public async Task BodylessStatusRemovesBodyAndContentLength(int status)
    {
        var app = new App();
        app.Get("/", async context =>
        {
            await context.Text("must not be sent");
            context.Status(status);
        });

        await using var response = await TestApp.Send(app);

        Assert.Equal(status, response.Response.StatusCode);
        Assert.Empty(response.BodyText);
        Assert.False(response.Response.Headers.ContainsKey("Content-Length"));
    }

    [Fact]
    public async Task ErrorAfterStreamingAbortsInsteadOfReplacingResponse()
    {
        var onErrorCalled = false;
        var app = new App();
        app.Use(async (context, next) =>
        {
            await next(context);
            throw new InvalidOperationException("late");
        });
        app.Get("/", context => context.Stream(
            "text/plain",
            static (writer, _) =>
            {
                writer.Write("sent"u8);
                return ValueTask.CompletedTask;
            }));
        app.OnError((context, exception) =>
        {
            onErrorCalled = true;
            return context.Text("replacement");
        });

        await using var response = await TestApp.Send(app);

        Assert.False(onErrorCalled);
        Assert.True(response.Lifetime.WasAborted);
        Assert.Equal("sent", response.BodyText);
    }
}
