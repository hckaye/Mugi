using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Miya;

public partial class Context
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    /// <summary>
    /// Accepts a WebSocket request and runs <paramref name="handler"/> until the connection ends.
    /// HTTP/1.1 upgrade requests and HTTP/2 extended CONNECT requests are supported. If the handler
    /// throws after the handshake, Miya sends close status 1011 when possible and aborts the connection.
    /// A configured subprotocol list may decline negotiation when the client offers no matching value.
    /// </summary>
    /// <param name="handler">The callback that owns the accepted socket for the duration of the request.</param>
    /// <param name="options">The WebSocket connection options.</param>
    public async ValueTask WebSocket(
        Func<System.Net.WebSockets.WebSocket, CancellationToken, ValueTask> handler,
        WebSocketOptions? options = null)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureBodyMutable();

        IReadOnlyList<string> supportedProtocols;
        TimeSpan keepAliveInterval;
        if (options is null)
        {
            supportedProtocols = Array.Empty<string>();
            keepAliveInterval = TimeSpan.FromSeconds(30);
        }
        else
        {
            ArgumentNullException.ThrowIfNull(options.SubProtocols);
            supportedProtocols = options.SubProtocols;
            keepAliveInterval = options.KeepAliveInterval;
        }

        ValidateWebSocketOptions(supportedProtocols, keepAliveInterval);

        var request = Features.Get<IHttpRequestFeature>()
            ?? throw new InvalidOperationException("The request feature is unavailable.");
        var connectFeature = Features.Get<IHttpExtendedConnectFeature>();
        var isExtendedConnect = connectFeature?.IsExtendedConnect == true;
        if (isExtendedConnect)
        {
            if (!string.Equals(request.Method, "CONNECT", StringComparison.Ordinal)
                || !string.Equals(connectFeature!.Protocol, "websocket", StringComparison.OrdinalIgnoreCase))
            {
                await WriteUpgradeRequired(isHttp2: true, includeVersion: false).ConfigureAwait(false);
                return;
            }

            if (!ContainsHeaderToken(request.Headers, "Sec-WebSocket-Version", "13", StringComparison.Ordinal))
            {
                await WriteUpgradeRequired(isHttp2: true, includeVersion: true).ConfigureAwait(false);
                return;
            }
        }
        else
        {
            if (!string.Equals(request.Method, "GET", StringComparison.Ordinal)
                || !ContainsHeaderToken(request.Headers, "Upgrade", "websocket", StringComparison.OrdinalIgnoreCase)
                || !ContainsHeaderToken(request.Headers, "Connection", "Upgrade", StringComparison.OrdinalIgnoreCase))
            {
                await WriteUpgradeRequired(isHttp2: false, includeVersion: false).ConfigureAwait(false);
                return;
            }

            if (!ContainsHeaderToken(request.Headers, "Sec-WebSocket-Version", "13", StringComparison.Ordinal))
            {
                await WriteUpgradeRequired(isHttp2: false, includeVersion: true).ConfigureAwait(false);
                return;
            }
        }

        string? requestKey = null;
        IHttpUpgradeFeature? upgradeFeature = null;
        if (!isExtendedConnect)
        {
            if (!TryGetWebSocketKey(request.Headers, out requestKey))
            {
                await WriteBadRequest().ConfigureAwait(false);
                return;
            }

            upgradeFeature = Features.Get<IHttpUpgradeFeature>();
            if (upgradeFeature?.IsUpgradableRequest != true)
            {
                await WriteUpgradeRequired(isHttp2: false, includeVersion: false).ConfigureAwait(false);
                return;
            }
        }

        var selectedProtocol = SelectSubProtocol(request.Headers, supportedProtocols);
        var timeoutControlled = ClaimStreamingResponse();
        try
        {
            PrepareWebSocketResponse(isExtendedConnect, requestKey, selectedProtocol);
        }
        finally
        {
            CompleteStreamingResponseClaim(timeoutControlled);
        }

        Stream stream;
        if (isExtendedConnect)
        {
            stream = await connectFeature!.AcceptAsync().ConfigureAwait(false);
        }
        else
        {
            stream = await upgradeFeature!.UpgradeAsync().ConfigureAwait(false);
            _statusCode = StatusCodes.Status101SwitchingProtocols;
        }

        if (Volatile.Read(ref _preventPooling) != 0)
        {
            stream.Dispose();
            return;
        }

        _responseState = ResponseState.WebSocketUpgraded;
        using var socket = System.Net.WebSockets.WebSocket.CreateFromStream(
            stream,
            new WebSocketCreationOptions
            {
                IsServer = true,
                SubProtocol = selectedProtocol,
                KeepAliveInterval = keepAliveInterval,
            });

        try
        {
            await handler(socket, Aborted).ConfigureAwait(false);
        }
        catch
        {
            await TryCloseWebSocket(socket, WebSocketCloseStatus.InternalServerError, waitForPeer: false)
                .ConfigureAwait(false);
            AbortResponse();
            return;
        }

        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await TryCloseWebSocket(socket, WebSocketCloseStatus.NormalClosure, waitForPeer: true)
                .ConfigureAwait(false);
        }
    }

    private void PrepareWebSocketResponse(
        bool isExtendedConnect,
        string? requestKey,
        string? selectedProtocol)
    {
        _buffer.Clear();
        _suppressedBodyLength = 0;
        _headers.Remove("Content-Type");
        _headers.Remove("Content-Length");
        _headers.Remove("Transfer-Encoding");
        _headers.Remove("Connection");
        _statusCode = StatusCodes.Status200OK;

        if (!isExtendedConnect)
        {
            SetFrameworkHeader("Upgrade", "websocket");
            SetFrameworkHeader("Sec-WebSocket-Accept", CreateWebSocketAccept(requestKey!));
        }

        if (selectedProtocol is not null)
        {
            SetFrameworkHeader("Sec-WebSocket-Protocol", selectedProtocol);
        }

        ApplyResponseHead(contentLength: null);
    }

    private async ValueTask TryCloseWebSocket(
        System.Net.WebSockets.WebSocket socket,
        WebSocketCloseStatus status,
        bool waitForPeer)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Aborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            if (waitForPeer)
            {
                await socket.CloseAsync(
                        status,
                        status == WebSocketCloseStatus.NormalClosure ? null : "Handler failed.",
                        timeout.Token)
                    .ConfigureAwait(false);
            }
            else if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseOutputAsync(status, "Handler failed.", timeout.Token).ConfigureAwait(false);
            }
        }
        catch
        {
        }
    }

    private async ValueTask WriteUpgradeRequired(bool isHttp2, bool includeVersion)
    {
        Status(StatusCodes.Status426UpgradeRequired);
        if (!isHttp2)
        {
            Header("Upgrade", "websocket");
        }

        if (includeVersion)
        {
            Header("Sec-WebSocket-Version", "13");
        }

        await Text("Upgrade Required").ConfigureAwait(false);
    }

    private async ValueTask WriteBadRequest()
    {
        Status(StatusCodes.Status400BadRequest);
        await Text("Bad Request").ConfigureAwait(false);
    }

    private static bool ContainsHeaderToken(
        IHeaderDictionary headers,
        string name,
        string expected,
        StringComparison comparison)
    {
        if (!headers.TryGetValue(name, out var values))
        {
            return false;
        }

        for (var valueIndex = 0; valueIndex < values.Count; valueIndex++)
        {
            var value = values[valueIndex];
            if (value is null)
            {
                continue;
            }

            var remaining = value.AsSpan();
            while (true)
            {
                var comma = remaining.IndexOf(',');
                var token = TrimOws(comma < 0 ? remaining : remaining[..comma]);
                if (token.Equals(expected, comparison))
                {
                    return true;
                }

                if (comma < 0)
                {
                    break;
                }

                remaining = remaining[(comma + 1)..];
            }
        }

        return false;
    }

    private static bool TryGetWebSocketKey(IHeaderDictionary headers, out string? key)
    {
        key = null;
        if (!headers.TryGetValue("Sec-WebSocket-Key", out var values) || values.Count != 1)
        {
            return false;
        }

        var value = values[0];
        if (value is null)
        {
            return false;
        }

        var span = TrimOws(value.AsSpan());
        if (span.Length != 24)
        {
            return false;
        }

        Span<byte> decoded = stackalloc byte[16];
        if (!Convert.TryFromBase64Chars(span, decoded, out var written) || written != decoded.Length)
        {
            return false;
        }

        key = span.Length == value.Length ? value : span.ToString();
        return true;
    }

    private static string CreateWebSocketAccept(string key)
    {
        Span<byte> source = stackalloc byte[60];
        Encoding.ASCII.GetBytes(key, source);
        Encoding.ASCII.GetBytes(WebSocketGuid, source[24..]);
        Span<byte> digest = stackalloc byte[20];
        SHA1.HashData(source, digest);
        return Convert.ToBase64String(digest);
    }

    private static string? SelectSubProtocol(
        IHeaderDictionary headers,
        IReadOnlyList<string> supportedProtocols)
    {
        if (supportedProtocols.Count == 0
            || !headers.TryGetValue("Sec-WebSocket-Protocol", out var requestedProtocols))
        {
            return null;
        }

        for (var supportedIndex = 0; supportedIndex < supportedProtocols.Count; supportedIndex++)
        {
            var supported = supportedProtocols[supportedIndex];
            for (var valueIndex = 0; valueIndex < requestedProtocols.Count; valueIndex++)
            {
                var value = requestedProtocols[valueIndex];
                if (value is null)
                {
                    continue;
                }

                var remaining = value.AsSpan();
                while (true)
                {
                    var comma = remaining.IndexOf(',');
                    var requested = TrimOws(comma < 0 ? remaining : remaining[..comma]);
                    if (requested.Equals(supported, StringComparison.Ordinal))
                    {
                        return supported;
                    }

                    if (comma < 0)
                    {
                        break;
                    }

                    remaining = remaining[(comma + 1)..];
                }
            }
        }

        return null;
    }

    private static void ValidateWebSocketOptions(
        IReadOnlyList<string> supportedProtocols,
        TimeSpan keepAliveInterval)
    {
        if (keepAliveInterval < TimeSpan.Zero
            && keepAliveInterval != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                "options",
                "The WebSocket keep-alive interval must be non-negative or infinite.");
        }

        for (var index = 0; index < supportedProtocols.Count; index++)
        {
            var protocol = supportedProtocols[index];
            if (!IsWebSocketToken(protocol))
            {
                throw new ArgumentException(
                    "WebSocket subprotocols must be non-empty HTTP tokens.",
                    "options");
            }
        }
    }

    private static bool IsWebSocketToken(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is < '!' or > '~'
                || character is '(' or ')' or '<' or '>' or '@' or ',' or ';' or ':' or '\\'
                    or '"' or '/' or '[' or ']' or '?' or '=' or '{' or '}')
            {
                return false;
            }
        }

        return true;
    }

    private static ReadOnlySpan<char> TrimOws(ReadOnlySpan<char> value)
    {
        var start = 0;
        while (start < value.Length && value[start] is ' ' or '\t')
        {
            start++;
        }

        var end = value.Length;
        while (end > start && value[end - 1] is ' ' or '\t')
        {
            end--;
        }

        return value[start..end];
    }
}
