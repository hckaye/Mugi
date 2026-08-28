using System.Collections.Concurrent;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using TimeoutFeature = Miya.Middleware.RequestTimeout;

namespace Miya.Tests;

public sealed class RequestTimeoutTests
{
    [Fact]
    public async Task FastHandlerIsUnaffected()
    {
        var app = new App();
        app.Use(TimeoutFeature.Middleware(TimeSpan.FromSeconds(1)));
        app.Get("/", context => context.Text("ok"));

        await using var response = await TestApp.Send(app);

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal("ok", response.BodyText);
    }

    [Fact(Timeout = 10_000)]
    public async Task SlowHandlerReturnsGatewayTimeoutNearTheDeadline()
    {
        var app = new App();
        app.Use(TimeoutFeature.Middleware(TimeSpan.FromMilliseconds(40)));
        app.Get("/", async context =>
        {
            await Task.Delay(500);
            await context.Text("late");
        });

        var stopwatch = Stopwatch.StartNew();
        await using var response = await TestApp.Send(app);
        stopwatch.Stop();

        Assert.Equal(StatusCodes.Status504GatewayTimeout, response.Response.StatusCode);
        Assert.Equal("Gateway Timeout", response.BodyText);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(300));
    }

    [Fact(Timeout = 10_000)]
    public async Task TimedOutHandlerCannotWriteAfterTheResponseIsSent()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeResult = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var app = new App();
        app.Use(TimeoutFeature.Middleware(TimeSpan.FromMilliseconds(20)));
        app.Get("/", async context =>
        {
            await release.Task;
            writeResult.TrySetResult(await Record.ExceptionAsync(async () =>
            {
                context.Status(201);
                context.Header("X-Late", "yes");
                await context.Text("late");
            }));
        });

        await using var response = await TestApp.Send(app);
        release.TrySetResult();
        var exception = await writeResult.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(StatusCodes.Status504GatewayTimeout, response.Response.StatusCode);
        Assert.IsType<InvalidOperationException>(exception);
        Assert.False(response.Response.Headers.ContainsKey("X-Late"));
        Assert.Equal("Gateway Timeout", response.BodyText);
    }

    [Fact(Timeout = 10_000)]
    public async Task StreamingResponseIsAbortedInsteadOfReplaced()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var app = new App();
        app.Use(TimeoutFeature.Middleware(TimeSpan.FromMilliseconds(20)));
        app.Get("/", context => context.Stream(
            "text/plain; charset=utf-8",
            async (writer, _) =>
            {
                writer.Write("started"u8);
                entered.TrySetResult();
                await release.Task;
            }));

        var send = TestApp.Send(app);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await using var response = await send.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(response.Lifetime.WasAborted);
        Assert.NotEqual(StatusCodes.Status504GatewayTimeout, response.Response.StatusCode);
        release.TrySetResult();
    }

    [Fact(Timeout = 20_000)]
    public async Task PooledContextReuseDoesNotAllowTimedOutHandlersToCorruptLaterResponses()
    {
        const int requestCount = 40;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var results = new ConcurrentQueue<Exception?>();
        var slowContexts = new ConcurrentQueue<PoolableTimeoutContext>();
        var fastContexts = new ConcurrentQueue<PoolableTimeoutContext>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remaining = requestCount;
        var app = new App<PoolableTimeoutContext>();
        var timeout = TimeoutFeature.Middleware(TimeSpan.FromMilliseconds(2));
        app.Use((context, next) => timeout(context, _ => next(context)));
        app.Get("/slow", async context =>
        {
            slowContexts.Enqueue(context);
            await release.Task;
            context.Value = 42;
            results.Enqueue(await Record.ExceptionAsync(async () =>
            {
                context.Header("X-Zombie", "yes");
                await context.Text("zombie");
            }));
            if (Interlocked.Decrement(ref remaining) == 0)
            {
                completed.TrySetResult();
            }
        });
        app.Get("/fast", context =>
        {
            fastContexts.Enqueue(context);
            return context.Text(context.Value == 0 ? "clean" : "corrupt");
        });

        for (var index = 0; index < requestCount; index++)
        {
            await using var timedOut = await TestApp.Send(app, path: "/slow");
            Assert.Equal(StatusCodes.Status504GatewayTimeout, timedOut.Response.StatusCode);

            await using var clean = await TestApp.Send(app, path: "/fast");
            Assert.Equal("clean", clean.BodyText);
            Assert.False(clean.Response.Headers.ContainsKey("X-Zombie"));
        }

        var timedOutContexts = slowContexts.ToArray();
        var laterRequests = fastContexts.ToArray();
        Assert.Equal(requestCount, timedOutContexts.Length);
        Assert.Equal(requestCount, laterRequests.Length);
        for (var timedOutIndex = 0; timedOutIndex < timedOutContexts.Length; timedOutIndex++)
        {
            for (var laterIndex = timedOutIndex; laterIndex < laterRequests.Length; laterIndex++)
            {
                Assert.NotSame(timedOutContexts[timedOutIndex], laterRequests[laterIndex]);
            }
        }

        release.TrySetResult();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(requestCount, results.Count);
        Assert.All(results, exception =>
            Assert.True(exception is InvalidOperationException or ObjectDisposedException));
    }

    [Fact(Timeout = 10_000)]
    public async Task TimeoutCancelsTheTokenObservedByTheHandler()
    {
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var app = new App();
        app.Use(TimeoutFeature.Middleware(TimeSpan.FromMilliseconds(20)));
        app.Get("/", async context =>
        {
            var aborted = context.Aborted;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), aborted);
            }
            catch (OperationCanceledException) when (aborted.IsCancellationRequested)
            {
                canceled.TrySetResult();
                throw;
            }
        });

        await using var response = await TestApp.Send(app);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(StatusCodes.Status504GatewayTimeout, response.Response.StatusCode);
        Assert.Equal("Gateway Timeout", response.BodyText);
    }

    [Fact(Timeout = 20_000)]
    public async Task StreamingAndTimeoutRaceProducesOnlyOneResponse()
    {
        for (var index = 0; index < 100; index++)
        {
            var app = new App();
            app.Use(TimeoutFeature.Middleware(TimeSpan.FromMilliseconds(2)));
            app.Get("/", async context =>
            {
                await Task.Delay(index % 3 == 0 ? 1 : 2);
                await context.Stream(
                    "text/plain; charset=utf-8",
                    static (writer, _) =>
                    {
                        writer.Write("stream"u8);
                        return ValueTask.CompletedTask;
                    });
            });

            await using var response = await TestApp.Send(app);
            if (response.Response.StatusCode == StatusCodes.Status504GatewayTimeout)
            {
                Assert.Equal("Gateway Timeout", response.BodyText);
                Assert.False(response.Lifetime.WasAborted);
            }
            else
            {
                Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
                Assert.DoesNotContain("Gateway Timeout", response.BodyText, StringComparison.Ordinal);
                Assert.True(response.BodyText == "stream" || response.Lifetime.WasAborted);
            }
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task WebSocketUpgradeAndTimeoutNeverBothWriteAResponse()
    {
        var upgrade = new BlockingUpgradeFeature();
        var app = new App();
        app.Use(TimeoutFeature.Middleware(TimeSpan.FromMilliseconds(20)));
        app.Get("/ws", context => context.WebSocket(static (socket, _) =>
        {
            socket.Abort();
            return ValueTask.CompletedTask;
        }));
        await using var exchange = TestExchange.Create(
            method: "GET",
            path: "/ws",
            headers: new Dictionary<string, string>
            {
                ["Upgrade"] = "websocket",
                ["Connection"] = "Upgrade",
                ["Sec-WebSocket-Version"] = "13",
                ["Sec-WebSocket-Key"] = "dGhlIHNhbXBsZSBub25jZQ==",
            });
        upgrade.Attach(exchange.Response);
        exchange.Features.Set<IHttpUpgradeFeature>(upgrade);

        var execution = app.ExecuteAsync(exchange.Features).AsTask();
        await upgrade.Entered.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(60);
        upgrade.Release();
        await upgrade.Completed.WaitAsync(TimeSpan.FromSeconds(2));
        await execution.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(upgrade.WasUpgraded);
        Assert.Equal(StatusCodes.Status101SwitchingProtocols, exchange.Response.StatusCode);
        Assert.Empty(exchange.BodyText);
    }

    [Fact]
    public async Task TimeoutResponseControlIsActivatedOnlyByTimeoutMiddleware()
    {
        var defaultControlEnabled = true;
        var defaultApp = new App();
        defaultApp.Get("/", context =>
        {
            defaultControlEnabled = context.TimeoutResponseControlEnabled;
            return context.Text("ok");
        });

        var timeoutControlEnabled = false;
        var timeoutApp = new App();
        timeoutApp.Use(TimeoutFeature.Middleware(TimeSpan.FromSeconds(1)));
        timeoutApp.Get("/", context =>
        {
            timeoutControlEnabled = context.TimeoutResponseControlEnabled;
            return context.Text("ok");
        });

        await using var defaultResponse = await TestApp.Send(defaultApp);
        await using var timeoutResponse = await TestApp.Send(timeoutApp);

        Assert.False(defaultControlEnabled);
        Assert.True(timeoutControlEnabled);
    }

    [Fact(Timeout = 20_000)]
    public async Task DeadlineBoundaryNeverProducesMixedResponses()
    {
        for (var index = 0; index < 200; index++)
        {
            var app = new App();
            app.Use(TimeoutFeature.Middleware(TimeSpan.FromMilliseconds(2)));
            app.Get("/", async context =>
            {
                await Task.Delay(index % 3 == 0 ? 1 : 2);
                await context.Text("handler");
            });

            await using var response = await TestApp.Send(app);
            if (response.Response.StatusCode == StatusCodes.Status200OK)
            {
                Assert.Equal("handler", response.BodyText);
            }
            else
            {
                Assert.Equal(StatusCodes.Status504GatewayTimeout, response.Response.StatusCode);
                Assert.Equal("Gateway Timeout", response.BodyText);
            }
        }
    }

    [Fact]
    public void TimeoutIsValidatedWhenMiddlewareIsCreated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeoutFeature.Middleware(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeoutFeature.Middleware(TimeSpan.FromMilliseconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeoutFeature.Middleware(TimeSpan.MaxValue));
    }

    public sealed class PoolableTimeoutContext : Context, IPoolableContext
    {
        public int Value { get; set; }

        public void OnReturn()
        {
            Value = 0;
        }
    }

    private sealed class BlockingUpgradeFeature : IHttpUpgradeFeature
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IHttpResponseFeature? _response;

        public bool IsUpgradableRequest => true;

        public bool WasUpgraded { get; private set; }

        public Task Entered => _entered.Task;

        public Task Completed => _completed.Task;

        public void Attach(IHttpResponseFeature response) => _response = response;

        public void Release() => _release.TrySetResult();

        public async Task<Stream> UpgradeAsync()
        {
            _entered.TrySetResult();
            await _release.Task;
            WasUpgraded = true;
            _response!.StatusCode = StatusCodes.Status101SwitchingProtocols;
            _response.Headers["Connection"] = "Upgrade";
            _completed.TrySetResult();
            return Stream.Null;
        }
    }
}
