using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Mugi.IntegrationTests;

public sealed class WebSocketIntegrationTests
{
    private const int TestTimeoutMilliseconds = 15_000;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task EchoesTextBinaryAndFragmentedMessages()
    {
        var app = CreateEchoApp();
        await using var server = await StartAsync(app);
        using var socket = new ClientWebSocket();
        await ConnectAsync(socket, server, "/ws");

        var text = Encoding.UTF8.GetBytes("hello");
        await socket.SendAsync(text, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        await AssertMessage(socket, text, WebSocketMessageType.Text);

        byte[] binary = [0, 1, 2, 127, 128, 254, 255];
        await socket.SendAsync(binary, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
        await AssertMessage(socket, binary, WebSocketMessageType.Binary);

        var fragmented = new byte[256 * 1024];
        for (var index = 0; index < fragmented.Length; index++)
        {
            fragmented[index] = (byte)(index % 251);
        }

        const int fragmentLength = 16 * 1024;
        for (var offset = 0; offset < fragmented.Length; offset += fragmentLength)
        {
            var count = Math.Min(fragmentLength, fragmented.Length - offset);
            await socket.SendAsync(
                new ArraySegment<byte>(fragmented, offset, count),
                WebSocketMessageType.Binary,
                endOfMessage: offset + count == fragmented.Length,
                CancellationToken.None);
        }

        await AssertMessage(socket, fragmented, WebSocketMessageType.Binary);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        Assert.Equal(WebSocketState.Closed, socket.State);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task NegotiatesSubProtocolInServerPreferenceOrder()
    {
        var app = new App();
        app.Get("/ws", context => context.WebSocket(
            Echo,
            new WebSocketOptions { SubProtocols = ["superchat", "chat"] }));
        await using var server = await StartAsync(app);
        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol("chat");
        socket.Options.AddSubProtocol("superchat");

        await ConnectAsync(socket, server, "/ws");

        Assert.Equal("superchat", socket.SubProtocol);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task ServerInitiatedCloseIsVisibleToClient()
    {
        var handlerCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var app = new App();
        app.Get("/ws", context => context.WebSocket(async (socket, token) =>
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "server closing", token);
            handlerCompleted.TrySetResult();
        }));
        await using var server = await StartAsync(app);
        using var socket = new ClientWebSocket();
        await ConnectAsync(socket, server, "/ws");

        var result = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[1]), CancellationToken.None);

        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, result.CloseStatus);
        Assert.Equal("server closing", result.CloseStatusDescription);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        await handlerCompleted.Task.WaitAsync(OperationTimeout);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task ClientInitiatedCloseCompletesHandler()
    {
        var handlerCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var app = new App();
        app.Get("/ws", context => context.WebSocket(async (socket, token) =>
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[1]), token);
            Assert.Equal(WebSocketMessageType.Close, result.MessageType);
            Assert.Equal(WebSocketCloseStatus.NormalClosure, result.CloseStatus);
            handlerCompleted.TrySetResult();
        }));
        await using var server = await StartAsync(app);
        using var socket = new ClientWebSocket();
        await ConnectAsync(socket, server, "/ws");

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "client closing", CancellationToken.None);

        await handlerCompleted.Task.WaitAsync(OperationTimeout);
        Assert.Equal(WebSocketState.Closed, socket.State);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task HandlerExceptionClosesOrAbortsClientWithoutWedge()
    {
        var app = new App();
        app.Get("/ws", context => context.WebSocket(static async (_, token) =>
        {
            await Task.Delay(50, token);
            throw new InvalidOperationException("handler failed");
        }));
        await using var server = await StartAsync(app);
        using var socket = new ClientWebSocket();
        await ConnectAsync(socket, server, "/ws");

        WebSocketReceiveResult? result = null;
        var exception = await Record.ExceptionAsync(async () =>
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[1]), CancellationToken.None);
        });

        if (exception is null)
        {
            Assert.NotNull(result);
            Assert.Equal(WebSocketMessageType.Close, result.MessageType);
            Assert.Equal(WebSocketCloseStatus.InternalServerError, result.CloseStatus);
        }
        else
        {
            Assert.IsType<WebSocketException>(exception);
        }
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task KeepAliveLeavesIdleConnectionUsable()
    {
        var app = new App();
        app.Get("/ws", context => context.WebSocket(
            Echo,
            new WebSocketOptions { KeepAliveInterval = TimeSpan.FromMilliseconds(100) }));
        await using var server = await StartAsync(app);
        using var socket = new ClientWebSocket();
        await ConnectAsync(socket, server, "/ws");

        await Task.Delay(TimeSpan.FromSeconds(3));

        Assert.Equal(WebSocketState.Open, socket.State);
        var message = Encoding.UTF8.GetBytes("still open");
        await socket.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        await AssertMessage(socket, message, WebSocketMessageType.Text);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task TwoSocketsCanExchangeMessagesConcurrently()
    {
        var app = CreateEchoApp();
        await using var server = await StartAsync(app);

        await Task.WhenAll(
            RunEchoClient(server, "first"),
            RunEchoClient(server, "second"));
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task ParameterRouteIsAvailableInsideWebSocketHandler()
    {
        var app = new App();
        app.Get("/rooms/:room", context => context.WebSocket(async (socket, token) =>
        {
            var room = Encoding.UTF8.GetBytes(context.Param("room"));
            await socket.SendAsync(room, WebSocketMessageType.Text, endOfMessage: true, token);
            await socket.ReceiveAsync(new ArraySegment<byte>(new byte[1]), token);
        }));
        await using var server = await StartAsync(app);
        using var socket = new ClientWebSocket();
        await ConnectAsync(socket, server, "/rooms/general");

        await AssertMessage(socket, Encoding.UTF8.GetBytes("general"), WebSocketMessageType.Text);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task AuthenticationMiddlewareCanRejectBeforeUpgrade()
    {
        var handlerCalled = false;
        var app = new App();
        app.Use(async (context, next) =>
        {
            if (!string.Equals(context.Req.Header("Authorization"), "Bearer secret", StringComparison.Ordinal))
            {
                context.Status(StatusCodes.Status401Unauthorized);
                await context.Text("Unauthorized");
                return;
            }

            await next(context);
        });
        app.Get("/ws", context => context.WebSocket(async (socket, token) =>
        {
            handlerCalled = true;
            await Echo(socket, token);
        }));
        await using var server = await StartAsync(app);

        using (var rejected = new ClientWebSocket())
        {
            rejected.Options.CollectHttpResponseDetails = true;
            await Assert.ThrowsAsync<WebSocketException>(() => ConnectAsync(rejected, server, "/ws"));
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.HttpStatusCode);
            Assert.False(handlerCalled);
        }

        using var accepted = new ClientWebSocket();
        accepted.Options.SetRequestHeader("Authorization", "Bearer secret");
        await ConnectAsync(accepted, server, "/ws");
        var message = Encoding.UTF8.GetBytes("authorized");
        await accepted.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        await AssertMessage(accepted, message, WebSocketMessageType.Text);
        Assert.True(handlerCalled);
        await accepted.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task Http2ExtendedConnectEchoesMessage()
    {
        var app = CreateEchoApp();
        await using var server = await app.StartAsync(new AppOptions
        {
            Port = 0,
            Protocols = Protocols.Http2,
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        });
        using var handler = new SocketsHttpHandler();
        using var client = new HttpClient(handler);
        using var socket = new ClientWebSocket();
        socket.Options.HttpVersion = HttpVersion.Version20;
        socket.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        socket.Options.CollectHttpResponseDetails = true;

        await ConnectAsync(socket, server, "/ws", client);

        Assert.Equal(HttpStatusCode.OK, socket.HttpStatusCode);
        var message = Encoding.UTF8.GetBytes("http2");
        await socket.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        await AssertMessage(socket, message, WebSocketMessageType.Text);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    private static App CreateEchoApp()
    {
        var app = new App();
        app.Get("/ws", context => context.WebSocket(Echo));
        return app;
    }

    private static async ValueTask Echo(System.Net.WebSockets.WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[8 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }

            await socket.SendAsync(
                new ArraySegment<byte>(buffer, 0, result.Count),
                result.MessageType,
                result.EndOfMessage,
                token);
        }
    }

    private static async Task RunEchoClient(Server server, string value)
    {
        using var socket = new ClientWebSocket();
        await ConnectAsync(socket, server, "/ws");
        var message = Encoding.UTF8.GetBytes(value);
        await socket.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        await AssertMessage(socket, message, WebSocketMessageType.Text);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    private static async Task AssertMessage(
        ClientWebSocket socket,
        byte[] expected,
        WebSocketMessageType expectedType)
    {
        var buffer = new byte[8 * 1024];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            Assert.Equal(expectedType, result.MessageType);
            message.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        Assert.Equal(expected, message.ToArray());
    }

    private static Task<Server> StartAsync(App app) => app.StartAsync(new AppOptions
    {
        Port = 0,
        ShutdownTimeout = TimeSpan.FromSeconds(2),
    });

    private static async Task ConnectAsync(
        ClientWebSocket socket,
        Server server,
        string path,
        HttpMessageInvoker? client = null)
    {
        var address = new UriBuilder(server.Addresses[0])
        {
            Scheme = server.Addresses[0].StartsWith("https", StringComparison.Ordinal) ? "wss" : "ws",
            Path = path,
        }.Uri;
        using var timeout = new CancellationTokenSource(OperationTimeout);
        if (client is null)
        {
            await socket.ConnectAsync(address, timeout.Token);
        }
        else
        {
            await socket.ConnectAsync(address, client, timeout.Token);
        }
    }
}
