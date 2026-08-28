using System.Text;
using Microsoft.AspNetCore.Http;

namespace Miya.Tests;

public sealed class SseTests
{
    [Fact]
    public async Task EventStreamSetsSseHeaders()
    {
        var app = new App();
        app.Get("/", context => context.EventStream(static (sse, _) => sse.Send("hello")));

        await using var response = await TestApp.Send(app);

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal("text/event-stream", response.Response.Headers["Content-Type"].ToString());
        Assert.Equal("no-cache", response.Response.Headers["Cache-Control"].ToString());
        Assert.Equal("no", response.Response.Headers["X-Accel-Buffering"].ToString());
        Assert.False(response.Response.Headers.ContainsKey("Content-Length"));
        Assert.True(response.ResponseBody.Started);
        Assert.True(response.ResponseBody.Completed);
    }

    [Fact]
    public async Task SendWritesEventIdThenDataThenBlankLine()
    {
        var app = new App();
        app.Get("/", context => context.EventStream(static (sse, _) => sse.Send("hello", "x", "1")));

        await using var response = await TestApp.Send(app);

        Assert.Equal("event: x\nid: 1\ndata: hello\n\n"u8.ToArray(), response.ResponseBody.Body.ToArray());
    }

    [Fact]
    public async Task SendOmitsMissingEventAndIdFields()
    {
        var app = new App();
        app.Get("/", context => context.EventStream(static (sse, _) => sse.Send("hello")));

        await using var response = await TestApp.Send(app);

        Assert.Equal("data: hello\n\n"u8.ToArray(), response.ResponseBody.Body.ToArray());
    }

    [Fact]
    public async Task SendSplitsMultilineDataOnLfCrLfAndCr()
    {
        var app = new App();
        app.Get("/", context => context.EventStream(async (sse, _) =>
        {
            await sse.Send("a\nb");
            await sse.Send("c\r\nd");
            await sse.Send("e\rf");
        }));

        await using var response = await TestApp.Send(app);

        Assert.Equal(
            "data: a\ndata: b\n\ndata: c\ndata: d\n\ndata: e\ndata: f\n\n"u8.ToArray(),
            response.ResponseBody.Body.ToArray());
    }

    [Fact]
    public async Task SendTreatsEmptyDataAsASingleDataLine()
    {
        var app = new App();
        app.Get("/", context => context.EventStream(static (sse, _) => sse.Send(string.Empty)));

        await using var response = await TestApp.Send(app);

        Assert.Equal("data: \n\n"u8.ToArray(), response.ResponseBody.Body.ToArray());
    }

    [Fact]
    public async Task SendEmitsTrailingEmptyDataLineWhenPayloadEndsWithNewline()
    {
        var app = new App();
        app.Get("/", context => context.EventStream(static (sse, _) => sse.Send("hello\n")));

        await using var response = await TestApp.Send(app);

        Assert.Equal("data: hello\ndata: \n\n"u8.ToArray(), response.ResponseBody.Body.ToArray());
    }

    [Fact]
    public async Task CommentWritesPrefixedLinesWithoutABlankTerminator()
    {
        var app = new App();
        app.Get("/", context => context.EventStream(async (sse, _) =>
        {
            await sse.Comment("keep-alive");
            await sse.Comment("a\nb\r\nc\rd");
            await sse.Comment(string.Empty);
        }));

        await using var response = await TestApp.Send(app);

        Assert.Equal(
            ": keep-alive\n: a\n: b\n: c\n: d\n: \n"u8.ToArray(),
            response.ResponseBody.Body.ToArray());
    }

    [Fact]
    public async Task RetryWritesWholeMilliseconds()
    {
        var app = new App();
        app.Get("/", context => context.EventStream(static (sse, _) => sse.Retry(TimeSpan.FromSeconds(1.5))));

        await using var response = await TestApp.Send(app);

        Assert.Equal("retry: 1500\n"u8.ToArray(), response.ResponseBody.Body.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task RetryRejectsNonPositiveIntervals(int milliseconds)
    {
        Exception? observed = null;
        var app = new App();
        app.Get("/", context => context.EventStream((sse, _) =>
        {
            observed = Record.Exception(() => sse.Retry(TimeSpan.FromMilliseconds(milliseconds)));
            return ValueTask.CompletedTask;
        }));

        await using var response = await TestApp.Send(app);

        Assert.IsType<ArgumentOutOfRangeException>(observed);
        Assert.Empty(response.ResponseBody.Body.ToArray());
    }

    [Fact]
    public async Task RetryRejectsFractionalMilliseconds()
    {
        Exception? observed = null;
        var app = new App();
        app.Get("/", context => context.EventStream((sse, _) =>
        {
            observed = Record.Exception(() => sse.Retry(TimeSpan.FromTicks(1)));
            return ValueTask.CompletedTask;
        }));

        await using var response = await TestApp.Send(app);

        Assert.IsType<ArgumentOutOfRangeException>(observed);
    }

    [Theory]
    [InlineData("x\ny")]
    [InlineData("x\ry")]
    [InlineData("x\0y")]
    public async Task SendRejectsEventNamesWithCrLfOrNul(string eventName)
    {
        Exception? observed = null;
        var app = new App();
        app.Get("/", context => context.EventStream((sse, _) =>
        {
            observed = Record.Exception(() => sse.Send("hello", eventName));
            return ValueTask.CompletedTask;
        }));

        await using var response = await TestApp.Send(app);

        var exception = Assert.IsType<ArgumentException>(observed);
        Assert.Equal("eventName", exception.ParamName);
        Assert.Empty(response.ResponseBody.Body.ToArray());
    }

    [Theory]
    [InlineData("1\n2")]
    [InlineData("1\r2")]
    [InlineData("1\02")]
    public async Task SendRejectsIdsWithCrLfOrNul(string id)
    {
        Exception? observed = null;
        var app = new App();
        app.Get("/", context => context.EventStream((sse, _) =>
        {
            observed = Record.Exception(() => sse.Send("hello", id: id));
            return ValueTask.CompletedTask;
        }));

        await using var response = await TestApp.Send(app);

        var exception = Assert.IsType<ArgumentException>(observed);
        Assert.Equal("id", exception.ParamName);
        Assert.Empty(response.ResponseBody.Body.ToArray());
    }

    [Fact]
    public async Task InvalidIdDoesNotLeaveAPartialEventBeforeTheNextSend()
    {
        Exception? observed = null;
        var app = new App();
        app.Get("/", context => context.EventStream(async (sse, _) =>
        {
            observed = Record.Exception(() => sse.Send("discarded", "stale", "bad\nid"));
            await sse.Send("clean");
        }));

        await using var response = await TestApp.Send(app);

        Assert.IsType<ArgumentException>(observed);
        Assert.Equal("data: clean\n\n"u8.ToArray(), response.ResponseBody.Body.ToArray());
    }

    [Fact]
    public async Task SendWritesUnicodePayloadAsUtf8()
    {
        var app = new App();
        app.Get("/", context => context.EventStream(static (sse, _) => sse.Send("こんにちは😀")));

        await using var response = await TestApp.Send(app);

        Assert.Equal(Encoding.UTF8.GetBytes("data: こんにちは😀\n\n"), response.ResponseBody.Body.ToArray());
    }

    [Fact]
    public async Task EventStreamRejectsNullWriter()
    {
        Exception? observed = null;
        var app = new App();
        app.Get("/", context =>
        {
            observed = Record.Exception(() => context.EventStream(null!));
            return context.Text("ok");
        });

        await using var response = await TestApp.Send(app);

        Assert.IsType<ArgumentNullException>(observed);
        Assert.Equal("ok", response.BodyText);
    }

    [Fact]
    public async Task EventStreamLocksHeadersOnceStreamingStarts()
    {
        InvalidOperationException? observed = null;
        var app = new App();
        app.Get("/", async context =>
        {
            await context.EventStream(static (sse, _) => sse.Send("hello"));
            observed = Assert.Throws<InvalidOperationException>(() => context.Header("X-Late", "no"));
        });

        await using var response = await TestApp.Send(app);

        Assert.NotNull(observed);
        Assert.Equal("data: hello\n\n"u8.ToArray(), response.ResponseBody.Body.ToArray());
    }

    [Fact]
    public async Task SendFlushesEachEventBeforeTheHandlerContinues()
    {
        await using var exchange = TestExchange.Create();
        var firstEvent = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueWriting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var app = new App();
        app.Get("/", context => context.EventStream(async (sse, _) =>
        {
            await sse.Send("hello", "x", "1");
            firstEvent.TrySetResult(exchange.ResponseBody.Body.ToArray());
            await continueWriting.Task;
            await sse.Send("world");
        }));

        var execute = app.ExecuteAsync(exchange.Features).AsTask();
        var observed = await firstEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("event: x\nid: 1\ndata: hello\n\n"u8.ToArray(), observed);

        continueWriting.TrySetResult();
        await execute;
        Assert.Equal(
            "event: x\nid: 1\ndata: hello\n\ndata: world\n\n"u8.ToArray(),
            exchange.ResponseBody.Body.ToArray());
    }

    [Fact]
    public async Task CancelledSendPropagatesWithoutReplacingTheResponse()
    {
        await using var exchange = TestExchange.Create();
        Exception? observed = null;
        var app = new App();
        app.Get("/", context => context.EventStream(async (sse, _) =>
        {
            await sse.Send("one");
            exchange.Lifetime.Abort();
            observed = await Record.ExceptionAsync(async () => await sse.Send("two"));
        }));

        await app.ExecuteAsync(exchange.Features);

        Assert.IsAssignableFrom<OperationCanceledException>(observed);
        Assert.True(exchange.Lifetime.WasAborted);
        Assert.Equal("data: one\n\n"u8.ToArray(), exchange.ResponseBody.Body.ToArray());
        Assert.NotEqual(StatusCodes.Status500InternalServerError, exchange.Response.StatusCode);
    }

    [Fact]
    public async Task SendAndCommentRejectNullPayloads()
    {
        Exception? sendObserved = null;
        Exception? commentObserved = null;
        var app = new App();
        app.Get("/", context => context.EventStream((sse, _) =>
        {
            sendObserved = Record.Exception(() => sse.Send(null!));
            commentObserved = Record.Exception(() => sse.Comment(null!));
            return ValueTask.CompletedTask;
        }));

        await using var response = await TestApp.Send(app);

        Assert.IsType<ArgumentNullException>(sendObserved);
        Assert.IsType<ArgumentNullException>(commentObserved);
    }
}
