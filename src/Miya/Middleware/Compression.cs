using System.Buffers;
using System.IO.Compression;
using Microsoft.AspNetCore.Http;

namespace Miya.Middleware;

/// <summary>
/// Configures response compression.
/// </summary>
public sealed class CompressionOptions
{
    /// <summary>
    /// Gets the minimum response body size eligible for compression.
    /// </summary>
    public int MinBytes { get; init; } = 1024;

    /// <summary>
    /// Gets the compression level.
    /// </summary>
    public CompressionLevel Level { get; init; } = CompressionLevel.Fastest;
}

/// <summary>
/// Compresses buffered response bodies with Brotli or gzip.
/// </summary>
public static class Compression
{
    /// <summary>
    /// Creates response compression middleware.
    /// </summary>
    /// <remarks>
    /// Register ETag middleware before this middleware so ETags are calculated from the compressed bytes.
    /// Responses promoted to streaming are not compressed.
    /// </remarks>
    /// <param name="options">Compression settings, or <see langword="null"/> for the defaults.</param>
    /// <returns>The configured middleware.</returns>
    public static Middleware<Context> Middleware(CompressionOptions? options = null)
    {
        options ??= new CompressionOptions();
        ArgumentOutOfRangeException.ThrowIfNegative(options.MinBytes);
        if (options.Level is not CompressionLevel.Optimal
            and not CompressionLevel.Fastest
            and not CompressionLevel.NoCompression
            and not CompressionLevel.SmallestSize)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The compression level is not valid.");
        }

        return new CompressionMiddleware(options.MinBytes, options.Level).Invoke;
    }

    private sealed class CompressionMiddleware
    {
        private readonly CompressionLevel _level;
        private readonly int _minBytes;

        internal CompressionMiddleware(int minBytes, CompressionLevel level)
        {
            _minBytes = minBytes;
            _level = level;
        }

        internal async ValueTask Invoke(Context context, Handler<Context> next)
        {
            await next(context).ConfigureAwait(false);

            if (!context.TryGetBufferedResponse(out var body)
                || !StatusAllowsBody(context.BufferedResponseStatusCode)
                || context.GetResponseHeader("Content-Encoding") is not null)
            {
                return;
            }

            var acceptEncoding = context.Req.Header("Accept-Encoding");
            if (acceptEncoding is null)
            {
                return;
            }

            if (!TrySelectEncoding(
                    acceptEncoding.AsSpan(),
                    out var encoding,
                    out var identityAccepted))
            {
                if (!identityAccepted)
                {
                    context.Status(StatusCodes.Status406NotAcceptable);
                    context.SetEmptyBody();
                    AppendVaryAcceptEncoding(context);
                }

                return;
            }

            if (context.GetResponseHeader("Content-Range") is not null
                || context.GetResponseHeader("ETag") is not null
                || !IsCompressibleContentType(context.GetResponseHeader("Content-Type")))
            {
                if (!identityAccepted)
                {
                    context.Status(StatusCodes.Status406NotAcceptable);
                    context.SetEmptyBody();
                    AppendVaryAcceptEncoding(context);
                }

                return;
            }

            if (body.Length < _minBytes && identityAccepted)
            {
                return;
            }

            using var compressed = new PooledCompressionStream(body.Length);
            if (encoding == ResponseEncoding.Brotli)
            {
                using var stream = new BrotliStream(compressed, _level, leaveOpen: true);
                stream.Write(body.Span);
            }
            else
            {
                using var stream = new GZipStream(compressed, _level, leaveOpen: true);
                stream.Write(body.Span);
            }

            if (compressed.WrittenCount >= body.Length && identityAccepted)
            {
                return;
            }

            context.ReplaceBufferedResponse(compressed.WrittenMemory);
            context.Header("Content-Encoding", encoding == ResponseEncoding.Brotli ? "br" : "gzip");
            AppendVaryAcceptEncoding(context);
        }

        private static void AppendVaryAcceptEncoding(Context context)
        {
            var vary = context.GetResponseHeader("Vary");
            if (vary is not null
                && (ContainsToken(vary.AsSpan(), "Accept-Encoding")
                    || ContainsToken(vary.AsSpan(), "*")))
            {
                return;
            }

            context.AppendHeader("Vary", "Accept-Encoding");
        }

        private static bool IsCompressibleContentType(string? contentType)
        {
            if (contentType is null)
            {
                return false;
            }

            var value = contentType.AsSpan();
            var separator = value.IndexOf(';');
            if (separator >= 0)
            {
                value = value[..separator];
            }

            value = TrimWhitespace(value);
            return value.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                || value.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                || value.Equals("application/javascript", StringComparison.OrdinalIgnoreCase)
                || value.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase)
                || value.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
                || value.Equals("application/wasm", StringComparison.OrdinalIgnoreCase);
        }

        private static bool StatusAllowsBody(int statusCode) =>
            statusCode is < 100 or >= 200
            && statusCode is not 204 and not 304;

        private static bool TrySelectEncoding(
            ReadOnlySpan<char> header,
            out ResponseEncoding encoding,
            out bool identityAccepted)
        {
            var brotliQuality = -1;
            var gzipQuality = -1;
            var wildcardQuality = -1;
            var brotliSeen = false;
            var gzipSeen = false;
            var identitySeen = false;
            var identityQuality = -1;
            var wildcardSeen = false;
            var position = 0;

            while (position < header.Length)
            {
                var comma = header[position..].IndexOf(',');
                var end = comma < 0 ? header.Length : position + comma;
                var item = TrimWhitespace(header[position..end]);
                if (item.IsEmpty || !TryParseCoding(item, out var coding, out var quality))
                {
                    encoding = default;
                    identityAccepted = true;
                    return false;
                }

                if (coding.Equals("br", StringComparison.OrdinalIgnoreCase))
                {
                    brotliSeen = true;
                    brotliQuality = Math.Max(brotliQuality, quality);
                }
                else if (coding.Equals("gzip", StringComparison.OrdinalIgnoreCase))
                {
                    gzipSeen = true;
                    gzipQuality = Math.Max(gzipQuality, quality);
                }
                else if (coding.Equals("*", StringComparison.Ordinal))
                {
                    wildcardSeen = true;
                    wildcardQuality = Math.Max(wildcardQuality, quality);
                }
                else if (coding.Equals("identity", StringComparison.OrdinalIgnoreCase))
                {
                    identitySeen = true;
                    identityQuality = Math.Max(identityQuality, quality);
                }

                if (comma < 0)
                {
                    break;
                }

                position = end + 1;
                if (position == header.Length)
                {
                    encoding = default;
                    identityAccepted = true;
                    return false;
                }
            }

            identityAccepted = identitySeen
                ? identityQuality > 0
                : !wildcardSeen || wildcardQuality > 0;

            if (!brotliSeen)
            {
                brotliQuality = wildcardQuality;
            }

            if (!gzipSeen)
            {
                gzipQuality = wildcardQuality;
            }

            if (brotliQuality <= 0 && gzipQuality <= 0)
            {
                encoding = default;
                return false;
            }

            encoding = brotliQuality >= gzipQuality ? ResponseEncoding.Brotli : ResponseEncoding.Gzip;
            return true;
        }

        private static bool TryParseCoding(
            ReadOnlySpan<char> item,
            out ReadOnlySpan<char> coding,
            out int quality)
        {
            var semicolon = item.IndexOf(';');
            coding = TrimWhitespace(semicolon < 0 ? item : item[..semicolon]);
            if (coding.IsEmpty || !IsToken(coding))
            {
                quality = 0;
                return false;
            }

            quality = 1000;
            if (semicolon < 0)
            {
                return true;
            }

            var parameter = TrimWhitespace(item[(semicolon + 1)..]);
            var equals = parameter.IndexOf('=');
            if (equals < 0
                || parameter[(equals + 1)..].IndexOf(';') >= 0
                || !TrimWhitespace(parameter[..equals]).Equals("q", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return TryParseQuality(TrimWhitespace(parameter[(equals + 1)..]), out quality);
        }

        private static bool TryParseQuality(ReadOnlySpan<char> value, out int quality)
        {
            quality = 0;
            if (value.IsEmpty || (value[0] != '0' && value[0] != '1'))
            {
                return false;
            }

            if (value.Length == 1)
            {
                quality = value[0] == '1' ? 1000 : 0;
                return true;
            }

            if (value[1] != '.' || value.Length > 5)
            {
                return false;
            }

            var fraction = 0;
            var multiplier = 100;
            for (var index = 2; index < value.Length; index++)
            {
                var digit = value[index] - '0';
                if ((uint)digit > 9 || (value[0] == '1' && digit != 0))
                {
                    return false;
                }

                fraction += digit * multiplier;
                multiplier /= 10;
            }

            quality = value[0] == '1' ? 1000 : fraction;
            return true;
        }

        private static bool ContainsToken(ReadOnlySpan<char> list, ReadOnlySpan<char> expected)
        {
            var position = 0;
            while (position < list.Length)
            {
                var comma = list[position..].IndexOf(',');
                var end = comma < 0 ? list.Length : position + comma;
                if (TrimWhitespace(list[position..end]).Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (comma < 0)
                {
                    break;
                }

                position = end + 1;
            }

            return false;
        }

        private static bool IsToken(ReadOnlySpan<char> value)
        {
            foreach (var character in value)
            {
                if (character is <= ' ' or >= '\u007f'
                    || character is '(' or ')' or '<' or '>' or '@' or ',' or ';' or ':' or '\\'
                        or '"' or '/' or '[' or ']' or '?' or '=' or '{' or '}')
                {
                    return false;
                }
            }

            return true;
        }

        private static ReadOnlySpan<char> TrimWhitespace(ReadOnlySpan<char> value)
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

    private enum ResponseEncoding
    {
        Brotli,
        Gzip,
    }

    private sealed class PooledCompressionStream : Stream
    {
        private byte[]? _buffer;
        private int _written;

        internal PooledCompressionStream(int capacity)
        {
            _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(capacity, 256));
        }

        internal int WrittenCount => _written;

        internal ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _written;

        public override long Position
        {
            get => _written;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (offset > buffer.Length - count)
            {
                throw new ArgumentException("The offset and count are outside the buffer.");
            }

            Write(buffer.AsSpan(offset, count));
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacity(buffer.Length);
            buffer.CopyTo(_buffer.AsSpan(_written));
            _written += buffer.Length;
        }

        protected override void Dispose(bool disposing)
        {
            var buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            _written = 0;
            base.Dispose(disposing);
        }

        private void EnsureCapacity(int additionalBytes)
        {
            var buffer = _buffer ?? throw new ObjectDisposedException(nameof(PooledCompressionStream));
            var required = checked(_written + additionalBytes);
            if (required <= buffer.Length)
            {
                return;
            }

            var replacement = ArrayPool<byte>.Shared.Rent(Math.Max(required, checked(buffer.Length * 2)));
            buffer.AsSpan(0, _written).CopyTo(replacement);
            _buffer = replacement;
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
