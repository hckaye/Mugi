namespace Miya;

/// <summary>
/// Configures a WebSocket connection accepted by <see cref="Context.WebSocket"/>.
/// </summary>
public sealed class WebSocketOptions
{
    /// <summary>
    /// Gets the supported subprotocols in server preference order.
    /// </summary>
    public IReadOnlyList<string> SubProtocols { get; init; } = [];

    /// <summary>
    /// Gets the interval used to keep an idle WebSocket connection alive.
    /// </summary>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(30);
}
