using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Miya.Middleware;

namespace Miya.Tests;

public sealed class RequestLoggerTests
{
    private static readonly Regex AccessLine = new(
        @"^(GET|POST) (/[^\s]*) (\d{3}) (\d+\.\d)ms$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    [Fact]
    public async Task LogsMethodPathStatusAndElapsedAfterSuccess()
    {
        var log = new StringWriter();
        var app = new App();
        app.Use(RequestLogger.Middleware(new RequestLoggerOptions { Writer = log }));
        app.Get("/users/:id", context => context.Text(context.Param("id")));

        await using var response = await TestApp.Send(app, path: "/users/42");

        Assert.Equal("42", response.BodyText);
        var line = ReadSingleLine(log);
        var match = AccessLine.Match(line);
        Assert.True(match.Success, line);
        Assert.Equal("GET", match.Groups[1].Value);
        Assert.Equal("/users/42", match.Groups[2].Value);
        Assert.Equal("200", match.Groups[3].Value);
        Assert.Equal(1, match.Groups[4].Value.Split('.')[1].Length);
    }

    [Fact]
    public async Task LogsHandlerStatusCode()
    {
        var log = new StringWriter();
        var app = new App();
        app.Use(RequestLogger.Middleware(new RequestLoggerOptions { Writer = log }));
        app.Post("/items", context =>
        {
            context.Status(StatusCodes.Status201Created);
            return context.Text("created");
        });

        await using var response = await TestApp.Send(app, method: "POST", path: "/items");

        Assert.Equal(StatusCodes.Status201Created, response.Response.StatusCode);
        var line = ReadSingleLine(log);
        Assert.StartsWith("POST /items 201 ", line, StringComparison.Ordinal);
        Assert.EndsWith("ms", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LogsNotFound()
    {
        var log = new StringWriter();
        var app = new App();
        app.Use(RequestLogger.Middleware(new RequestLoggerOptions { Writer = log }));
        app.Get("/", context => context.Text("ok"));

        await using var response = await TestApp.Send(app, path: "/missing");

        Assert.Equal(StatusCodes.Status404NotFound, response.Response.StatusCode);
        Assert.StartsWith("GET /missing 404 ", ReadSingleLine(log), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExceptionPathLogs500ThenRethrowsToOnError()
    {
        var log = new StringWriter();
        Exception? observed = null;
        var app = new App();
        app.Use(RequestLogger.Middleware(new RequestLoggerOptions { Writer = log }));
        app.Get("/", context => throw new InvalidOperationException("boom"));
        app.OnError((context, exception) =>
        {
            observed = exception;
            context.Status(599);
            return context.Text("caught");
        });

        await using var response = await TestApp.Send(app);

        Assert.Equal("boom", Assert.IsType<InvalidOperationException>(observed).Message);
        Assert.Equal(599, response.Response.StatusCode);
        Assert.Equal("caught", response.BodyText);
        Assert.StartsWith("GET / 500 ", ReadSingleLine(log), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExceptionPathLogs500WhenTheDefaultErrorHandlerRuns()
    {
        var log = new StringWriter();
        var app = new App();
        app.Use(RequestLogger.Middleware(new RequestLoggerOptions { Writer = log }));
        app.Get("/", context => throw new InvalidOperationException("boom"));

        await using var response = await TestApp.Send(app);

        Assert.Equal(StatusCodes.Status500InternalServerError, response.Response.StatusCode);
        Assert.Equal("Internal Server Error", response.BodyText);
        Assert.StartsWith("GET / 500 ", ReadSingleLine(log), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UsesInvariantCultureForElapsedMilliseconds()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        var log = new StringWriter();
        try
        {
            var german = CultureInfo.GetCultureInfo("de-DE");
            CultureInfo.CurrentCulture = german;
            CultureInfo.CurrentUICulture = german;

            var app = new App();
            app.Use(RequestLogger.Middleware(new RequestLoggerOptions { Writer = log }));
            app.Get("/", context => context.Text("ok"));

            await using var response = await TestApp.Send(app);

            var line = ReadSingleLine(log);
            Assert.DoesNotContain(',', line);
            Assert.Matches(@"^GET / 200 \d+\.\dms$", line);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [Fact]
    public async Task CustomWriterReceivesASingleTerminatedLine()
    {
        var log = new StringWriter { NewLine = "\n" };
        var app = new App();
        app.Use(RequestLogger.Middleware(new RequestLoggerOptions { Writer = log }));
        app.Get("/", context => context.Text("ok"));

        await using var response = await TestApp.Send(app);

        var output = log.ToString();
        Assert.EndsWith("\n", output, StringComparison.Ordinal);
        Assert.Equal(1, CountNewLines(output));
        Assert.DoesNotContain('\r', output);
    }

    [Fact]
    public async Task SequentialRequestsWriteSeparateLines()
    {
        var log = new StringWriter { NewLine = "\n" };
        var app = new App();
        app.Use(RequestLogger.Middleware(new RequestLoggerOptions { Writer = log }));
        app.Get("/a", context => context.Text("a"));
        app.Get("/b", context => context.Text("b"));

        await using var first = await TestApp.Send(app, path: "/a");
        await using var second = await TestApp.Send(app, path: "/b");

        var lines = log.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("GET /a 200 ", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("GET /b 200 ", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task LogsHttp11WebSocketUpgradeAsSwitchingProtocols()
    {
        var log = new StringWriter();
        var app = new App();
        app.Use(RequestLogger.Middleware(new RequestLoggerOptions { Writer = log }));
        app.Get("/ws", context => context.WebSocket(static (socket, _) =>
        {
            socket.Abort();
            return ValueTask.CompletedTask;
        }));

        await using var response = await TestApp.Send(
            app,
            path: "/ws",
            headers: new Dictionary<string, string>
            {
                ["Upgrade"] = "websocket",
                ["Connection"] = "Upgrade",
                ["Sec-WebSocket-Version"] = "13",
                ["Sec-WebSocket-Key"] = "dGhlIHNhbXBsZSBub25jZQ==",
            },
            upgradable: true);

        Assert.Equal(StatusCodes.Status101SwitchingProtocols, response.Response.StatusCode);
        Assert.StartsWith("GET /ws 101 ", ReadSingleLine(log), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentRequestsWriteIntactLinesToAStringWriter()
    {
        const int requestCount = 64;
        var log = new YieldingStringWriter { NewLine = "\n" };
        var releaseHandlers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlersReady = 0;
        var allHandlersReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var app = new App();
        app.Use(RequestLogger.Middleware(new RequestLoggerOptions { Writer = log }));
        app.Get("/request/:id", async context =>
        {
            if (Interlocked.Increment(ref handlersReady) == requestCount)
            {
                allHandlersReady.TrySetResult();
            }

            await releaseHandlers.Task;
            await context.Text(context.Param("id"));
        });

        var requests = Enumerable.Range(0, requestCount)
            .Select(index => TestApp.Send(app, path: string.Concat("/request/", index)))
            .ToArray();
        await allHandlersReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseHandlers.TrySetResult();
        var responses = await Task.WhenAll(requests);
        foreach (var response in responses)
        {
            await response.DisposeAsync();
        }

        var lines = log.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(requestCount, lines.Length);
        Assert.All(lines, line => Assert.Matches(@"^GET /request/\d+ 200 \d+\.\dms$", line));
        Assert.Equal(requestCount, lines.Select(line => line.Split(' ')[1]).Distinct().Count());
    }

    private static string ReadSingleLine(StringWriter log)
    {
        var output = log.ToString();
        Assert.False(string.IsNullOrEmpty(output));
        var newLine = log.NewLine;
        Assert.True(output.EndsWith(newLine, StringComparison.Ordinal), output);
        return output[..^newLine.Length];
    }

    private static int CountNewLines(string value)
    {
        var count = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\n')
            {
                count++;
            }
        }

        return count;
    }

    private sealed class YieldingStringWriter : StringWriter
    {
        public override void Write(string? value)
        {
            if (value is null)
            {
                return;
            }

            foreach (var character in value)
            {
                base.Write(character);
                Thread.Yield();
            }
        }
    }
}
