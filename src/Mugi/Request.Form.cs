using System.Buffers;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Mugi;

public sealed partial class Request
{
    /// <summary>
    /// Reads an URL-encoded or multipart request body into memory.
    /// </summary>
    /// <returns>The parsed form data.</returns>
    /// <exception cref="FormException">The content type or form body is invalid or exceeds a form limit.</exception>
    public async ValueTask<FormData> Form()
    {
        _context.EnsureActive();
        ClaimBody();

        var contentType = GetFormContentType();
        if (IsMediaType(contentType, "application/x-www-form-urlencoded"))
        {
            var reader = CreateClaimedBodyReader(enforceFormLimit: true);
            using var body = await ReadClaimedBody(reader, _context.Options.MaxFormBodyBytes).ConfigureAwait(false);
            return ParseUrlEncoded(body.WrittenMemory.Span, _context.Options.MaxFormFields);
        }

        if (!IsMediaType(contentType, "multipart/form-data"))
        {
            throw FormException.UnsupportedMediaType();
        }

        var boundary = ParseBoundary(contentType);
        var multipart = new MultipartReader(
            CreateClaimedBodyReader(enforceFormLimit: true),
            boundary,
            _context.Options.MaxMultipartParts,
            _context.Aborted);
        try
        {
            return await BufferMultipart(multipart).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await multipart.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }

            throw;
        }
    }

    /// <summary>
    /// Opens a multipart request body for sequential streaming.
    /// </summary>
    /// <returns>A reader positioned before the first multipart part.</returns>
    /// <exception cref="FormException">The content type or multipart boundary is invalid.</exception>
    public ValueTask<MultipartReader> Multipart()
    {
        _context.EnsureActive();
        ClaimBody();

        var contentType = GetFormContentType();
        if (!IsMediaType(contentType, "multipart/form-data"))
        {
            throw FormException.UnsupportedMediaType();
        }

        var boundary = ParseBoundary(contentType);
        return new ValueTask<MultipartReader>(new MultipartReader(
            CreateClaimedBodyReader(enforceFormLimit: false),
            boundary,
            _context.Options.MaxMultipartParts,
            _context.Aborted));
    }

    private string GetFormContentType()
    {
        if (!Feature.Headers.TryGetValue("Content-Type", out var values))
        {
            throw FormException.UnsupportedMediaType();
        }

        var contentType = values.ToString();
        if (contentType.Length == 0)
        {
            throw FormException.UnsupportedMediaType();
        }

        return contentType;
    }

    private PipeReader CreateClaimedBodyReader(bool enforceFormLimit)
    {
        if (enforceFormLimit)
        {
            ValidateFormContentLength(_context.Options.MaxFormBodyBytes);
        }

        var requestLimit = _context.Options.MaxRequestBodyBytes;
        ValidateContentLength(requestLimit);
        PipeReader reader = new LimitedPipeReader(GetBodyReader(), requestLimit);
        if (enforceFormLimit)
        {
            reader = new FormLimitedPipeReader(reader, _context.Options.MaxFormBodyBytes);
        }

        return reader;
    }

    private void ValidateFormContentLength(int limit)
    {
        if (!Feature.Headers.TryGetValue("Content-Length", out var values))
        {
            return;
        }

        var raw = values.ToString();
        if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length < 0)
        {
            throw FormException.BadRequest("The Content-Length header is invalid.");
        }

        if (length > limit)
        {
            throw FormException.PayloadTooLarge(limit);
        }
    }

    private async ValueTask<PooledByteBufferWriter> ReadClaimedBody(PipeReader reader, int limit)
    {
        var destination = new PooledByteBufferWriter(_context.Options.Json.MaxPooledBufferByteLength);
        try
        {
            while (true)
            {
                var result = await reader.ReadAsync(_context.Aborted).ConfigureAwait(false);
                var buffer = result.Buffer;
                try
                {
                    foreach (var segment in buffer)
                    {
                        if (segment.Length > limit - destination.WrittenCount)
                        {
                            throw FormException.PayloadTooLarge(limit);
                        }

                        destination.Write(segment.Span);
                    }
                }
                finally
                {
                    reader.AdvanceTo(buffer.End);
                }

                if (result.IsCompleted)
                {
                    return destination;
                }

                if (result.IsCanceled)
                {
                    throw new OperationCanceledException(_context.Aborted);
                }
            }
        }
        catch
        {
            destination.Dispose();
            throw;
        }
    }

    private async ValueTask<FormData> BufferMultipart(MultipartReader multipart)
    {
        var fields = new List<KeyValuePair<string, string>>();
        var files = new List<FormFile>();
        var bufferedBytes = 0;
        var limit = _context.Options.MaxFormBodyBytes;
        var countLimit = _context.Options.MaxFormFields;

        while (await multipart.ReadNextAsync(_context.Aborted).ConfigureAwait(false) is { } part)
        {
            if (part.HasFileName)
            {
                if (files.Count == countLimit)
                {
                    throw FormException.BadRequest($"The multipart form contains more than {countLimit} files.");
                }

                var content = await BufferPart(part.Body, limit - bufferedBytes).ConfigureAwait(false);
                bufferedBytes = checked(bufferedBytes + content.Length);
                files.Add(new FormFile(part.Name, part.FileName, part.ContentType, content));
            }
            else
            {
                if (fields.Count == countLimit)
                {
                    throw FormException.BadRequest($"The form contains more than {countLimit} fields.");
                }

                var content = await BufferPart(part.Body, limit - bufferedBytes).ConfigureAwait(false);
                bufferedBytes = checked(bufferedBytes + content.Length);
                string value;
                try
                {
                    value = StrictUtf8.GetString(content.Span);
                }
                catch (DecoderFallbackException exception)
                {
                    throw FormException.BadRequest("A multipart form field is not valid UTF-8.", exception);
                }

                fields.Add(new KeyValuePair<string, string>(part.Name, value));
            }
        }

        return new FormData(fields.ToArray(), files.ToArray());
    }

    private async ValueTask<ReadOnlyMemory<byte>> BufferPart(PipeReader reader, int remaining)
    {
        if (remaining < 0)
        {
            throw FormException.PayloadTooLarge(_context.Options.MaxFormBodyBytes);
        }

        using var destination = new PooledByteBufferWriter(_context.Options.Json.MaxPooledBufferByteLength);
        while (true)
        {
            var result = await reader.ReadAsync(_context.Aborted).ConfigureAwait(false);
            var buffer = result.Buffer;
            try
            {
                foreach (var segment in buffer)
                {
                    if (segment.Length > remaining - destination.WrittenCount)
                    {
                        throw FormException.PayloadTooLarge(_context.Options.MaxFormBodyBytes);
                    }

                    destination.Write(segment.Span);
                }
            }
            finally
            {
                reader.AdvanceTo(buffer.End);
            }

            if (result.IsCompleted)
            {
                reader.Complete();
                return destination.WrittenMemory.ToArray();
            }

            if (result.IsCanceled)
            {
                throw new OperationCanceledException(_context.Aborted);
            }
        }
    }

    private static FormData ParseUrlEncoded(ReadOnlySpan<byte> body, int fieldLimit)
    {
        var fields = new List<KeyValuePair<string, string>>();
        var start = 0;
        while (start <= body.Length)
        {
            var separator = body[start..].IndexOf((byte)'&');
            var end = separator < 0 ? body.Length : start + separator;
            if (end > start)
            {
                if (fields.Count == fieldLimit)
                {
                    throw FormException.BadRequest($"The form contains more than {fieldLimit} fields.");
                }

                var pair = body[start..end];
                var equals = pair.IndexOf((byte)'=');
                var nameBytes = equals < 0 ? pair : pair[..equals];
                var valueBytes = equals < 0 ? ReadOnlySpan<byte>.Empty : pair[(equals + 1)..];
                fields.Add(new KeyValuePair<string, string>(
                    DecodeUrlEncoded(nameBytes),
                    DecodeUrlEncoded(valueBytes)));
            }

            if (separator < 0)
            {
                break;
            }

            start = end + 1;
        }

        return new FormData(fields.ToArray(), Array.Empty<FormFile>());
    }

    private static string DecodeUrlEncoded(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            return string.Empty;
        }

        var rented = ArrayPool<byte>.Shared.Rent(value.Length);
        var written = 0;
        try
        {
            for (var i = 0; i < value.Length; i++)
            {
                var current = value[i];
                if (current == '+')
                {
                    rented[written++] = (byte)' ';
                }
                else if (current == '%')
                {
                    if (i > value.Length - 3
                        || !TryHex(value[i + 1], out var high)
                        || !TryHex(value[i + 2], out var low))
                    {
                        throw FormException.BadRequest("The URL-encoded form contains an invalid percent escape.");
                    }

                    rented[written++] = (byte)((high << 4) | low);
                    i += 2;
                }
                else
                {
                    rented[written++] = current;
                }
            }

            try
            {
                return StrictUtf8.GetString(rented.AsSpan(0, written));
            }
            catch (DecoderFallbackException exception)
            {
                throw FormException.BadRequest("The URL-encoded form is not valid UTF-8.", exception);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static bool TryHex(byte value, out int result)
    {
        if (value is >= (byte)'0' and <= (byte)'9')
        {
            result = value - '0';
            return true;
        }

        if (value is >= (byte)'A' and <= (byte)'F')
        {
            result = value - 'A' + 10;
            return true;
        }

        if (value is >= (byte)'a' and <= (byte)'f')
        {
            result = value - 'a' + 10;
            return true;
        }

        result = 0;
        return false;
    }

    private static bool IsMediaType(string contentType, string expected)
    {
        var span = contentType.AsSpan();
        var semicolon = span.IndexOf(';');
        var mediaType = (semicolon < 0 ? span : span[..semicolon]).Trim();
        return mediaType.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string ParseBoundary(string contentType)
    {
        var position = contentType.IndexOf(';');
        string? boundary = null;
        while (position >= 0 && position < contentType.Length)
        {
            position++;
            SkipWhitespace(contentType, ref position);
            if (position == contentType.Length)
            {
                break;
            }

            var nameStart = position;
            while (position < contentType.Length
                && contentType[position] != '='
                && contentType[position] != ';'
                && contentType[position] is not ' ' and not '\t')
            {
                position++;
            }

            var nameEnd = position;
            for (var i = nameStart; i < nameEnd; i++)
            {
                if (!IsMimeTokenCharacter(contentType[i]))
                {
                    throw FormException.BadRequest("The multipart Content-Type parameters are invalid.");
                }
            }

            SkipWhitespace(contentType, ref position);
            if (nameEnd == nameStart || position == contentType.Length || contentType[position] != '=')
            {
                throw FormException.BadRequest("The multipart Content-Type parameters are invalid.");
            }

            position++;
            SkipWhitespace(contentType, ref position);
            var value = ReadContentTypeParameter(contentType, ref position, out var quoted);
            if (contentType.AsSpan(nameStart, nameEnd - nameStart).Equals("boundary", StringComparison.OrdinalIgnoreCase))
            {
                if (boundary is not null)
                {
                    throw FormException.BadRequest("The multipart Content-Type contains more than one boundary.");
                }

                if (!quoted)
                {
                    for (var i = 0; i < value.Length; i++)
                    {
                        if (!IsMimeTokenCharacter(value[i]))
                        {
                            throw FormException.BadRequest("The multipart boundary parameter must be quoted.");
                        }
                    }
                }

                boundary = value;
            }

            SkipWhitespace(contentType, ref position);
            if (position < contentType.Length && contentType[position] != ';')
            {
                throw FormException.BadRequest("The multipart Content-Type parameters are invalid.");
            }
        }

        if (boundary is null || !IsValidBoundary(boundary))
        {
            throw FormException.BadRequest("The multipart Content-Type boundary is missing or invalid.");
        }

        return boundary;
    }

    private static string ReadContentTypeParameter(string contentType, ref int position, out bool quoted)
    {
        if (position == contentType.Length)
        {
            quoted = false;
            return string.Empty;
        }

        if (contentType[position] != '"')
        {
            quoted = false;
            var start = position;
            while (position < contentType.Length && contentType[position] != ';')
            {
                position++;
            }

            return contentType[start..position].Trim();
        }

        quoted = true;
        position++;
        var builder = new StringBuilder();
        while (position < contentType.Length)
        {
            var current = contentType[position++];
            if (current == '"')
            {
                return builder.ToString();
            }

            if (current == '\\')
            {
                if (position == contentType.Length)
                {
                    break;
                }

                current = contentType[position++];
            }

            if (current > 0x7f || current is '\r' or '\n')
            {
                throw FormException.BadRequest("The multipart boundary parameter is invalid.");
            }

            builder.Append(current);
        }

        throw FormException.BadRequest("The multipart boundary parameter has an unterminated quoted value.");
    }

    private static bool IsMimeTokenCharacter(char value)
    {
        if (value is < '!' or > '~')
        {
            return false;
        }

        return value is not '(' and not ')' and not '<' and not '>' and not '@'
            and not ',' and not ';' and not ':' and not '\\' and not '"' and not '/'
            and not '[' and not ']' and not '?' and not '=' and not '{' and not '}';
    }

    private static bool IsValidBoundary(string boundary)
    {
        if (boundary.Length is < 1 or > 70 || boundary[^1] == ' ')
        {
            return false;
        }

        for (var i = 0; i < boundary.Length; i++)
        {
            var value = boundary[i];
            if ((value is >= '0' and <= '9')
                || (value is >= 'A' and <= 'Z')
                || (value is >= 'a' and <= 'z')
                || value is '\'' or '(' or ')' or '+' or '_' or ',' or '-' or '.' or '/' or ':' or '=' or '?' or ' ')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static void SkipWhitespace(string value, ref int position)
    {
        while (position < value.Length && value[position] is ' ' or '\t')
        {
            position++;
        }
    }
}

internal sealed class FormLimitedPipeReader : PipeReader
{
    private readonly PipeReader _inner;
    private readonly int _limit;
    private ReadOnlySequence<byte> _activeBuffer;
    private long _consumedBytes;
    private bool _hasActiveRead;

    public FormLimitedPipeReader(PipeReader inner, int limit)
    {
        _inner = inner;
        _limit = limit;
    }

    public override void AdvanceTo(SequencePosition consumed)
    {
        TrackConsumed(consumed);
        _inner.AdvanceTo(consumed);
    }

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        TrackConsumed(consumed);
        _inner.AdvanceTo(consumed, examined);
    }

    public override void CancelPendingRead() => _inner.CancelPendingRead();

    public override void Complete(Exception? exception = null) => _inner.Complete(exception);

    public override ValueTask CompleteAsync(Exception? exception = null) => _inner.CompleteAsync(exception);

    public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        EnsureNoActiveRead();
        var operation = _inner.ReadAsync(cancellationToken);
        if (operation.IsCompletedSuccessfully)
        {
            return new ValueTask<ReadResult>(Validate(operation.Result));
        }

        return AwaitRead(operation);
    }

    public override bool TryRead(out ReadResult result)
    {
        EnsureNoActiveRead();
        if (!_inner.TryRead(out var innerResult))
        {
            result = default;
            return false;
        }

        result = Validate(innerResult);
        return true;
    }

    private async ValueTask<ReadResult> AwaitRead(ValueTask<ReadResult> operation) =>
        Validate(await operation.ConfigureAwait(false));

    private ReadResult Validate(ReadResult result)
    {
        var buffer = result.Buffer;
        if (buffer.Length > _limit - _consumedBytes)
        {
            _inner.AdvanceTo(buffer.End);
            throw FormException.PayloadTooLarge(_limit);
        }

        _activeBuffer = buffer;
        _hasActiveRead = true;
        return result;
    }

    private void EnsureNoActiveRead()
    {
        if (_hasActiveRead)
        {
            throw new InvalidOperationException("AdvanceTo must be called before reading again.");
        }
    }

    private void TrackConsumed(SequencePosition consumed)
    {
        if (!_hasActiveRead)
        {
            throw new InvalidOperationException("No read operation is active.");
        }

        _consumedBytes = checked(_consumedBytes + _activeBuffer.Slice(0, consumed).Length);
        _activeBuffer = default;
        _hasActiveRead = false;
    }
}
