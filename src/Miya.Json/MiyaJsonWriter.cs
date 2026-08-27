using System.Buffers;
using System.Buffers.Text;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Unicode;

namespace Miya.Json;

/// <summary>Writes UTF-8 JSON directly into an <see cref="IBufferWriter{Byte}"/>.</summary>
public ref struct MiyaJsonWriter
{
    private const int CancellationCheckByteInterval = 64 * 1024;
    private const int CancellationCheckDepthInterval = 64;

    private static readonly SearchValues<char> CharactersToEscape = SearchValues.Create(
        "\"\\\u0000\u0001\u0002\u0003\u0004\u0005\u0006\u0007\u0008\u0009\u000A\u000B\u000C\u000D\u000E\u000F" +
        "\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001A\u001B\u001C\u001D\u001E\u001F");
    private static readonly SearchValues<byte> Utf8BytesToEscape = SearchValues.Create(
        "\"\\\u0000\u0001\u0002\u0003\u0004\u0005\u0006\u0007\u0008\u0009\u000A\u000B\u000C\u000D\u000E\u000F\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001A\u001B\u001C\u001D\u001E\u001F"u8);

    private readonly IBufferWriter<byte> _destination;
    private readonly MiyaJsonOptions _options;
    private readonly CancellationToken _cancellationToken;
    private readonly int _maxDepth;
    private readonly int _maxCollectionSize;
    private readonly int _maxDocumentByteLength;
    private readonly int _maxStringByteLength;
    private Span<byte> _buffer;
    private int _pendingBytes;
    private int _remainingDocumentBytes;
    private int _bytesUntilCancellationCheck;
    private int _depth;

    public MiyaJsonWriter(IBufferWriter<byte> destination, MiyaJsonOptions options)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(options);
        if (!ReferenceEquals(options, MiyaJsonOptions.Default))
        {
            ValidateOptions(options);
        }

        _destination = destination;
        _options = options;
        _cancellationToken = options.CancellationToken;
        _maxDepth = options.MaxDepth;
        _maxCollectionSize = options.MaxCollectionSize;
        _maxDocumentByteLength = options.MaxDocumentByteLength;
        _maxStringByteLength = options.MaxStringByteLength;
        _buffer = destination.GetSpan();
        _pendingBytes = 0;
        _remainingDocumentBytes = _maxDocumentByteLength;
        _bytesUntilCancellationCheck = CancellationCheckByteInterval;
        _depth = 0;
    }

    /// <summary>
    /// Enters an object or array and validates its nesting depth and element count.
    /// Generated codecs call this before writing every container.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnterContainer(int elementCount)
    {
        if (elementCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(elementCount));
        }

        if (_depth >= _maxDepth)
        {
            throw new MiyaJsonException(
                $"The JSON nesting depth exceeds the {_maxDepth}-level limit.");
        }

        if (elementCount > _maxCollectionSize)
        {
            throw new MiyaJsonException(
                $"The container exceeds the {_maxCollectionSize}-element limit.");
        }

        if ((_depth & (CancellationCheckDepthInterval - 1)) == CancellationCheckDepthInterval - 1)
        {
            _cancellationToken.ThrowIfCancellationRequested();
        }

        _depth++;
    }

    /// <summary>Leaves an object or array previously entered with <see cref="EnterContainer"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ExitContainer()
    {
        if (_depth == 0)
        {
            throw new MiyaJsonException("No JSON container is currently open.");
        }

        _depth--;
    }

    /// <summary>Checks the configured cancellation token during long generated collection loops.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ThrowIfCancellationRequested() => _cancellationToken.ThrowIfCancellationRequested();

    /// <summary>Writes trusted, pre-encoded JSON fragments such as property names and structural tokens.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteRaw(scoped ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length == 1)
        {
            WriteByte(utf8[0]);
            return;
        }

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
        const int maximumChunkCharacters = 2048;
        EnsureStringCapacity(value.Length);
        WriteByte((byte)'"');
        if (value.Length > maximumChunkCharacters)
        {
            var destination = GetWriteSpan(value.Length);
            var asciiStatus = Ascii.FromUtf16(value, destination, out var bytesWritten);
            if (asciiStatus == OperationStatus.Done &&
                destination[..bytesWritten].IndexOfAny(Utf8BytesToEscape) < 0)
            {
                EnsureStringCapacity(bytesWritten);
                EnsureDocumentCapacity(bytesWritten);
                _pendingBytes += bytesWritten;
                WriteByte((byte)'"');
                return;
            }

            if (asciiStatus != OperationStatus.Done && value.IndexOfAny(CharactersToEscape) < 0)
            {
                int maximumEncodedLength;
                try
                {
                    maximumEncodedLength = checked(value.Length * 3);
                }
                catch (OverflowException exception)
                {
                    throw new MiyaJsonException("The string is too large to encode as UTF-8.", exception);
                }

                destination = GetWriteSpan(maximumEncodedLength);
                var utf8Status = Utf8.FromUtf16(
                    value,
                    destination,
                    out var charsRead,
                    out bytesWritten,
                    replaceInvalidSequences: false,
                    isFinalBlock: true);
                if (utf8Status != OperationStatus.Done || charsRead != value.Length)
                {
                    throw new MiyaJsonException("The string contains an invalid UTF-16 surrogate sequence.");
                }

                EnsureStringCapacity(bytesWritten);
                EnsureDocumentCapacity(bytesWritten);
                _pendingBytes += bytesWritten;
                WriteByte((byte)'"');
                return;
            }
        }

        var remaining = value;
        var totalEncodedLength = 0;
        var charactersUntilCancellationCheck = 0;

        while (!remaining.IsEmpty)
        {
            if (charactersUntilCancellationCheck <= 0)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                charactersUntilCancellationCheck = 16 * 1024;
            }

            var chunkLength = Math.Min(remaining.Length, maximumChunkCharacters);
            if (chunkLength < remaining.Length && char.IsHighSurrogate(remaining[chunkLength - 1]))
            {
                chunkLength++;
            }

            var chunk = remaining[..chunkLength];
            int maximumEncodedLength;
            try
            {
                maximumEncodedLength = checked(chunkLength * 6);
            }
            catch (OverflowException exception)
            {
                throw new MiyaJsonException("The string is too large to encode as JSON.", exception);
            }

            var destination = GetWriteSpan(maximumEncodedLength);
            var written = EncodeStringChunk(chunk, destination);
            if (written > _maxStringByteLength - totalEncodedLength)
            {
                throw new MiyaJsonException(
                    $"The encoded string exceeds the {_maxStringByteLength}-byte limit.");
            }

            totalEncodedLength += written;
            EnsureDocumentCapacity(written);
            _pendingBytes += written;
            remaining = remaining[chunkLength..];
            charactersUntilCancellationCheck -= chunkLength;
        }

        WriteByte((byte)'"');
    }

    private static int EncodeStringChunk(ReadOnlySpan<char> value, Span<byte> destination)
    {
        if (value.IndexOfAny(CharactersToEscape) >= 0)
        {
            return EncodeEscapedStringChunk(value, destination);
        }

        if (value.Length <= 32)
        {
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (character > 0x7F)
                {
                    break;
                }

                destination[index] = (byte)character;
                if (index == value.Length - 1)
                {
                    return value.Length;
                }
            }
        }

        var status = Utf8.FromUtf16(
            value,
            destination,
            out var charsRead,
            out var bytesWritten,
            replaceInvalidSequences: false,
            isFinalBlock: true);
        if (status != OperationStatus.Done || charsRead != value.Length)
        {
            throw new MiyaJsonException("The string contains an invalid UTF-16 surrogate sequence.");
        }

        return bytesWritten;
    }

    private static int EncodeEscapedStringChunk(ReadOnlySpan<char> value, Span<byte> destination)
    {
        Span<byte> utf8 = stackalloc byte[value.Length * 3];
        var status = Utf8.FromUtf16(
            value,
            utf8,
            out var charsRead,
            out var bytesWritten,
            replaceInvalidSequences: false,
            isFinalBlock: true);
        if (status != OperationStatus.Done || charsRead != value.Length)
        {
            throw new MiyaJsonException("The string contains an invalid UTF-16 surrogate sequence.");
        }

        var written = 0;
        for (var index = 0; index < bytesWritten; index++)
        {
            var character = utf8[index];
            if (character >= 0x20 && character != (byte)'"' && character != (byte)'\\')
            {
                destination[written++] = character;
                continue;
            }

            destination[written++] = (byte)'\\';
            switch (character)
            {
                case (byte)'"':
                case (byte)'\\':
                    destination[written++] = (byte)character;
                    break;
                case (byte)'\b':
                    destination[written++] = (byte)'b';
                    break;
                case (byte)'\f':
                    destination[written++] = (byte)'f';
                    break;
                case (byte)'\n':
                    destination[written++] = (byte)'n';
                    break;
                case (byte)'\r':
                    destination[written++] = (byte)'r';
                    break;
                case (byte)'\t':
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

        return written;
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteNumber(long value)
    {
        var negative = value < 0;
        var magnitude = negative ? (ulong)(-(value + 1)) + 1 : (ulong)value;
        var digitCount = CountUInt64Digits(magnitude);
        var written = digitCount + (negative ? 1 : 0);
        EnsureDocumentCapacity(written);
        var destination = GetWriteSpan(written);
        if (negative)
        {
            destination[0] = (byte)'-';
        }

        WriteUInt64Digits(magnitude, destination.Slice(written - digitCount, digitCount));
        _pendingBytes += written;
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteNumber(ulong value)
    {
        var written = CountUInt64Digits(value);
        EnsureDocumentCapacity(written);
        var destination = GetWriteSpan(written);
        WriteUInt64Digits(value, destination[..written]);
        _pendingBytes += written;
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

    private static ReadOnlySpan<ulong> UInt64PowersOfTen =>
    [
        1UL,
        10UL,
        100UL,
        1_000UL,
        10_000UL,
        100_000UL,
        1_000_000UL,
        10_000_000UL,
        100_000_000UL,
        1_000_000_000UL,
        10_000_000_000UL,
        100_000_000_000UL,
        1_000_000_000_000UL,
        10_000_000_000_000UL,
        100_000_000_000_000UL,
        1_000_000_000_000_000UL,
        10_000_000_000_000_000UL,
        100_000_000_000_000_000UL,
        1_000_000_000_000_000_000UL,
        10_000_000_000_000_000_000UL,
    ];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountUInt64Digits(ulong value)
    {
        var digitCount = ((BitOperations.Log2(value | 1) * 1233) >> 12) + 1;
        if (digitCount < 20 && value >= UInt64PowersOfTen[digitCount])
        {
            digitCount++;
        }

        return digitCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteUInt64Digits(ulong value, Span<byte> destination)
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

    private static byte ToHex(byte value)
    {
        value &= 0x0F;
        return (byte)(value < 10 ? '0' + value : 'A' + value - 10);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteByte(byte value)
    {
        EnsureDocumentCapacity(1);
        GetWriteSpan(1)[0] = value;
        _pendingBytes++;
    }

    private void EnsureStringCapacity(int encodedByteLength)
    {
        if (encodedByteLength > _maxStringByteLength)
        {
            throw new MiyaJsonException(
                $"The encoded string exceeds the {_maxStringByteLength}-byte limit.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureDocumentCapacity(int additionalBytes)
    {
        if (additionalBytes > _remainingDocumentBytes)
        {
            throw new MiyaJsonException(
                $"The JSON document exceeds the {_maxDocumentByteLength}-byte limit.");
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

        if (_pendingBytes >= _bytesUntilCancellationCheck)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var remainder = _pendingBytes % CancellationCheckByteInterval;
            _bytesUntilCancellationCheck = remainder == 0
                ? CancellationCheckByteInterval
                : CancellationCheckByteInterval - remainder;
        }
        else
        {
            _bytesUntilCancellationCheck -= _pendingBytes;
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
