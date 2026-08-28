using System.Buffers.Text;
using System.IO.Pipelines;
using System.Text;

namespace Miya;

/// <summary>
/// Writes Server-Sent Events to a streaming response. Each <see cref="Send"/> emits fields in
/// this order: <c>event</c>, <c>id</c>, then one <c>data:</c> line per payload line, then a blank line.
/// </summary>
public sealed class SseWriter
{
    private static ReadOnlySpan<byte> DataPrefix => "data: "u8;
    private static ReadOnlySpan<byte> EventPrefix => "event: "u8;
    private static ReadOnlySpan<byte> IdPrefix => "id: "u8;
    private static ReadOnlySpan<byte> RetryPrefix => "retry: "u8;
    private static ReadOnlySpan<byte> CommentPrefix => ": "u8;

    private readonly PipeWriter _writer;
    private readonly CancellationToken _cancellationToken;

    internal SseWriter(PipeWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Writes one event and flushes it. Optional <paramref name="eventName"/> and <paramref name="id"/>
    /// are emitted before data lines. Empty <paramref name="data"/> produces a single <c>data:</c> line.
    /// </summary>
    public ValueTask Send(string data, string? eventName = null, string? id = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (eventName is not null)
        {
            ValidateField(eventName, nameof(eventName));
        }

        if (id is not null)
        {
            ValidateField(id, nameof(id));
        }

        if (eventName is not null)
        {
            WritePrefixedLine(EventPrefix, eventName);
        }

        if (id is not null)
        {
            WritePrefixedLine(IdPrefix, id);
        }

        WritePrefixedLines(DataPrefix, data);
        WriteByte((byte)'\n');
        return FlushAsync();
    }

    /// <summary>
    /// Writes an SSE comment (<c>: </c> prefix) and flushes. Multiline comments are split the same way as data.
    /// </summary>
    public ValueTask Comment(string comment)
    {
        ArgumentNullException.ThrowIfNull(comment);
        WritePrefixedLines(CommentPrefix, comment);
        return FlushAsync();
    }

    /// <summary>
    /// Writes a <c>retry:</c> field with the interval in whole milliseconds and flushes.
    /// </summary>
    public ValueTask Retry(TimeSpan interval)
    {
        var milliseconds = interval.TotalMilliseconds;
        if (milliseconds < 1 || milliseconds > int.MaxValue || milliseconds != Math.Truncate(milliseconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(interval),
                "The retry interval must be a positive integer number of milliseconds.");
        }

        Span<byte> digits = stackalloc byte[10];
        if (!Utf8Formatter.TryFormat((int)milliseconds, digits, out var digitCount))
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        var prefix = RetryPrefix;
        var length = prefix.Length + digitCount + 1;
        var span = _writer.GetSpan(length);
        prefix.CopyTo(span);
        digits[..digitCount].CopyTo(span[prefix.Length..]);
        span[prefix.Length + digitCount] = (byte)'\n';
        _writer.Advance(length);
        return FlushAsync();
    }

    private static void ValidateField(string value, string paramName)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var character = value[i];
            if (character is '\r' or '\n' or '\0')
            {
                throw new ArgumentException("SSE event and id fields must not contain CR, LF, or NUL.", paramName);
            }
        }
    }

    private void WritePrefixedLines(ReadOnlySpan<byte> prefix, string value)
    {
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var character = value[i];
            if (character == '\r')
            {
                WritePrefixedLine(prefix, value.AsSpan(start, i - start));
                if (i + 1 < value.Length && value[i + 1] == '\n')
                {
                    i++;
                }

                start = i + 1;
            }
            else if (character == '\n')
            {
                WritePrefixedLine(prefix, value.AsSpan(start, i - start));
                start = i + 1;
            }
        }

        WritePrefixedLine(prefix, value.AsSpan(start));
    }

    private void WritePrefixedLine(ReadOnlySpan<byte> prefix, ReadOnlySpan<char> line)
    {
        var byteCount = Encoding.UTF8.GetByteCount(line);
        var length = checked(prefix.Length + byteCount + 1);
        var span = _writer.GetSpan(length);
        prefix.CopyTo(span);
        var written = Encoding.UTF8.GetBytes(line, span[prefix.Length..]);
        span[prefix.Length + written] = (byte)'\n';
        _writer.Advance(prefix.Length + written + 1);
    }

    private void WriteByte(byte value)
    {
        var span = _writer.GetSpan(1);
        span[0] = value;
        _writer.Advance(1);
    }

    private async ValueTask FlushAsync()
    {
        var flush = await _writer.FlushAsync(_cancellationToken).ConfigureAwait(false);
        if (flush.IsCanceled)
        {
            throw new OperationCanceledException(_cancellationToken);
        }
    }
}
