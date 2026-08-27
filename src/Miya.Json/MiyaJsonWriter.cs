using System.Buffers;
using System.Buffers.Text;
using System.Runtime.CompilerServices;
using System.Text.Unicode;

namespace Miya.Json;

/// <summary>Writes UTF-8 JSON directly into an <see cref="IBufferWriter{Byte}"/>.</summary>
public ref struct MiyaJsonWriter
{
    private static readonly SearchValues<char> CharactersToEscape = SearchValues.Create(
        "\"\\\u0000\u0001\u0002\u0003\u0004\u0005\u0006\u0007\u0008\u0009\u000A\u000B\u000C\u000D\u000E\u000F" +
        "\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001A\u001B\u001C\u001D\u001E\u001F");

    private readonly IBufferWriter<byte> _destination;
    private readonly MiyaJsonOptions _options;
    private Span<byte> _buffer;
    private int _pendingBytes;
    private int _remainingDocumentBytes;

    public MiyaJsonWriter(IBufferWriter<byte> destination, MiyaJsonOptions options)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        _destination = destination;
        _options = options;
        _buffer = destination.GetSpan();
        _pendingBytes = 0;
        _remainingDocumentBytes = options.MaxDocumentByteLength;
    }

    /// <summary>Writes trusted, pre-encoded JSON fragments such as property names and structural tokens.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteRaw(scoped ReadOnlySpan<byte> utf8)
    {
        EnsureDocumentCapacity(utf8.Length);
        utf8.CopyTo(GetWriteSpan(utf8.Length));
        _pendingBytes += utf8.Length;
    }

    public void WriteNull() => WriteRaw("null"u8);

    public void WriteBool(bool value) => WriteRaw(value ? "true"u8 : "false"u8);

    public void WriteString(string? value)
    {
        if (value is null)
        {
            WriteNull();
            return;
        }

        WriteString(value.AsSpan());
    }

    public void WriteString(scoped ReadOnlySpan<char> value)
    {
        int maximumLength;
        try
        {
            maximumLength = checked((value.Length * 6) + 2);
        }
        catch (OverflowException exception)
        {
            throw new MiyaJsonException("The string is too large to encode as JSON.", exception);
        }

        var destination = GetWriteSpan(maximumLength);
        var written = 0;
        destination[written++] = (byte)'"';
        var remaining = value;
        var charactersUntilCancellationCheck = 0;

        while (!remaining.IsEmpty)
        {
            if (charactersUntilCancellationCheck <= 0)
            {
                _options.CancellationToken.ThrowIfCancellationRequested();
                charactersUntilCancellationCheck = 16 * 1024;
            }

            var escapeIndex = remaining.IndexOfAny(CharactersToEscape);
            var safeLength = escapeIndex < 0 ? remaining.Length : escapeIndex;
            if (safeLength != 0)
            {
                var status = Utf8.FromUtf16(
                    remaining[..safeLength],
                    destination[written..],
                    out var charsRead,
                    out var bytesWritten,
                    replaceInvalidSequences: false,
                    isFinalBlock: true);
                if (status != OperationStatus.Done || charsRead != safeLength)
                {
                    throw new MiyaJsonException("The string contains an invalid UTF-16 surrogate sequence.");
                }

                written += bytesWritten;
                remaining = remaining[safeLength..];
                charactersUntilCancellationCheck -= safeLength;
            }

            if (escapeIndex < 0)
            {
                break;
            }

            var character = remaining[0];
            remaining = remaining[1..];
            charactersUntilCancellationCheck--;
            destination[written++] = (byte)'\\';
            switch (character)
            {
                case '"':
                case '\\':
                    destination[written++] = (byte)character;
                    break;
                case '\b':
                    destination[written++] = (byte)'b';
                    break;
                case '\f':
                    destination[written++] = (byte)'f';
                    break;
                case '\n':
                    destination[written++] = (byte)'n';
                    break;
                case '\r':
                    destination[written++] = (byte)'r';
                    break;
                case '\t':
                    destination[written++] = (byte)'t';
                    break;
                default:
                    destination[written++] = (byte)'u';
                    destination[written++] = (byte)'0';
                    destination[written++] = (byte)'0';
                    destination[written++] = ToHex((byte)(character >> 4));
                    destination[written++] = ToHex((byte)character);
                    break;
            }
        }

        EnsureStringCapacity(written - 1);
        destination[written++] = (byte)'"';
        EnsureDocumentCapacity(written);
        _pendingBytes += written;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteNumber(int value)
    {
        var negative = value < 0;
        var magnitude = negative ? (uint)(-(value + 1)) + 1 : (uint)value;
        var digitCount = CountUInt32Digits(magnitude);
        var written = digitCount + (negative ? 1 : 0);
        EnsureDocumentCapacity(written);
        var destination = GetWriteSpan(written);
        if (negative)
        {
            destination[0] = (byte)'-';
        }

        WriteUInt32Digits(magnitude, destination.Slice(written - digitCount, digitCount));
        _pendingBytes += written;
    }

    public void WriteNumber(long value)
    {
        Span<byte> buffer = stackalloc byte[20];
        var written = FormatInt64(value, buffer);
        WriteRaw(buffer[..written]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteNumber(uint value)
    {
        var written = CountUInt32Digits(value);
        EnsureDocumentCapacity(written);
        var destination = GetWriteSpan(written);
        WriteUInt32Digits(value, destination[..written]);
        _pendingBytes += written;
    }

    public void WriteNumber(ulong value)
    {
        Span<byte> buffer = stackalloc byte[20];
        var written = FormatUInt64(value, buffer);
        WriteRaw(buffer[..written]);
    }

    public void WriteNumber(float value)
    {
        if (!float.IsFinite(value) && !_options.AllowNonFiniteNumbers)
        {
            throw new MiyaJsonException("Non-finite floating-point values are disabled.");
        }

        Span<byte> buffer = stackalloc byte[32];
        if (!Utf8Formatter.TryFormat(value, buffer, out var written))
        {
            throw new MiyaJsonException("The Single value could not be formatted.");
        }

        WriteRaw(buffer[..written]);
    }

    public void WriteNumber(double value)
    {
        if (!double.IsFinite(value) && !_options.AllowNonFiniteNumbers)
        {
            throw new MiyaJsonException("Non-finite floating-point values are disabled.");
        }

        Span<byte> buffer = stackalloc byte[32];
        if (!Utf8Formatter.TryFormat(value, buffer, out var written))
        {
            throw new MiyaJsonException("The Double value could not be formatted.");
        }

        WriteRaw(buffer[..written]);
    }

    public void WriteNumber(decimal value)
    {
        Span<byte> buffer = stackalloc byte[32];
        if (!Utf8Formatter.TryFormat(value, buffer, out var written))
        {
            throw new MiyaJsonException("The Decimal value could not be formatted.");
        }

        WriteRaw(buffer[..written]);
    }

    public void WriteGuid(Guid value)
    {
        Span<byte> buffer = stackalloc byte[38];
        buffer[0] = (byte)'"';
        if (!Utf8Formatter.TryFormat(value, buffer[1..], out var written, new StandardFormat('D')))
        {
            throw new MiyaJsonException("The Guid value could not be formatted.");
        }

        buffer[written + 1] = (byte)'"';
        WriteRaw(buffer[..(written + 2)]);
    }

    public void WriteDateTime(DateTime value)
    {
        Span<byte> buffer = stackalloc byte[40];
        buffer[0] = (byte)'"';
        if (!Utf8Formatter.TryFormat(value, buffer[1..], out var written, new StandardFormat('O')))
        {
            throw new MiyaJsonException("The DateTime value could not be formatted.");
        }

        buffer[written + 1] = (byte)'"';
        WriteRaw(buffer[..(written + 2)]);
    }

    public void WriteDateTimeOffset(DateTimeOffset value)
    {
        Span<byte> buffer = stackalloc byte[40];
        buffer[0] = (byte)'"';
        if (!Utf8Formatter.TryFormat(value, buffer[1..], out var written, new StandardFormat('O')))
        {
            throw new MiyaJsonException("The DateTimeOffset value could not be formatted.");
        }

        buffer[written + 1] = (byte)'"';
        WriteRaw(buffer[..(written + 2)]);
    }

    /// <summary>Completes the writer. <see cref="IBufferWriter{T}"/> has no separate flush operation.</summary>
    public void Flush()
    {
        FlushPendingBytes();
    }

    private static ReadOnlySpan<byte> DigitPairs =>
        "00010203040506070809101112131415161718192021222324252627282930313233343536373839404142434445464748495051525354555657585960616263646566676869707172737475767778798081828384858687888990919293949596979899"u8;

    private static int FormatInt64(long value, Span<byte> destination)
    {
        if (value >= 0)
        {
            return FormatUInt64((ulong)value, destination);
        }

        destination[0] = (byte)'-';
        var magnitude = (ulong)(-(value + 1)) + 1;
        return 1 + FormatUInt64(magnitude, destination[1..]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountUInt32Digits(uint value)
    {
        if (value < 10_000)
        {
            if (value < 100)
            {
                return value < 10 ? 1 : 2;
            }

            return value < 1_000 ? 3 : 4;
        }

        if (value < 100_000_000)
        {
            if (value < 1_000_000)
            {
                return value < 100_000 ? 5 : 6;
            }

            return value < 10_000_000 ? 7 : 8;
        }

        return value < 1_000_000_000 ? 9 : 10;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteUInt32Digits(uint value, Span<byte> destination)
    {
        var position = destination.Length;
        var pairs = DigitPairs;

        while (value >= 100)
        {
            var quotient = value / 100;
            var remainder = (int)(value - (quotient * 100));
            position -= 2;
            var pairIndex = remainder * 2;
            destination[position] = pairs[pairIndex];
            destination[position + 1] = pairs[pairIndex + 1];
            value = quotient;
        }

        if (value < 10)
        {
            destination[--position] = (byte)('0' + value);
        }
        else
        {
            position -= 2;
            var pairIndex = (int)value * 2;
            destination[position] = pairs[pairIndex];
            destination[position + 1] = pairs[pairIndex + 1];
        }
    }

    private static int FormatUInt64(ulong value, Span<byte> destination)
    {
        Span<byte> reversed = stackalloc byte[20];
        var start = reversed.Length;
        var pairs = DigitPairs;

        while (value >= 100)
        {
            var quotient = value / 100;
            var remainder = (int)(value - (quotient * 100));
            start -= 2;
            pairs.Slice(remainder * 2, 2).CopyTo(reversed[start..]);
            value = quotient;
        }

        if (value < 10)
        {
            reversed[--start] = (byte)('0' + value);
        }
        else
        {
            start -= 2;
            pairs.Slice((int)value * 2, 2).CopyTo(reversed[start..]);
        }

        var length = reversed.Length - start;
        reversed[start..].CopyTo(destination);
        return length;
    }

    private static byte ToHex(byte value)
    {
        value &= 0x0F;
        return (byte)(value < 10 ? '0' + value : 'A' + value - 10);
    }

    private void EnsureStringCapacity(int encodedByteLength)
    {
        if (encodedByteLength > _options.MaxStringByteLength)
        {
            throw new MiyaJsonException(
                $"The encoded string exceeds the {_options.MaxStringByteLength}-byte limit.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureDocumentCapacity(int additionalBytes)
    {
        if (additionalBytes > _remainingDocumentBytes)
        {
            throw new MiyaJsonException(
                $"The JSON document exceeds the {_options.MaxDocumentByteLength}-byte limit.");
        }

        _remainingDocumentBytes -= additionalBytes;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Span<byte> GetWriteSpan(int length)
    {
        if (length <= _buffer.Length - _pendingBytes)
        {
            return _buffer[_pendingBytes..];
        }

        FlushPendingBytes();
        _buffer = _destination.GetSpan(length);
        return _buffer;
    }

    private void FlushPendingBytes()
    {
        if (_pendingBytes == 0)
        {
            return;
        }

        _destination.Advance(_pendingBytes);
        _pendingBytes = 0;
        _buffer = default;
    }

    private static void ValidateOptions(MiyaJsonOptions options)
    {
        if (options.MaxDepth is < 1 or > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxDepth must be from 1 through 1024.");
        }

        if (options.MaxDocumentByteLength < 1 || options.MaxStringByteLength < 0 ||
            options.MaxCollectionSize < 0 || options.MaxNumberDigits < 1 ||
            options.MaxPooledBufferByteLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MiyaJson limits must not be negative or zero where a value is required.");
        }
    }
}
