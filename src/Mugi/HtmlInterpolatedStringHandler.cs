using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Mugi;

/// <summary>
/// Builds a UTF-8 HTML response, escaping interpolated values and writing literals as-is.
/// </summary>
[InterpolatedStringHandler]
public ref struct HtmlInterpolatedStringHandler
{
    private static readonly SearchValues<char> SpecialCharacters = SearchValues.Create("&<>\"'");

    private byte[]? _buffer;
    private int _written;
    private bool _consumed;

    /// <summary>
    /// Creates a handler used by <see cref="Context.Html(ref HtmlInterpolatedStringHandler)"/>.
    /// The compiler supplies the literal length, hole count, and receiver.
    /// </summary>
    public HtmlInterpolatedStringHandler(int literalLength, int formattedCount, Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfNegative(literalLength);
        ArgumentOutOfRangeException.ThrowIfNegative(formattedCount);
        context.EnsureActive();
        _buffer = null;
        _written = 0;
        _consumed = false;
    }

    /// <summary>
    /// Appends an interpolated-string literal without escaping.
    /// </summary>
    public void AppendLiteral(string s)
    {
        ThrowIfConsumed();
        ArgumentNullException.ThrowIfNull(s);
        WriteUnescaped(s.AsSpan());
    }

    /// <summary>
    /// Appends a string value after HTML-escaping <c>&amp; &lt; &gt; &quot; '</c>. A null value writes nothing.
    /// </summary>
    public void AppendFormatted(string? value)
    {
        ThrowIfConsumed();
        if (value is null)
        {
            return;
        }

        AppendEscaped(value.AsSpan());
    }

    /// <summary>
    /// Appends markup without escaping.
    /// </summary>
    public void AppendFormatted(RawHtml value)
    {
        ThrowIfConsumed();
        var html = value.Value;
        if (html is null)
        {
            return;
        }

        WriteUnescaped(html.AsSpan());
    }

    /// <summary>
    /// Formats <paramref name="value"/> with <see cref="CultureInfo.InvariantCulture"/> and HTML-escapes the result.
    /// </summary>
    public void AppendFormatted<T>(T value)
        where T : ISpanFormattable
    {
        AppendFormatted(value, format: null);
    }

    /// <summary>
    /// Formats <paramref name="value"/> with the given format and <see cref="CultureInfo.InvariantCulture"/>,
    /// then HTML-escapes the result.
    /// </summary>
    public void AppendFormatted<T>(T value, string? format)
        where T : ISpanFormattable
    {
        ThrowIfConsumed();
        Span<char> stack = stackalloc char[256];
        if (value.TryFormat(stack, out var charsWritten, format, CultureInfo.InvariantCulture))
        {
            AppendEscaped(stack[..charsWritten]);
            return;
        }

        AppendFormattedSlow(value, format);
    }

    internal ReadOnlyMemory<byte> WrittenMemory => _buffer is null
        ? ReadOnlyMemory<byte>.Empty
        : _buffer.AsMemory(0, _written);

    internal void Consume()
    {
        ThrowIfConsumed();
        _consumed = true;
    }

    internal void Release()
    {
        var buffer = _buffer;
        _buffer = null;
        _written = 0;
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void AppendFormattedSlow<T>(T value, string? format)
        where T : ISpanFormattable
    {
        var rented = ArrayPool<char>.Shared.Rent(512);
        try
        {
            while (true)
            {
                if (value.TryFormat(rented, out var charsWritten, format, CultureInfo.InvariantCulture))
                {
                    AppendEscaped(rented.AsSpan(0, charsWritten));
                    return;
                }

                var next = ArrayPool<char>.Shared.Rent(checked(rented.Length * 2));
                ArrayPool<char>.Shared.Return(rented);
                rented = next;
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    private void AppendEscaped(scoped ReadOnlySpan<char> value)
    {
        while (!value.IsEmpty)
        {
            var index = value.IndexOfAny(SpecialCharacters);
            if (index < 0)
            {
                WriteUnescaped(value);
                return;
            }

            if (index > 0)
            {
                WriteUnescaped(value[..index]);
            }

            WriteSpecial(value[index]);
            value = value[(index + 1)..];
        }
    }

    private void WriteUnescaped(scoped ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return;
        }

        if (Ascii.IsValid(value))
        {
            var destination = GetSpan(value.Length);
            Ascii.FromUtf16(value, destination, out var written);
            Advance(written);
            return;
        }

        var maxByteCount = Encoding.UTF8.GetMaxByteCount(value.Length);
        var utf8 = GetSpan(maxByteCount);
        Advance(Encoding.UTF8.GetBytes(value, utf8));
    }

    private void WriteSpecial(char character)
    {
        ReadOnlySpan<byte> escape = character switch
        {
            '&' => "&amp;"u8,
            '<' => "&lt;"u8,
            '>' => "&gt;"u8,
            '"' => "&quot;"u8,
            _ => "&#39;"u8,
        };

        var destination = GetSpan(escape.Length);
        escape.CopyTo(destination);
        Advance(escape.Length);
    }

    private Span<byte> GetSpan(int sizeHint)
    {
        EnsureCapacity(sizeHint);
        return _buffer!.AsSpan(_written);
    }

    private void Advance(int count)
    {
        if (count < 0 || _buffer is null || count > _buffer.Length - _written)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        _written += count;
    }

    private void EnsureCapacity(int sizeHint)
    {
        if (sizeHint < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeHint));
        }

        if (sizeHint == 0)
        {
            sizeHint = 1;
        }

        var required = checked(_written + sizeHint);
        if (_buffer is not null && required <= _buffer.Length)
        {
            return;
        }

        var newSize = Math.Max(required, _buffer is null ? 256 : checked(_buffer.Length * 2));
        var replacement = ArrayPool<byte>.Shared.Rent(newSize);
        if (_buffer is not null)
        {
            _buffer.AsSpan(0, _written).CopyTo(replacement);
            ArrayPool<byte>.Shared.Return(_buffer);
        }

        _buffer = replacement;
    }

    private void ThrowIfConsumed()
    {
        if (_consumed)
        {
            throw new InvalidOperationException("The HTML interpolated string handler has already been used.");
        }
    }
}
