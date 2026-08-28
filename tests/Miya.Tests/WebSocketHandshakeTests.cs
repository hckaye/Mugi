using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;

namespace Miya.Tests;

public sealed class WebSocketHandshakeTests
{
    [Fact]
    public async Task NonUpgradeRequestReturnsUpgradeRequired()
    {
        var app = CreateAbortApp();

        await using var response = await TestApp.Send(app, path: "/ws");

        Assert.Equal(StatusCodes.Status426UpgradeRequired, response.Response.StatusCode);
        Assert.Equal("websocket", response.Response.Headers.Upgrade);
        Assert.Equal("Upgrade Required", response.BodyText);
    }

    [Theory]
    [InlineData("method")]
    [InlineData("upgrade-missing")]
    [InlineData("upgrade-wrong")]
    [InlineData("connection-missing")]
    [InlineData("connection-wrong")]
    public async Task InvalidUpgradeShapeReturnsUpgradeRequired(string invalidPart)
    {
        var headers = CreateValidHeaders();
        var method = "GET";
        switch (invalidPart)
        {
            case "method":
                method = "POST";
                break;
            case "upgrade-missing":
                headers.Remove("Upgrade");
                break;
            case "upgrade-wrong":
                headers["Upgrade"] = "h2c";
                break;
            case "connection-missing":
                headers.Remove("Connection");
                break;
            case "connection-wrong":
                headers["Connection"] = "keep-alive";
                break;
        }

        var app = new App();
        app.All("/ws", context => context.WebSocket(AbortSocket));

        await using var response = await TestApp.Send(
            app,
            method: method,
            path: "/ws",
            headers: headers,
            upgradable: true);

        Assert.Equal(StatusCodes.Status426UpgradeRequired, response.Response.StatusCode);
        Assert.Equal("websocket", response.Response.Headers.Upgrade);
        Assert.Equal("Upgrade Required", response.BodyText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12")]
    [InlineData("13x")]
    public async Task UnsupportedVersionReturnsRequiredVersion(string? version)
    {
        var headers = CreateValidHeaders();
        if (version is null)
        {
            headers.Remove("Sec-WebSocket-Version");
        }
        else
        {
            headers["Sec-WebSocket-Version"] = version;
        }

        await using var response = await TestApp.Send(
            CreateAbortApp(),
            path: "/ws",
            headers: headers,
            upgradable: true);

        Assert.Equal(StatusCodes.Status426UpgradeRequired, response.Response.StatusCode);
        Assert.Equal("13", response.Response.Headers.SecWebSocketVersion);
        Assert.Equal("Upgrade Required", response.BodyText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("dGhlIHNhbXBsZSBub25jZQ=")]
    [InlineData("dGhlIHNhbXBsZSBub25jZQ==, dGhlIHNhbXBsZSBub25jZQ==")]
    public async Task InvalidKeyReturnsBadRequest(string? key)
    {
        var headers = CreateValidHeaders();
        if (key is null)
        {
            headers.Remove("Sec-WebSocket-Key");
        }
        else
        {
            headers["Sec-WebSocket-Key"] = key;
        }

        await using var response = await TestApp.Send(
            CreateAbortApp(),
            path: "/ws",
            headers: headers,
            upgradable: true);

        Assert.Equal(StatusCodes.Status400BadRequest, response.Response.StatusCode);
        Assert.Equal("Bad Request", response.BodyText);
    }

    [Fact]
    public async Task HostileOversizedKeyReturnsBadRequest()
    {
        var headers = CreateValidHeaders();
        headers["Sec-WebSocket-Key"] = new string('A', 32_768);

        await using var response = await TestApp.Send(
            CreateAbortApp(),
            path: "/ws",
            headers: headers,
            upgradable: true);

        Assert.Equal(StatusCodes.Status400BadRequest, response.Response.StatusCode);
    }

    [Fact]
    public async Task NonUpgradableFeatureReturnsUpgradeRequired()
    {
        await using var response = await TestApp.Send(
            CreateAbortApp(),
            path: "/ws",
            headers: CreateValidHeaders(),
            upgradable: false);

        Assert.Equal(StatusCodes.Status426UpgradeRequired, response.Response.StatusCode);
        Assert.Equal("websocket", response.Response.Headers.Upgrade);
    }

    [Fact]
    public async Task ValidHandshakeUsesRfcAcceptKeyVectorAndPreservesEarlierHeaders()
    {
        string? subProtocol = null;
        var app = new App();
        app.Use(static async (context, next) =>
        {
            context.Header("X-Before-Upgrade", "present");
            await next(context);
        });
        app.Get("/ws", async context =>
        {
            await context.WebSocket((socket, _) =>
            {
                subProtocol = socket.SubProtocol;
                socket.Abort();
                return ValueTask.CompletedTask;
            });
        });
        var headers = CreateValidHeaders();
        headers["Upgrade"] = "h2c, WebSocket";
        headers["Connection"] = "keep-alive, upgrade";
        headers["Sec-WebSocket-Version"] = "12, 13";
        headers["Sec-WebSocket-Key"] = "  dGhlIHNhbXBsZSBub25jZQ==\t";

        await using var response = await TestApp.Send(
            app,
            path: "/ws",
            headers: headers,
            upgradable: true);

        Assert.Equal(StatusCodes.Status101SwitchingProtocols, response.Response.StatusCode);
        Assert.Equal("Upgrade", response.Response.Headers.Connection);
        Assert.Equal("websocket", response.Response.Headers.Upgrade);
        Assert.Equal("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", response.Response.Headers.SecWebSocketAccept);
        Assert.Equal("present", response.Response.Headers["X-Before-Upgrade"]);
        Assert.Null(subProtocol);
        Assert.False(response.ResponseBody.Started);
        Assert.False(response.ResponseBody.Completed);
    }

    [Theory]
    [MemberData(nameof(SubProtocolCases))]
    public async Task SubProtocolUsesServerPreference(
        string? clientProtocols,
        string[] serverProtocols,
        string? expected)
    {
        string? socketProtocol = null;
        var app = new App();
        app.Get("/ws", context => context.WebSocket(
            (socket, _) =>
            {
                socketProtocol = socket.SubProtocol;
                socket.Abort();
                return ValueTask.CompletedTask;
            },
            new WebSocketOptions { SubProtocols = serverProtocols }));
        var headers = CreateValidHeaders();
        if (clientProtocols is not null)
        {
            headers["Sec-WebSocket-Protocol"] = clientProtocols;
        }

        await using var response = await TestApp.Send(
            app,
            path: "/ws",
            headers: headers,
            upgradable: true);

        Assert.Equal(StatusCodes.Status101SwitchingProtocols, response.Response.StatusCode);
        Assert.Equal(expected, response.Response.Headers.SecWebSocketProtocol.ToString() is { Length: > 0 } value
            ? value
            : null);
        Assert.Equal(expected, socketProtocol);
    }

    [Fact]
    public async Task ExtendedConnectUsesGetRouteWithoutHttp1Headers()
    {
        var handlerCalled = false;
        var app = new App();
        app.Get("/rooms/:room", context => context.WebSocket(
            (socket, _) =>
            {
                handlerCalled = context.Param("room") == "general";
                socket.Abort();
                return ValueTask.CompletedTask;
            },
            new WebSocketOptions { SubProtocols = ["chat"] }));
        var headers = new Dictionary<string, string>
        {
            ["Sec-WebSocket-Version"] = "13",
            ["Sec-WebSocket-Protocol"] = "chat",
        };

        await using var response = await TestApp.Send(
            app,
            method: "CONNECT",
            path: "/rooms/general",
            headers: headers,
            extendedConnectProtocol: "WebSocket");

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal("chat", response.Response.Headers.SecWebSocketProtocol);
        Assert.False(response.Response.Headers.ContainsKey("Connection"));
        Assert.False(response.Response.Headers.ContainsKey("Upgrade"));
        Assert.False(response.Response.Headers.ContainsKey("Sec-WebSocket-Accept"));
        Assert.True(handlerCalled);
    }

    [Fact]
    public async Task ExtendedConnectWithWrongVersionReturns426()
    {
        var headers = new Dictionary<string, string>
        {
            ["Sec-WebSocket-Version"] = "12",
        };

        await using var response = await TestApp.Send(
            CreateAbortApp(),
            method: "CONNECT",
            path: "/ws",
            headers: headers,
            extendedConnectProtocol: "websocket");

        Assert.Equal(StatusCodes.Status426UpgradeRequired, response.Response.StatusCode);
        Assert.Equal("13", response.Response.Headers.SecWebSocketVersion);
        Assert.False(response.Response.Headers.ContainsKey("Upgrade"));
    }

    [Fact]
    public async Task OperationsAfterUpgradeUseWebSocketResponseGuard()
    {
        Exception? statusException = null;
        Exception? headerException = null;
        Exception? bodyException = null;
        var app = new App();
        app.Get("/ws", async context =>
        {
            await context.WebSocket(AbortSocket);
            statusException = Record.Exception(() => context.Status(StatusCodes.Status204NoContent));
            headerException = Record.Exception(() => context.Header("X-Late", "value"));
            bodyException = await Record.ExceptionAsync(() => context.Text("late").AsTask());
        });

        await using var response = await TestApp.Send(
            app,
            path: "/ws",
            headers: CreateValidHeaders(),
            upgradable: true);

        AssertWebSocketGuard(statusException);
        AssertWebSocketGuard(headerException);
        AssertWebSocketGuard(bodyException);
        Assert.Equal(StatusCodes.Status101SwitchingProtocols, response.Response.StatusCode);
    }

    [Fact]
    public async Task ThrowingHandlerAbortsConnectionWithoutHttpErrorResponse()
    {
        var app = new App();
        app.Get("/ws", context => context.WebSocket(
            static (_, _) => ValueTask.FromException(new InvalidOperationException("handler failed"))));

        await using var response = await TestApp.Send(
            app,
            path: "/ws",
            headers: CreateValidHeaders(),
            upgradable: true);

        Assert.Equal(StatusCodes.Status101SwitchingProtocols, response.Response.StatusCode);
        Assert.True(response.Lifetime.WasAborted);
        Assert.Empty(response.BodyText);
    }

    [Fact]
    public async Task InvalidServerSubProtocolIsRejectedBeforeUpgrade()
    {
        Exception? captured = null;
        var app = new App();
        app.OnError((context, exception) =>
        {
            captured = exception;
            context.Status(StatusCodes.Status400BadRequest);
            return context.Text("invalid options");
        });
        app.Get("/ws", context => context.WebSocket(
            AbortSocket,
            new WebSocketOptions { SubProtocols = ["chat\r\nInjected: true"] }));

        await using var response = await TestApp.Send(
            app,
            path: "/ws",
            headers: CreateValidHeaders(),
            upgradable: true);

        Assert.IsType<ArgumentException>(captured);
        Assert.Equal(StatusCodes.Status400BadRequest, response.Response.StatusCode);
        Assert.Equal("invalid options", response.BodyText);
        Assert.False(response.Response.Headers.ContainsKey("Injected"));
    }

    [Fact]
    public async Task InvalidKeepAliveIntervalIsRejectedBeforeUpgrade()
    {
        Exception? captured = null;
        var app = new App();
        app.OnError((context, exception) =>
        {
            captured = exception;
            context.Status(StatusCodes.Status400BadRequest);
            return context.Text("invalid options");
        });
        app.Get("/ws", context => context.WebSocket(
            AbortSocket,
            new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(-2) }));

        await using var response = await TestApp.Send(
            app,
            path: "/ws",
            headers: CreateValidHeaders(),
            upgradable: true);

        Assert.IsType<ArgumentOutOfRangeException>(captured);
        Assert.Equal(StatusCodes.Status400BadRequest, response.Response.StatusCode);
        Assert.Equal("invalid options", response.BodyText);
    }

    public static TheoryData<string?, string[], string?> SubProtocolCases => new()
    {
        { " chat , superchat ", ["superchat", "chat"], "superchat" },
        { "chat", ["other"], null },
        { null, ["chat"], null },
        { "chat", [], null },
        { "CHAT", ["chat"], null },
    };

    private static App CreateAbortApp()
    {
        var app = new App();
        app.Get("/ws", context => context.WebSocket(AbortSocket));
        return app;
    }

    private static Dictionary<string, string> CreateValidHeaders() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Upgrade"] = "websocket",
        ["Connection"] = "Upgrade",
        ["Sec-WebSocket-Version"] = "13",
        ["Sec-WebSocket-Key"] = "dGhlIHNhbXBsZSBub25jZQ==",
    };

    private static ValueTask AbortSocket(System.Net.WebSockets.WebSocket socket, CancellationToken _)
    {
        socket.Abort();
        return ValueTask.CompletedTask;
    }

    private static void AssertWebSocketGuard(Exception? exception)
    {
        var invalidOperation = Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal("response already sent via WebSocket upgrade", invalidOperation.Message);
    }
}
