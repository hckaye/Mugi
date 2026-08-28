namespace Mugi;

public partial class Context
{
    /// <summary>
    /// Starts a <c>text/event-stream</c> response and writes events through <see cref="SseWriter"/>.
    /// Sets <c>Content-Type: text/event-stream</c>, <c>Cache-Control: no-cache</c>, and
    /// <c>X-Accel-Buffering: no</c>, then delegates to <see cref="Stream"/>. Each event is written
    /// as <c>event</c>, then <c>id</c>, then <c>data</c> lines, then a blank line.
    /// </summary>
    public ValueTask EventStream(Func<SseWriter, CancellationToken, ValueTask> write)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(write);
        Header("Cache-Control", "no-cache");
        Header("X-Accel-Buffering", "no");
        return Stream("text/event-stream", (writer, token) => write(new SseWriter(writer, token), token));
    }
}
