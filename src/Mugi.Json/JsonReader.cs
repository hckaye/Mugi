using System.Buffers;
using System.Buffers.Text;
using System.Runtime.CompilerServices;
using System.Text;

namespace Mugi.Json;

/// <summary>Reads one complete UTF-8 JSON document from a span.</summary>
public ref struct JsonReader
{
    private const int InlineFrameCount = 64;
    private const int MaximumRetainedFrameCount = 256;

    private readonly ReadOnlySpan<byte> _source;
    private readonly JsonOptions _options;
    private InlineFrames _inlineFrames;
    private ContainerFrame[]? _frames;
    private byte[]? _scratch;
    private int _position;
    private int _depth;
    private bool _framesFromPool;
    private bool _scratchFromPool;
    private bool _rootValueStarted;
    private bool _disposed;

    public JsonReader(ReadOnlySpan<byte> utf8Json, JsonOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        if (utf8Json.Length > options.MaxDocumentByteLength)
        {
            throw new JsonException(
                $"The JSON document exceeds the {options.MaxDocumentByteLength}-byte limit.",
                isInputError: true);
        }

        _source = utf8Json;
        _options = options;
        _inlineFrames = default;
        _frames = null;
        _scratch = null;
        _position = 0;
        _depth = 0;
        _framesFromPool = false;
        _scratchFromPool = false;
        _rootValueStarted = false;
        _disposed = false;
    }

    /// <summary>Consumes a null token if one is next. Returns whether it did.</summary>
    public bool TryReadNull()
    {
        SkipWhitespace();
        if (!IsLiteralAt(_position, "null"u8))
        {
            return false;
        }

        BeginValue();
        _position += 4;
        return true;
    }

    public bool ReadBool()
    {
        SkipWhitespace();
        if (IsLiteralAt(_position, "true"u8))
        {
            BeginValue();
            _position += 4;
            return true;
        }

        if (IsLiteralAt(_position, "false"u8))
        {
            BeginValue();
            _position += 5;
            return false;
        }

        throw Error("Expected a Boolean value");
    }

    public int ReadInt32()
    {
        var token = ReadNumberToken();
        if (!Utf8Parser.TryParse(token, out int value, out var consumed) || consumed != token.Length)
        {
            throw Error("The number is outside the Int32 range");
        }

        return value;
    }

    public long ReadInt64()
    {
        var token = ReadNumberToken();
        if (!Utf8Parser.TryParse(token, out long value, out var consumed) || consumed != token.Length)
        {
            throw Error("The number is outside the Int64 range");
        }

        return value;
    }

    public uint ReadUInt32()
    {
        var token = ReadNumberToken();
        if (!Utf8Parser.TryParse(token, out uint value, out var consumed) || consumed != token.Length)
        {
            throw Error("The number is outside the UInt32 range");
        }

        return value;
    }

    public ulong ReadUInt64()
    {
        var token = ReadNumberToken();
        if (!Utf8Parser.TryParse(token, out ulong value, out var consumed) || consumed != token.Length)
        {
            throw Error("The number is outside the UInt64 range");
        }

        return value;
    }

    public float ReadSingle()
    {
        var token = ReadFloatingPointToken();
        if (TryReadSpecialSingle(token, out var special))
        {
            return special;
        }

        if (!Utf8Parser.TryParse(token, out float value, out var consumed) || consumed != token.Length ||
            (!float.IsFinite(value) && !_options.AllowNonFiniteNumbers))
        {
            throw Error("The number is outside the Single range");
        }

        return value;
    }

    public double ReadDouble()
    {
        var token = ReadFloatingPointToken();
        if (TryReadSpecialDouble(token, out var special))
        {
            return special;
        }

        if (!Utf8Parser.TryParse(token, out double value, out var consumed) || consumed != token.Length ||
            (!double.IsFinite(value) && !_options.AllowNonFiniteNumbers))
        {
            throw Error("The number is outside the Double range");
        }

        return value;
    }

    public decimal ReadDecimal()
    {
        var token = ReadNumberToken();
        if (!Utf8Parser.TryParse(token, out decimal value, out var consumed) || consumed != token.Length)
        {
            throw Error("The number is outside the Decimal range");
        }

        return value;
    }

    public string? ReadString()
    {
        if (TryReadNull())
        {
            return null;
        }

        var bytes = ReadStringBytesValue();
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new JsonException(
                $"Invalid UTF-8 at byte offset {_position}.",
                exception,
                isInputError: true);
        }
    }

    public Guid ReadGuid()
    {
        var bytes = ReadStringBytesValue();
        if (!Utf8Parser.TryParse(bytes, out Guid value, out var consumed, 'D') || consumed != bytes.Length)
        {
            throw Error("Expected a Guid in D format");
        }

        return value;
    }

    public DateTime ReadDateTime()
    {
        var bytes = ReadStringBytesValue();
        if (!Utf8Parser.TryParse(bytes, out DateTime value, out var consumed, 'O') || consumed != bytes.Length)
        {
            throw Error("Expected an ISO 8601 round-trip DateTime");
        }

        return value;
    }

    public DateTimeOffset ReadDateTimeOffset()
    {
        var bytes = ReadStringBytesValue();
        if (!Utf8Parser.TryParse(bytes, out DateTimeOffset value, out var consumed, 'O') || consumed != bytes.Length)
        {
            throw Error("Expected an ISO 8601 round-trip DateTimeOffset");
        }

        return value;
    }

    public void ReadBeginObject()
    {
        SkipWhitespace();
        BeginValue();
        ExpectByte((byte)'{', "Expected the beginning of an object");
        PushContainer(ContainerKind.Object);
    }

    /// <summary>Consumes the object terminator if it is next. Returns whether the object ended.</summary>
    public bool TryReadEndObject() => TryReadEndContainer(ContainerKind.Object, (byte)'}');

    /// <summary>
    /// Reads the next property name and returns unescaped UTF-8 bytes. Unescaped names are slices
    /// of the input; escaped names use reader-owned temporary storage until the next reader call.
    /// </summary>
    public ReadOnlySpan<byte> ReadPropertyName()
    {
        var frame = RequireContainer(ContainerKind.Object);
        if (frame.State != ContainerState.PropertyReady)
        {
            throw Error("Expected an object member separator");
        }

        SkipWhitespace();
        var bytes = ReadStringToken();
        SkipWhitespace();
        ExpectByte((byte)':', "Expected ':' after the property name");
        frame.State = ContainerState.ValueReady;
        SetCurrentFrame(frame);
        return bytes;
    }

    public void ReadBeginArray()
    {
        SkipWhitespace();
        BeginValue();
        ExpectByte((byte)'[', "Expected the beginning of an array");
        PushContainer(ContainerKind.Array);
    }

    /// <summary>Consumes the array terminator if it is next. Returns whether the array ended.</summary>
    public bool TryReadEndArray() => TryReadEndContainer(ContainerKind.Array, (byte)']');

    /// <summary>Skips one complete value of any kind.</summary>
    public void SkipValue()
    {
        SkipWhitespace();
        if (_position >= _source.Length)
        {
            throw Error("Expected a JSON value");
        }

        switch (_source[_position])
        {
            case (byte)'{':
                ReadBeginObject();
                while (!TryReadEndObject())
                {
                    _options.CancellationToken.ThrowIfCancellationRequested();
                    ReadPropertyName();
                    SkipValue();
                }

                break;
            case (byte)'[':
                ReadBeginArray();
                while (!TryReadEndArray())
                {
                    _options.CancellationToken.ThrowIfCancellationRequested();
                    SkipValue();
                }

                break;
            case (byte)'"':
                _ = ReadStringBytesValue();
                break;
            case (byte)'n':
                if (!TryReadNull())
                {
                    throw Error("Expected a null value");
                }

                break;
            case (byte)'t':
            case (byte)'f':
                _ = ReadBool();
                break;
            case (byte)'N':
            case (byte)'I':
                _ = ReadDouble();
                break;
            default:
                _ = ReadNumberToken();
                break;
        }
    }

    /// <summary>Asserts that only whitespace remains after the document.</summary>
    public void ExpectEnd()
    {
        if (_depth != 0)
        {
            throw Error("The JSON document ended before all containers were closed");
        }

        if (!_rootValueStarted)
        {
            throw Error("Expected a JSON value");
        }

        SkipWhitespace();
        if (_position != _source.Length)
        {
            throw Error("Unexpected data after the JSON value");
        }

        Dispose();
    }

    /// <summary>Returns temporary parser buffers. Calling it more than once has no effect.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_scratchFromPool && _scratch is not null)
        {
            ArrayPool<byte>.Shared.Return(_scratch);
        }

        if (_framesFromPool && _frames is not null)
        {
            ArrayPool<ContainerFrame>.Shared.Return(_frames);
        }

        _scratch = null;
        _frames = null;
        _scratchFromPool = false;
        _framesFromPool = false;
        _disposed = true;
    }

    internal JsonTokenKind PeekValueKind()
    {
        SkipWhitespace();
        if (_position >= _source.Length)
        {
            return JsonTokenKind.Invalid;
        }

        return _source[_position] switch
        {
            (byte)'{' => JsonTokenKind.Object,
            (byte)'[' => JsonTokenKind.Array,
            (byte)'"' => JsonTokenKind.String,
            (byte)'t' => JsonTokenKind.True,
            (byte)'f' => JsonTokenKind.False,
            (byte)'n' => JsonTokenKind.Null,
            (byte)'-' => JsonTokenKind.Number,
            >= (byte)'0' and <= (byte)'9' => JsonTokenKind.Number,
            _ => JsonTokenKind.Invalid,
        };
    }

    internal ReadOnlySpan<byte> ReadNumberBytes() => ReadNumberToken();

    private ReadOnlySpan<byte> ReadStringBytesValue()
    {
        SkipWhitespace();
        BeginValue();
        return ReadStringToken();
    }

    private ReadOnlySpan<byte> ReadStringToken()
    {
        ExpectByte((byte)'"', "Expected a JSON string");
        var contentStart = _position;
        var hasEscape = false;

        while (_position < _source.Length)
        {
            if (((_position - contentStart) & 0x3FFF) == 0)
            {
                _options.CancellationToken.ThrowIfCancellationRequested();
            }

            var current = _source[_position];
            if (current == (byte)'"')
            {
                var raw = _source[contentStart.._position];
                _position++;
                if (raw.Length > _options.MaxStringByteLength)
                {
                    throw Error($"The string exceeds the {_options.MaxStringByteLength}-byte limit");
                }

                if (!hasEscape)
                {
                    ValidateUtf8(raw);
                    return raw;
                }

                return Unescape(raw);
            }

            if (current < 0x20)
            {
                throw Error("A JSON string contains an unescaped control character");
            }

            if (current == (byte)'\\')
            {
                hasEscape = true;
                _position++;
                if (_position >= _source.Length)
                {
                    throw Error("The JSON escape sequence is incomplete");
                }

                var escaped = _source[_position];
                if (escaped == (byte)'u')
                {
                    if (_source.Length - _position < 5)
                    {
                        throw Error("The Unicode escape sequence is incomplete");
                    }

                    for (var index = 1; index <= 4; index++)
                    {
                        if (HexValue(_source[_position + index]) < 0)
                        {
                            throw Error("The Unicode escape sequence contains a non-hexadecimal digit");
                        }
                    }

                    _position += 5;
                    continue;
                }

                if (escaped is not ((byte)'"' or (byte)'\\' or (byte)'/' or (byte)'b' or (byte)'f' or
                    (byte)'n' or (byte)'r' or (byte)'t'))
                {
                    throw Error("The JSON escape sequence is invalid");
                }
            }

            _position++;
            if (_position - contentStart > _options.MaxStringByteLength)
            {
                throw Error($"The string exceeds the {_options.MaxStringByteLength}-byte limit");
            }
        }

        throw Error("The JSON string is incomplete");
    }

    private ReadOnlySpan<byte> Unescape(ReadOnlySpan<byte> raw)
    {
        var destination = GetScratch(raw.Length);
        var sourceIndex = 0;
        var destinationIndex = 0;

        while (sourceIndex < raw.Length)
        {
            var slashIndex = raw[sourceIndex..].IndexOf((byte)'\\');
            if (slashIndex < 0)
            {
                raw[sourceIndex..].CopyTo(destination[destinationIndex..]);
                destinationIndex += raw.Length - sourceIndex;
                break;
            }

            var safe = raw.Slice(sourceIndex, slashIndex);
            safe.CopyTo(destination[destinationIndex..]);
            destinationIndex += safe.Length;
            sourceIndex += slashIndex + 1;
            var escaped = raw[sourceIndex++];
            switch (escaped)
            {
                case (byte)'"':
                case (byte)'\\':
                case (byte)'/':
                    destination[destinationIndex++] = escaped;
                    break;
                case (byte)'b':
                    destination[destinationIndex++] = (byte)'\b';
                    break;
                case (byte)'f':
                    destination[destinationIndex++] = (byte)'\f';
                    break;
                case (byte)'n':
                    destination[destinationIndex++] = (byte)'\n';
                    break;
                case (byte)'r':
                    destination[destinationIndex++] = (byte)'\r';
                    break;
                case (byte)'t':
                    destination[destinationIndex++] = (byte)'\t';
                    break;
                case (byte)'u':
                    var high = ReadHex4(raw.Slice(sourceIndex, 4));
                    sourceIndex += 4;
                    if (high is >= 0xD800 and <= 0xDBFF)
                    {
                        if (raw.Length - sourceIndex < 6 || raw[sourceIndex] != (byte)'\\' ||
                            raw[sourceIndex + 1] != (byte)'u')
                        {
                            throw Error("A high surrogate is not followed by a low surrogate");
                        }

                        var low = ReadHex4(raw.Slice(sourceIndex + 2, 4));
                        if (low is < 0xDC00 or > 0xDFFF)
                        {
                            throw Error("A high surrogate is not followed by a low surrogate");
                        }

                        sourceIndex += 6;
                        var scalar = 0x10000 + ((high - 0xD800) << 10) + low - 0xDC00;
                        destinationIndex += new Rune(scalar).EncodeToUtf8(destination[destinationIndex..]);
                    }
                    else
                    {
                        if (high is >= 0xDC00 and <= 0xDFFF)
                        {
                            throw Error("The string contains an isolated low surrogate");
                        }

                        destinationIndex += new Rune(high).EncodeToUtf8(destination[destinationIndex..]);
                    }

                    break;
                default:
                    throw Error("The JSON escape sequence is invalid");
            }
        }

        var result = destination[..destinationIndex];
        ValidateUtf8(result);
        return result;
    }

    private ReadOnlySpan<byte> ReadNumberToken()
    {
        SkipWhitespace();
        _options.CancellationToken.ThrowIfCancellationRequested();
        BeginValue();
        var start = _position;
        var digits = 0;

        if (TryConsume((byte)'-') && _position >= _source.Length)
        {
            throw Error("The JSON number is incomplete");
        }

        if (TryConsume((byte)'0'))
        {
            CountDigit(ref digits);
            if (_position < _source.Length && IsDigit(_source[_position]))
            {
                throw Error("A JSON number cannot contain a leading zero");
            }
        }
        else
        {
            if (_position >= _source.Length || _source[_position] is < (byte)'1' or > (byte)'9')
            {
                throw Error("Expected a JSON number");
            }

            do
            {
                _position++;
                CountDigit(ref digits);
            }
            while (_position < _source.Length && IsDigit(_source[_position]));
        }

        if (TryConsume((byte)'.'))
        {
            if (_position >= _source.Length || !IsDigit(_source[_position]))
            {
                throw Error("A fractional part must contain at least one digit");
            }

            do
            {
                _position++;
                CountDigit(ref digits);
            }
            while (_position < _source.Length && IsDigit(_source[_position]));
        }

        if (_position < _source.Length && _source[_position] is (byte)'e' or (byte)'E')
        {
            _position++;
            if (_position < _source.Length && _source[_position] is (byte)'+' or (byte)'-')
            {
                _position++;
            }

            if (_position >= _source.Length || !IsDigit(_source[_position]))
            {
                throw Error("An exponent must contain at least one digit");
            }

            do
            {
                _position++;
                CountDigit(ref digits);
            }
            while (_position < _source.Length && IsDigit(_source[_position]));
        }

        if (_position < _source.Length && !IsValueDelimiter(_source[_position]))
        {
            throw Error("The JSON number has an invalid trailing character");
        }

        return _source[start.._position];
    }

    private ReadOnlySpan<byte> ReadFloatingPointToken()
    {
        SkipWhitespace();
        if (_options.AllowNonFiniteNumbers)
        {
            if (IsLiteralAt(_position, "NaN"u8))
            {
                BeginValue();
                var result = _source.Slice(_position, 3);
                _position += 3;
                return result;
            }

            if (IsLiteralAt(_position, "Infinity"u8))
            {
                BeginValue();
                var result = _source.Slice(_position, 8);
                _position += 8;
                return result;
            }

            if (IsLiteralAt(_position, "-Infinity"u8))
            {
                BeginValue();
                var result = _source.Slice(_position, 9);
                _position += 9;
                return result;
            }
        }

        return ReadNumberToken();
    }

    private static bool TryReadSpecialSingle(ReadOnlySpan<byte> token, out float value)
    {
        if (token.SequenceEqual("NaN"u8))
        {
            value = float.NaN;
            return true;
        }

        if (token.SequenceEqual("Infinity"u8))
        {
            value = float.PositiveInfinity;
            return true;
        }

        if (token.SequenceEqual("-Infinity"u8))
        {
            value = float.NegativeInfinity;
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryReadSpecialDouble(ReadOnlySpan<byte> token, out double value)
    {
        if (token.SequenceEqual("NaN"u8))
        {
            value = double.NaN;
            return true;
        }

        if (token.SequenceEqual("Infinity"u8))
        {
            value = double.PositiveInfinity;
            return true;
        }

        if (token.SequenceEqual("-Infinity"u8))
        {
            value = double.NegativeInfinity;
            return true;
        }

        value = default;
        return false;
    }

    private bool TryReadEndContainer(ContainerKind kind, byte terminator)
    {
        _options.CancellationToken.ThrowIfCancellationRequested();
        var frame = RequireContainer(kind);
        SkipWhitespace();

        if (frame.State is ContainerState.Initial or ContainerState.AfterValue)
        {
            if (TryConsume(terminator))
            {
                _depth--;
                return true;
            }
        }

        if (frame.State == ContainerState.Initial)
        {
            PrepareNext(ref frame, kind);
            SetCurrentFrame(frame);
            return false;
        }

        if (frame.State != ContainerState.AfterValue)
        {
            throw Error("A container value was not read");
        }

        ExpectByte((byte)',', "Expected ',' or the end of the container");
        SkipWhitespace();
        if (_position < _source.Length && _source[_position] == terminator)
        {
            throw Error("A trailing comma is not permitted");
        }

        PrepareNext(ref frame, kind);
        SetCurrentFrame(frame);
        return false;
    }

    private void PrepareNext(ref ContainerFrame frame, ContainerKind kind)
    {
        if (frame.Count >= _options.MaxCollectionSize)
        {
            throw Error($"The container exceeds the {_options.MaxCollectionSize}-element limit");
        }

        frame.Count++;
        frame.State = kind == ContainerKind.Object ? ContainerState.PropertyReady : ContainerState.ValueReady;
    }

    private void BeginValue()
    {
        if (_depth == 0)
        {
            if (_rootValueStarted)
            {
                throw Error("Only one root JSON value is permitted");
            }

            _rootValueStarted = true;
            return;
        }

        var frame = GetCurrentFrame();
        if (frame.State != ContainerState.ValueReady)
        {
            throw Error("Expected a collection separator before the value");
        }

        frame.State = ContainerState.AfterValue;
        SetCurrentFrame(frame);
    }

    private void PushContainer(ContainerKind kind)
    {
        if (_depth >= _options.MaxDepth)
        {
            throw Error($"The JSON nesting depth exceeds the {_options.MaxDepth}-level limit");
        }

        if (_depth == InlineFrameCount && _frames is null)
        {
            AllocateFrames();
        }

        var frame = new ContainerFrame(kind, ContainerState.Initial);
        if (_frames is null)
        {
            _inlineFrames[_depth] = frame;
        }
        else
        {
            _frames[_depth] = frame;
        }

        _depth++;
    }

    private void AllocateFrames()
    {
        if (_options.MaxDepth <= MaximumRetainedFrameCount)
        {
            _frames = ArrayPool<ContainerFrame>.Shared.Rent(_options.MaxDepth);
            _framesFromPool = true;
        }
        else
        {
            _frames = new ContainerFrame[_options.MaxDepth];
            _framesFromPool = false;
        }

        for (var index = 0; index < InlineFrameCount; index++)
        {
            _frames[index] = _inlineFrames[index];
        }
    }

    private ContainerFrame RequireContainer(ContainerKind kind)
    {
        var frame = _depth == 0 ? default : GetCurrentFrame();
        if (_depth == 0 || frame.Kind != kind)
        {
            throw Error(kind == ContainerKind.Object ? "Expected an open object" : "Expected an open array");
        }

        return frame;
    }

    private ContainerFrame GetCurrentFrame()
    {
        if (_frames is not null)
        {
            return _frames[_depth - 1];
        }

        return _inlineFrames[_depth - 1];
    }

    private void SetCurrentFrame(ContainerFrame frame)
    {
        if (_frames is not null)
        {
            _frames[_depth - 1] = frame;
            return;
        }

        _inlineFrames[_depth - 1] = frame;
    }

    private Span<byte> GetScratch(int requiredLength)
    {
        if (_scratch is not null && _scratch.Length >= requiredLength)
        {
            return _scratch;
        }

        if (_scratchFromPool && _scratch is not null)
        {
            ArrayPool<byte>.Shared.Return(_scratch);
        }

        if (requiredLength <= _options.MaxPooledBufferByteLength)
        {
            _scratch = ArrayPool<byte>.Shared.Rent(requiredLength);
            _scratchFromPool = true;
        }
        else
        {
            _scratch = new byte[requiredLength];
            _scratchFromPool = false;
        }

        return _scratch;
    }

    private void SkipWhitespace()
    {
        var start = _position;
        while (_position < _source.Length && _source[_position] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            if (((_position - start) & 0x3FFF) == 0)
            {
                _options.CancellationToken.ThrowIfCancellationRequested();
            }

            _position++;
        }
    }

    private void ExpectByte(byte expected, string message)
    {
        if (_position >= _source.Length || _source[_position] != expected)
        {
            throw Error(message);
        }

        _position++;
    }

    private bool TryConsume(byte value)
    {
        if (_position >= _source.Length || _source[_position] != value)
        {
            return false;
        }

        _position++;
        return true;
    }

    private bool IsLiteralAt(int position, ReadOnlySpan<byte> literal)
    {
        if (position < 0 || _source.Length - position < literal.Length ||
            !_source.Slice(position, literal.Length).SequenceEqual(literal))
        {
            return false;
        }

        var end = position + literal.Length;
        return end == _source.Length || IsValueDelimiter(_source[end]);
    }

    private void CountDigit(ref int digits)
    {
        digits++;
        if ((digits & 0x3FFF) == 0)
        {
            _options.CancellationToken.ThrowIfCancellationRequested();
        }

        if (digits > _options.MaxNumberDigits)
        {
            throw Error($"The number exceeds the {_options.MaxNumberDigits}-digit limit");
        }
    }

    private static bool IsDigit(byte value) => value is >= (byte)'0' and <= (byte)'9';

    private static bool IsValueDelimiter(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)',' or (byte)']' or (byte)'}';

    private static int ReadHex4(ReadOnlySpan<byte> source) =>
        (HexValue(source[0]) << 12) | (HexValue(source[1]) << 8) |
        (HexValue(source[2]) << 4) | HexValue(source[3]);

    private static int HexValue(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - (byte)'0',
        >= (byte)'a' and <= (byte)'f' => value - (byte)'a' + 10,
        >= (byte)'A' and <= (byte)'F' => value - (byte)'A' + 10,
        _ => -1,
    };

    private void ValidateUtf8(ReadOnlySpan<byte> bytes)
    {
        var remaining = bytes;
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf8(remaining, out _, out var consumed);
            if (status != OperationStatus.Done)
            {
                throw Error("The string contains invalid UTF-8");
            }

            remaining = remaining[consumed..];
        }
    }

    private JsonException Error(string message) =>
        new($"{message} at byte offset {_position}.", isInputError: true);

    private static void ValidateOptions(JsonOptions options)
    {
        if (options.MaxDepth is < 1 or > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxDepth must be from 1 through 1024.");
        }

        if (options.MaxDocumentByteLength < 1 || options.MaxStringByteLength < 0 ||
            options.MaxCollectionSize < 0 || options.MaxNumberDigits < 1 ||
            options.MaxPooledBufferByteLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Json limits must not be negative or zero where a value is required.");
        }
    }

    private enum ContainerKind : byte
    {
        Object,
        Array,
    }

    private enum ContainerState : byte
    {
        Initial,
        PropertyReady,
        ValueReady,
        AfterValue,
    }

    private struct ContainerFrame
    {
        public ContainerFrame(ContainerKind kind, ContainerState state)
        {
            Kind = kind;
            State = state;
            Count = 0;
        }

        public ContainerKind Kind;
        public ContainerState State;
        public int Count;
    }

    [InlineArray(InlineFrameCount)]
    private struct InlineFrames
    {
        private ContainerFrame _element0;
    }
}

internal enum JsonTokenKind
{
    Invalid,
    Object,
    Array,
    String,
    Number,
    True,
    False,
    Null,
}
