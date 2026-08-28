using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.ExceptionServices;
using System.Text;

namespace Mugi;

/// <summary>
/// Reads the parts of a multipart form request in request order.
/// </summary>
public sealed class MultipartReader : IAsyncDisposable
{
    private const int MaximumHeaders = 16;
    private const int MaximumHeaderBytes = 16 * 1024;
    private const int MaximumBoundaryPaddingBytes = 16 * 1024;
    private const int OutputChunkBytes = 16 * 1024;
    private static readonly byte[] CrLf = "\r\n"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly PipeReader _reader;
    private readonly byte[] _initialBoundary;
    private readonly byte[] _partBoundary;
    private readonly int _maximumParts;
    private readonly CancellationToken _aborted;
    private MultipartPartBodyReader? _activeBody;
    private bool _started;
    private bool _ended;
    private bool _epilogueDrained;
    private bool _disposed;
    private int _partCount;

    internal MultipartReader(
        PipeReader reader,
        string boundary,
        int maximumParts,
        CancellationToken aborted)
    {
        _reader = reader;
        _initialBoundary = Encoding.ASCII.GetBytes(string.Concat("--", boundary));
        _partBoundary = Encoding.ASCII.GetBytes(string.Concat("\r\n--", boundary));
        _maximumParts = maximumParts;
        _aborted = aborted;
    }

    /// <summary>
    /// Reads the next multipart part, or returns <see langword="null"/> after the closing boundary.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels this read.</param>
    /// <returns>The next part, or <see langword="null"/> at the end of the multipart body.</returns>
    /// <exception cref="FormException">The multipart body is malformed or contains too many parts.</exception>
    public ValueTask<MultipartPart?> ReadNextAsync(CancellationToken cancellationToken = default) =>
        ReadNextCoreAsync(cancellationToken);

    private async ValueTask<MultipartPart?> ReadNextCoreAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            using var linkedCancellation = CreateLinkedCancellation(cancellationToken);
            var effectiveCancellation = linkedCancellation?.Token
                ?? (cancellationToken.CanBeCanceled ? cancellationToken : _aborted);
            effectiveCancellation.ThrowIfCancellationRequested();

            if (_activeBody is not null)
            {
                await _activeBody.DrainAsync(effectiveCancellation).ConfigureAwait(false);
                _ended = _activeBody.TerminalBoundary;
                _activeBody = null;
            }

            if (!_started)
            {
                _ended = await ReadInitialBoundaryAsync(effectiveCancellation).ConfigureAwait(false);
                _started = true;
            }

            if (_ended)
            {
                await DrainEpilogueAsync(effectiveCancellation).ConfigureAwait(false);
                return null;
            }

            if (_partCount == _maximumParts)
            {
                throw FormException.BadRequest($"The multipart body contains more than {_maximumParts} parts.");
            }

            _partCount++;
            var headers = await ReadHeadersAsync(effectiveCancellation).ConfigureAwait(false);
            var disposition = ParseContentDisposition(headers);
            var contentType = headers.Get("Content-Type") ?? "application/octet-stream";
            if (headers.ContainsNestedMultipart())
            {
                throw FormException.BadRequest("Nested multipart content is not supported.");
            }

            var pipe = new Pipe(new PipeOptions(
                pauseWriterThreshold: 64 * 1024,
                resumeWriterThreshold: 32 * 1024,
                useSynchronizationContext: false));
            var body = new MultipartPartBodyReader(pipe.Reader);
            body.SetPump(PumpPartBodyAsync(pipe.Writer, body));
            _activeBody = body;
            return new MultipartPart(
                disposition.Name,
                disposition.FileName,
                contentType,
                headers.Names,
                headers.Values,
                body,
                disposition.HasFileName);
        }
        catch
        {
            await CleanupActiveBodyAfterErrorAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Stops reading the current part and waits for its body processing to finish.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await CleanupActiveBodyAsync().ConfigureAwait(false);
    }

    private async ValueTask CleanupActiveBodyAfterErrorAsync()
    {
        try
        {
            await CleanupActiveBodyAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async ValueTask CleanupActiveBodyAsync()
    {
        var body = _activeBody;
        if (body is null)
        {
            return;
        }

        _activeBody = null;
        try
        {
            await body.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _ended = body.TerminalBoundary;
        }
    }

    private CancellationTokenSource? CreateLinkedCancellation(CancellationToken cancellationToken)
    {
        if (!_aborted.CanBeCanceled
            || !cancellationToken.CanBeCanceled
            || cancellationToken == _aborted)
        {
            return null;
        }

        return CancellationTokenSource.CreateLinkedTokenSource(_aborted, cancellationToken);
    }

    private async ValueTask<bool> ReadInitialBoundaryAsync(CancellationToken cancellationToken)
    {
        var checkBodyStart = true;
        while (true)
        {
            var result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;
            var search = buffer;

            if (result.IsCanceled)
            {
                _reader.AdvanceTo(buffer.Start, buffer.Start);
                throw new OperationCanceledException(cancellationToken);
            }

            if (checkBodyStart)
            {
                var prefix = GetPrefixState(buffer, _initialBoundary);
                if (prefix == PrefixState.Partial && !result.IsCompleted)
                {
                    _reader.AdvanceTo(buffer.Start, buffer.End);
                    continue;
                }

                if (prefix == PrefixState.Full)
                {
                    var afterPattern = buffer.GetPosition(_initialBoundary.Length);
                    var termination = ReadBoundaryTermination(
                        buffer.Slice(afterPattern),
                        result.IsCompleted,
                        out var terminationBytes);
                    if (termination == BoundaryTermination.NeedMore)
                    {
                        _reader.AdvanceTo(buffer.Start, buffer.End);
                        continue;
                    }

                    if (termination is BoundaryTermination.NextPart or BoundaryTermination.Final)
                    {
                        var consumed = buffer.GetPosition(terminationBytes, afterPattern);
                        _reader.AdvanceTo(consumed, consumed);
                        return termination == BoundaryTermination.Final;
                    }
                }

                checkBodyStart = false;
            }

            while (TryFindPattern(search, _partBoundary, out var candidate, out var afterPattern))
            {
                var termination = ReadBoundaryTermination(
                    buffer.Slice(afterPattern),
                    result.IsCompleted,
                    out var terminationBytes);
                if (termination == BoundaryTermination.NeedMore)
                {
                    _reader.AdvanceTo(candidate, buffer.End);
                    goto ContinueReading;
                }

                if (termination is BoundaryTermination.NextPart or BoundaryTermination.Final)
                {
                    var consumed = buffer.GetPosition(terminationBytes, afterPattern);
                    _reader.AdvanceTo(consumed, consumed);
                    return termination == BoundaryTermination.Final;
                }

                var afterFirstCandidateByte = buffer.GetPosition(1, candidate);
                search = buffer.Slice(afterFirstCandidateByte);
            }

            if (result.IsCompleted)
            {
                _reader.AdvanceTo(buffer.End);
                throw FormException.BadRequest("The multipart body does not contain a valid boundary.");
            }

            var retainedBytes = Math.Min((long)_partBoundary.Length - 1, search.Length);
            var consumedPosition = buffer.GetPosition(buffer.Length - retainedBytes);
            _reader.AdvanceTo(consumedPosition, buffer.End);

        ContinueReading:
            continue;
        }
    }

    private async ValueTask<MultipartHeaders> ReadHeadersAsync(CancellationToken cancellationToken)
    {
        var names = new List<string>();
        var values = new List<string>();
        while (true)
        {
            var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line.Length == 0)
            {
                return new MultipartHeaders(names.ToArray(), values.ToArray());
            }

            if (names.Count == MaximumHeaders)
            {
                throw FormException.BadRequest($"A multipart part contains more than {MaximumHeaders} headers.");
            }

            ParseHeaderLine(line, out var name, out var value);
            names.Add(name);
            values.Add(value);
        }
    }

    private async ValueTask<byte[]> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;
            if (result.IsCanceled)
            {
                _reader.AdvanceTo(buffer.Start, buffer.Start);
                throw new OperationCanceledException(cancellationToken);
            }

            var sequenceReader = new SequenceReader<byte>(buffer);
            if (sequenceReader.TryReadTo(
                out ReadOnlySequence<byte> line,
                CrLf,
                advancePastDelimiter: true))
            {
                if (line.Length > MaximumHeaderBytes)
                {
                    _reader.AdvanceTo(sequenceReader.Position, sequenceReader.Position);
                    throw FormException.BadRequest($"A multipart header exceeds {MaximumHeaderBytes} bytes.");
                }

                var bytes = line.ToArray();
                _reader.AdvanceTo(sequenceReader.Position, sequenceReader.Position);
                return bytes;
            }

            if (buffer.Length > MaximumHeaderBytes + 1L)
            {
                _reader.AdvanceTo(buffer.End);
                throw FormException.BadRequest($"A multipart header exceeds {MaximumHeaderBytes} bytes.");
            }

            if (result.IsCompleted)
            {
                _reader.AdvanceTo(buffer.End);
                throw FormException.BadRequest("The multipart body ended in the part headers.");
            }

            _reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    private async Task PumpPartBodyAsync(PipeWriter writer, MultipartPartBodyReader body)
    {
        Exception? error = null;
        try
        {
            var discardOutput = false;
            while (true)
            {
                var result = await _reader.ReadAsync(_aborted).ConfigureAwait(false);
                var buffer = result.Buffer;
                var search = buffer;
                if (result.IsCanceled)
                {
                    _reader.AdvanceTo(buffer.Start, buffer.Start);
                    throw new OperationCanceledException(_aborted);
                }

                while (TryFindPattern(search, _partBoundary, out var candidate, out var afterPattern))
                {
                    var termination = ReadBoundaryTermination(
                        buffer.Slice(afterPattern),
                        result.IsCompleted,
                        out var terminationBytes);
                    if (termination == BoundaryTermination.NeedMore)
                    {
                        discardOutput |= await WriteOutputAsync(
                            writer,
                            buffer.Slice(buffer.Start, candidate),
                            discardOutput).ConfigureAwait(false);
                        _reader.AdvanceTo(candidate, buffer.End);
                        goto ContinueReading;
                    }

                    if (termination is BoundaryTermination.NextPart or BoundaryTermination.Final)
                    {
                        discardOutput |= await WriteOutputAsync(
                            writer,
                            buffer.Slice(buffer.Start, candidate),
                            discardOutput).ConfigureAwait(false);
                        var consumed = buffer.GetPosition(terminationBytes, afterPattern);
                        _reader.AdvanceTo(consumed, consumed);
                        body.TerminalBoundary = termination == BoundaryTermination.Final;
                        return;
                    }

                    var afterFirstCandidateByte = buffer.GetPosition(1, candidate);
                    discardOutput |= await WriteOutputAsync(
                        writer,
                        buffer.Slice(buffer.Start, afterFirstCandidateByte),
                        discardOutput).ConfigureAwait(false);
                    buffer = buffer.Slice(afterFirstCandidateByte);
                    search = buffer;
                }

                if (result.IsCompleted)
                {
                    discardOutput |= await WriteOutputAsync(writer, buffer, discardOutput).ConfigureAwait(false);
                    _reader.AdvanceTo(buffer.End);
                    throw FormException.BadRequest("The multipart body is missing its closing boundary.");
                }

                var retainedBytes = Math.Min((long)_partBoundary.Length - 1, buffer.Length);
                var consumedPosition = buffer.GetPosition(buffer.Length - retainedBytes);
                discardOutput |= await WriteOutputAsync(
                    writer,
                    buffer.Slice(buffer.Start, consumedPosition),
                    discardOutput).ConfigureAwait(false);
                _reader.AdvanceTo(consumedPosition, buffer.End);

            ContinueReading:
                continue;
            }
        }
        catch (Exception exception)
        {
            error = exception;
        }
        finally
        {
            try
            {
                await writer.CompleteAsync(error).ConfigureAwait(false);
            }
            catch when (error is not null)
            {
            }
        }

        if (error is not null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    private async ValueTask<bool> WriteOutputAsync(
        PipeWriter writer,
        ReadOnlySequence<byte> content,
        bool discardOutput)
    {
        if (discardOutput || content.IsEmpty)
        {
            return discardOutput;
        }

        foreach (var memory in content)
        {
            var remaining = memory;
            while (!remaining.IsEmpty)
            {
                var length = Math.Min(OutputChunkBytes, remaining.Length);
                writer.Write(remaining.Span[..length]);
                remaining = remaining[length..];
                var flush = await writer.FlushAsync(_aborted).ConfigureAwait(false);
                if (flush.IsCanceled)
                {
                    throw new OperationCanceledException(_aborted);
                }

                if (flush.IsCompleted)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private async ValueTask DrainEpilogueAsync(CancellationToken cancellationToken)
    {
        if (_epilogueDrained)
        {
            return;
        }

        while (true)
        {
            var result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;
            _reader.AdvanceTo(buffer.End);
            if (result.IsCompleted)
            {
                _epilogueDrained = true;
                return;
            }

            if (result.IsCanceled)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
    }

    private static void ParseHeaderLine(byte[] line, out string name, out string value)
    {
        if (line.Length == 0 || line[0] is (byte)' ' or (byte)'\t')
        {
            throw FormException.BadRequest("Multipart header folding is not supported.");
        }

        var colon = line.AsSpan().IndexOf((byte)':');
        if (colon <= 0)
        {
            throw FormException.BadRequest("A multipart header line is invalid.");
        }

        for (var i = 0; i < colon; i++)
        {
            if (!IsTokenCharacter((char)line[i]))
            {
                throw FormException.BadRequest("A multipart header name is invalid.");
            }
        }

        var valueStart = colon + 1;
        while (valueStart < line.Length && line[valueStart] is (byte)' ' or (byte)'\t')
        {
            valueStart++;
        }

        var valueEnd = line.Length;
        while (valueEnd > valueStart && line[valueEnd - 1] is (byte)' ' or (byte)'\t')
        {
            valueEnd--;
        }

        for (var i = valueStart; i < valueEnd; i++)
        {
            if (line[i] > 0x7f || (line[i] < 0x20 && line[i] != '\t') || line[i] == 0x7f)
            {
                throw FormException.BadRequest("A multipart header contains a non-ASCII or control character.");
            }
        }

        name = Encoding.ASCII.GetString(line, 0, colon);
        value = Encoding.ASCII.GetString(line, valueStart, valueEnd - valueStart);
    }

    private static ParsedDisposition ParseContentDisposition(MultipartHeaders headers)
    {
        var dispositionCount = headers.Count("Content-Disposition");
        if (dispositionCount != 1)
        {
            throw FormException.BadRequest("Each multipart part requires one Content-Disposition header.");
        }

        var value = headers.Get("Content-Disposition")!;
        var position = 0;
        SkipWhitespace(value, ref position);
        var dispositionStart = position;
        while (position < value.Length && value[position] != ';' && value[position] is not ' ' and not '\t')
        {
            position++;
        }

        if (!value.AsSpan(dispositionStart, position - dispositionStart)
            .Equals("form-data", StringComparison.OrdinalIgnoreCase))
        {
            throw FormException.BadRequest("The multipart Content-Disposition must be form-data.");
        }

        SkipWhitespace(value, ref position);
        string? name = null;
        string? fileName = null;
        string? extendedFileName = null;
        var hasRegularFileName = false;
        var hasFileName = false;
        while (position < value.Length)
        {
            if (value[position] != ';')
            {
                throw FormException.BadRequest("The multipart Content-Disposition parameters are invalid.");
            }

            position++;
            SkipWhitespace(value, ref position);
            var parameterStart = position;
            while (position < value.Length
                && value[position] != '='
                && value[position] != ';'
                && value[position] is not ' ' and not '\t')
            {
                position++;
            }

            var parameterEnd = position;
            SkipWhitespace(value, ref position);
            if (parameterStart == parameterEnd || position == value.Length || value[position] != '=')
            {
                throw FormException.BadRequest("The multipart Content-Disposition parameters are invalid.");
            }

            position++;
            SkipWhitespace(value, ref position);
            var parameterValue = ReadDispositionParameter(value, ref position);
            var parameterName = value.AsSpan(parameterStart, parameterEnd - parameterStart);
            if (parameterName.Equals("name", StringComparison.OrdinalIgnoreCase))
            {
                if (name is not null)
                {
                    throw FormException.BadRequest("The multipart Content-Disposition repeats the name parameter.");
                }

                name = parameterValue;
            }
            else if (parameterName.Equals("filename", StringComparison.OrdinalIgnoreCase))
            {
                if (hasRegularFileName)
                {
                    throw FormException.BadRequest("The multipart Content-Disposition repeats the filename parameter.");
                }

                hasRegularFileName = true;
                hasFileName = true;
                fileName = parameterValue;
            }
            else if (parameterName.Equals("filename*", StringComparison.OrdinalIgnoreCase))
            {
                if (extendedFileName is not null)
                {
                    throw FormException.BadRequest("The multipart Content-Disposition repeats the filename* parameter.");
                }

                hasFileName = true;
                extendedFileName = DecodeExtendedFileName(parameterValue);
            }

            SkipWhitespace(value, ref position);
        }

        if (name is null)
        {
            throw FormException.BadRequest("The multipart Content-Disposition is missing the name parameter.");
        }

        return new ParsedDisposition(
            name,
            StripPath(extendedFileName ?? fileName ?? string.Empty),
            hasFileName);
    }

    private static string ReadDispositionParameter(string value, ref int position)
    {
        if (position == value.Length)
        {
            throw FormException.BadRequest("A multipart Content-Disposition parameter has no value.");
        }

        if (value[position] != '"')
        {
            var start = position;
            while (position < value.Length && value[position] != ';')
            {
                position++;
            }

            var token = value[start..position].Trim();
            if (token.Length == 0)
            {
                throw FormException.BadRequest("A multipart Content-Disposition parameter has no value.");
            }

            for (var i = 0; i < token.Length; i++)
            {
                if (!IsTokenCharacter(token[i]))
                {
                    throw FormException.BadRequest("A multipart Content-Disposition parameter value is invalid.");
                }
            }

            return token;
        }

        position++;
        var builder = new StringBuilder();
        while (position < value.Length)
        {
            var current = value[position++];
            if (current == '"')
            {
                SkipWhitespace(value, ref position);
                if (position < value.Length && value[position] != ';')
                {
                    throw FormException.BadRequest("A multipart Content-Disposition quoted value is invalid.");
                }

                return builder.ToString();
            }

            if (current == '\\')
            {
                if (position == value.Length)
                {
                    break;
                }

                var escaped = value[position];
                if (escaped is '"' or '\\')
                {
                    current = escaped;
                    position++;
                }
            }

            builder.Append(current);
        }

        throw FormException.BadRequest("A multipart Content-Disposition quoted value is unterminated.");
    }

    private static string DecodeExtendedFileName(string value)
    {
        var firstQuote = value.IndexOf('\'');
        var secondQuote = firstQuote < 0 ? -1 : value.IndexOf('\'', firstQuote + 1);
        if (firstQuote <= 0
            || secondQuote < 0
            || !value.AsSpan(0, firstQuote).Equals("UTF-8", StringComparison.OrdinalIgnoreCase))
        {
            throw FormException.BadRequest("The filename* parameter must use RFC 5987 UTF-8 encoding.");
        }

        var encoded = value.AsSpan(secondQuote + 1);
        var rented = ArrayPool<byte>.Shared.Rent(Math.Max(1, encoded.Length));
        var written = 0;
        try
        {
            for (var i = 0; i < encoded.Length; i++)
            {
                if (encoded[i] == '%')
                {
                    if (i > encoded.Length - 3
                        || !TryHex(encoded[i + 1], out var high)
                        || !TryHex(encoded[i + 2], out var low))
                    {
                        throw FormException.BadRequest("The filename* parameter contains an invalid percent escape.");
                    }

                    rented[written++] = (byte)((high << 4) | low);
                    i += 2;
                }
                else
                {
                    if (encoded[i] > 0x7f || !IsAttributeCharacter(encoded[i]))
                    {
                        throw FormException.BadRequest("The filename* parameter contains an invalid character.");
                    }

                    rented[written++] = (byte)encoded[i];
                }
            }

            try
            {
                return StrictUtf8.GetString(rented.AsSpan(0, written));
            }
            catch (DecoderFallbackException exception)
            {
                throw FormException.BadRequest("The filename* parameter is not valid UTF-8.", exception);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static bool TryHex(char value, out int result)
    {
        if (value is >= '0' and <= '9')
        {
            result = value - '0';
            return true;
        }

        if (value is >= 'A' and <= 'F')
        {
            result = value - 'A' + 10;
            return true;
        }

        if (value is >= 'a' and <= 'f')
        {
            result = value - 'a' + 10;
            return true;
        }

        result = 0;
        return false;
    }

    private static bool IsAttributeCharacter(char value) =>
        (value is >= '0' and <= '9')
        || (value is >= 'A' and <= 'Z')
        || (value is >= 'a' and <= 'z')
        || value is '!' or '#' or '$' or '&' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';

    private static string StripPath(string fileName)
    {
        var slash = fileName.LastIndexOf('/');
        var backslash = fileName.LastIndexOf('\\');
        var separator = Math.Max(slash, backslash);
        return separator < 0 ? fileName : fileName[(separator + 1)..];
    }

    private static bool TryFindPattern(
        ReadOnlySequence<byte> buffer,
        ReadOnlySpan<byte> pattern,
        out SequencePosition candidate,
        out SequencePosition afterPattern)
    {
        var reader = new SequenceReader<byte>(buffer);
        if (reader.TryReadTo(
            out ReadOnlySequence<byte> before,
            pattern,
            advancePastDelimiter: true))
        {
            candidate = before.End;
            afterPattern = reader.Position;
            return true;
        }

        candidate = default;
        afterPattern = default;
        return false;
    }

    private static PrefixState GetPrefixState(ReadOnlySequence<byte> buffer, ReadOnlySpan<byte> pattern)
    {
        var compareLength = (int)Math.Min(buffer.Length, pattern.Length);
        Span<byte> prefix = stackalloc byte[72];
        buffer.Slice(0, compareLength).CopyTo(prefix);
        if (!prefix[..compareLength].SequenceEqual(pattern[..compareLength]))
        {
            return PrefixState.None;
        }

        return compareLength == pattern.Length ? PrefixState.Full : PrefixState.Partial;
    }

    private static BoundaryTermination ReadBoundaryTermination(
        ReadOnlySequence<byte> afterPattern,
        bool isCompleted,
        out long consumedBytes)
    {
        consumedBytes = 0;
        var reader = new SequenceReader<byte>(afterPattern);
        if (!reader.TryRead(out var first))
        {
            return isCompleted ? BoundaryTermination.Invalid : BoundaryTermination.NeedMore;
        }

        if (first == '\r')
        {
            if (!reader.TryRead(out var lineFeed))
            {
                return isCompleted ? BoundaryTermination.Invalid : BoundaryTermination.NeedMore;
            }

            if (lineFeed != '\n')
            {
                return BoundaryTermination.Invalid;
            }

            consumedBytes = reader.Consumed;
            return BoundaryTermination.NextPart;
        }

        var final = false;
        if (first == '-')
        {
            if (!reader.TryRead(out var second))
            {
                return isCompleted ? BoundaryTermination.Invalid : BoundaryTermination.NeedMore;
            }

            if (second != '-')
            {
                return BoundaryTermination.Invalid;
            }

            final = true;
        }
        else if (first is not (byte)' ' and not (byte)'\t')
        {
            return BoundaryTermination.Invalid;
        }

        while (reader.TryRead(out var padding))
        {
            if (reader.Consumed > MaximumBoundaryPaddingBytes + 2L)
            {
                return BoundaryTermination.Invalid;
            }

            if (padding is (byte)' ' or (byte)'\t')
            {
                continue;
            }

            if (padding != '\r')
            {
                return BoundaryTermination.Invalid;
            }

            if (!reader.TryRead(out var lineFeed))
            {
                return isCompleted ? BoundaryTermination.Invalid : BoundaryTermination.NeedMore;
            }

            if (lineFeed != '\n')
            {
                return BoundaryTermination.Invalid;
            }

            consumedBytes = reader.Consumed;
            return final ? BoundaryTermination.Final : BoundaryTermination.NextPart;
        }

        if (isCompleted && final)
        {
            consumedBytes = reader.Consumed;
            return BoundaryTermination.Final;
        }

        return isCompleted ? BoundaryTermination.Invalid : BoundaryTermination.NeedMore;
    }

    private static bool IsTokenCharacter(char value)
    {
        if (value is < '!' or > '~')
        {
            return false;
        }

        return value is not '(' and not ')' and not '<' and not '>' and not '@'
            and not ',' and not ';' and not ':' and not '\\' and not '"' and not '/'
            and not '[' and not ']' and not '?' and not '=' and not '{' and not '}';
    }

    private static void SkipWhitespace(string value, ref int position)
    {
        while (position < value.Length && value[position] is ' ' or '\t')
        {
            position++;
        }
    }

    private enum PrefixState
    {
        None,
        Partial,
        Full,
    }

    private enum BoundaryTermination
    {
        Invalid,
        NeedMore,
        NextPart,
        Final,
    }

    private readonly record struct ParsedDisposition(string Name, string FileName, bool HasFileName);
}

/// <summary>
/// Represents one streamed multipart form part.
/// </summary>
public sealed class MultipartPart
{
    private readonly string[] _headerNames;
    private readonly string[] _headerValues;

    internal MultipartPart(
        string name,
        string fileName,
        string contentType,
        string[] headerNames,
        string[] headerValues,
        PipeReader body,
        bool hasFileName)
    {
        Name = name;
        FileName = fileName;
        ContentType = contentType;
        _headerNames = headerNames;
        _headerValues = headerValues;
        Body = body;
        HasFileName = hasFileName;
    }

    /// <summary>
    /// Gets the form field name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the submitted file name without path components, or an empty string when none was supplied.
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// Gets the part content type, or <c>application/octet-stream</c> when none was supplied.
    /// </summary>
    public string ContentType { get; }

    /// <summary>
    /// Gets the streaming body reader. Consume or complete it before retaining no further interest in the part.
    /// </summary>
    public PipeReader Body { get; }

    internal bool HasFileName { get; }

    /// <summary>
    /// Gets the first value of a part header, or <see langword="null"/> when the header is absent.
    /// </summary>
    /// <param name="name">The case-insensitive header name.</param>
    /// <returns>The first matching value, or <see langword="null"/>.</returns>
    public string? Header(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        for (var i = 0; i < _headerNames.Length; i++)
        {
            if (string.Equals(_headerNames[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return _headerValues[i];
            }
        }

        return null;
    }
}

internal sealed class MultipartHeaders
{
    public MultipartHeaders(string[] names, string[] values)
    {
        Names = names;
        Values = values;
    }

    public string[] Names { get; }

    public string[] Values { get; }

    public string? Get(string name)
    {
        for (var i = 0; i < Names.Length; i++)
        {
            if (string.Equals(Names[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return Values[i];
            }
        }

        return null;
    }

    public int Count(string name)
    {
        var count = 0;
        for (var i = 0; i < Names.Length; i++)
        {
            if (string.Equals(Names[i], name, StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        return count;
    }

    public bool ContainsNestedMultipart()
    {
        for (var i = 0; i < Names.Length; i++)
        {
            if (string.Equals(Names[i], "Content-Type", StringComparison.OrdinalIgnoreCase)
                && IsNestedMultipart(Values[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNestedMultipart(string contentType)
    {
        var semicolon = contentType.IndexOf(';');
        var mediaType = (semicolon < 0 ? contentType.AsSpan() : contentType.AsSpan(0, semicolon)).Trim();
        return mediaType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class MultipartPartBodyReader : PipeReader
{
    private readonly PipeReader _inner;
    private ReadOnlySequence<byte> _activeBuffer;
    private Task? _pump;
    private bool _hasActiveRead;
    private bool _completed;

    public MultipartPartBodyReader(PipeReader inner)
    {
        _inner = inner;
    }

    public bool TerminalBoundary { get; set; }

    public void SetPump(Task pump) => _pump = pump;

    public override void AdvanceTo(SequencePosition consumed)
    {
        EnsureActiveRead();
        _inner.AdvanceTo(consumed);
        ClearActiveRead();
    }

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        EnsureActiveRead();
        _inner.AdvanceTo(consumed, examined);
        ClearActiveRead();
    }

    public override void CancelPendingRead() => _inner.CancelPendingRead();

    public override void Complete(Exception? exception = null)
    {
        if (_completed)
        {
            return;
        }

        ConsumeActiveBuffer();
        _inner.Complete(exception);
        _completed = true;
    }

    public override async ValueTask CompleteAsync(Exception? exception = null)
    {
        if (_completed)
        {
            return;
        }

        ConsumeActiveBuffer();
        await _inner.CompleteAsync(exception).ConfigureAwait(false);
        _completed = true;
    }

    public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        EnsureCanRead();
        var operation = _inner.ReadAsync(cancellationToken);
        if (operation.IsCompletedSuccessfully)
        {
            return new ValueTask<ReadResult>(TrackRead(operation.Result));
        }

        return AwaitReadAsync(operation);
    }

    public override bool TryRead(out ReadResult result)
    {
        EnsureCanRead();
        if (!_inner.TryRead(out var innerResult))
        {
            result = default;
            return false;
        }

        result = TrackRead(innerResult);
        return true;
    }

    public async ValueTask DrainAsync(CancellationToken cancellationToken)
    {
        if (!_completed)
        {
            ConsumeActiveBuffer();
            while (true)
            {
                var result = await _inner.ReadAsync(cancellationToken).ConfigureAwait(false);
                _inner.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted)
                {
                    break;
                }

                if (result.IsCanceled)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
            }

            await _inner.CompleteAsync().ConfigureAwait(false);
            _completed = true;
        }

        if (_pump is not null)
        {
            await _pump.ConfigureAwait(false);
        }
    }

    public async ValueTask StopAsync()
    {
        Exception? completionError = null;
        try
        {
            await CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            completionError = exception;
        }

        try
        {
            if (_pump is not null)
            {
                await _pump.ConfigureAwait(false);
            }
        }
        catch when (completionError is not null)
        {
        }

        if (completionError is not null)
        {
            ExceptionDispatchInfo.Capture(completionError).Throw();
        }
    }

    private async ValueTask<ReadResult> AwaitReadAsync(ValueTask<ReadResult> operation) =>
        TrackRead(await operation.ConfigureAwait(false));

    private ReadResult TrackRead(ReadResult result)
    {
        _activeBuffer = result.Buffer;
        _hasActiveRead = true;
        return result;
    }

    private void EnsureCanRead()
    {
        if (_completed)
        {
            throw new InvalidOperationException("The multipart part body reader is complete.");
        }

        if (_hasActiveRead)
        {
            throw new InvalidOperationException("AdvanceTo must be called before reading again.");
        }
    }

    private void EnsureActiveRead()
    {
        if (!_hasActiveRead)
        {
            throw new InvalidOperationException("No read operation is active.");
        }
    }

    private void ConsumeActiveBuffer()
    {
        if (!_hasActiveRead)
        {
            return;
        }

        _inner.AdvanceTo(_activeBuffer.End);
        ClearActiveRead();
    }

    private void ClearActiveRead()
    {
        _activeBuffer = default;
        _hasActiveRead = false;
    }
}
