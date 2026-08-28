using System.Security.Cryptography;

namespace Miya.Middleware;

/// <summary>
/// Configures generated entity tags.
/// </summary>
public sealed class ETagOptions
{
    /// <summary>
    /// Gets a value indicating whether generated entity tags are weak.
    /// </summary>
    public bool Weak { get; init; }
}

/// <summary>
/// Adds entity tags to buffered GET and HEAD responses.
/// </summary>
public static class ETag
{
    /// <summary>
    /// Creates entity tag middleware.
    /// </summary>
    /// <remarks>
    /// Register this middleware before compression middleware so the entity tag identifies the compressed
    /// representation selected for the request.
    /// </remarks>
    /// <param name="options">Entity tag settings, or <see langword="null"/> for the defaults.</param>
    /// <returns>The configured middleware.</returns>
    public static Middleware<Context> Middleware(ETagOptions? options = null)
    {
        options ??= new ETagOptions();
        return new ETagMiddleware(options.Weak).Invoke;
    }

    private sealed class ETagMiddleware
    {
        private readonly bool _weak;

        internal ETagMiddleware(bool weak)
        {
            _weak = weak;
        }

        internal async ValueTask Invoke(Context context, Handler<Context> next)
        {
            await next(context).ConfigureAwait(false);

            var method = context.Req.Method;
            if ((!string.Equals(method, "GET", StringComparison.Ordinal)
                    && !string.Equals(method, "HEAD", StringComparison.Ordinal))
                || context.BufferedResponseStatusCode != 200
                || !context.TryGetBufferedResponse(out var body))
            {
                return;
            }

            var entityTag = context.GetResponseHeader("ETag");
            if (entityTag is null)
            {
                entityTag = CreateEntityTag(body.Span, _weak);
                context.Header("ETag", entityTag);
            }

            var ifNoneMatch = context.Req.Header("If-None-Match");
            if (ifNoneMatch is null || !Matches(ifNoneMatch.AsSpan(), entityTag.AsSpan()))
            {
                return;
            }

            context.ReplaceBufferedResponse(ReadOnlyMemory<byte>.Empty);
            context.Status(304);
        }

        private static string CreateEntityTag(ReadOnlySpan<byte> body, bool weak)
        {
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(body, hash);
            var value = Convert.ToBase64String(hash[..20])
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
            return weak ? string.Concat("W/\"", value, "\"") : string.Concat('"', value, '"');
        }

        private static bool Matches(ReadOnlySpan<char> list, ReadOnlySpan<char> current)
        {
            if (!TryGetOpaqueTag(current, out var currentOpaque))
            {
                return false;
            }

            var position = 0;
            var matched = false;
            var firstItem = true;
            while (position < list.Length)
            {
                SkipWhitespace(list, ref position);
                if (position >= list.Length)
                {
                    return false;
                }

                if (list[position] == '*')
                {
                    position++;
                    SkipWhitespace(list, ref position);
                    return firstItem && position == list.Length;
                }

                var start = position;
                if (position + 2 <= list.Length
                    && list[position] == 'W'
                    && list[position + 1] == '/')
                {
                    position += 2;
                }

                if (position >= list.Length || list[position] != '"')
                {
                    return false;
                }

                position++;
                while (position < list.Length && list[position] != '"')
                {
                    var character = list[position];
                    if (character < '\u0021' || character == '\u007f')
                    {
                        return false;
                    }

                    position++;
                }

                if (position >= list.Length)
                {
                    return false;
                }

                position++;
                firstItem = false;
                var candidate = list[start..position];
                if (TryGetOpaqueTag(candidate, out var candidateOpaque)
                    && candidateOpaque.SequenceEqual(currentOpaque))
                {
                    matched = true;
                }

                SkipWhitespace(list, ref position);
                if (position == list.Length)
                {
                    return matched;
                }

                if (list[position] != ',')
                {
                    return false;
                }

                position++;
                if (position == list.Length)
                {
                    return false;
                }
            }

            return matched;
        }

        private static bool TryGetOpaqueTag(ReadOnlySpan<char> entityTag, out ReadOnlySpan<char> opaque)
        {
            if (entityTag.StartsWith("W/", StringComparison.Ordinal))
            {
                entityTag = entityTag[2..];
            }

            if (entityTag.Length < 2 || entityTag[0] != '"' || entityTag[^1] != '"')
            {
                opaque = default;
                return false;
            }

            opaque = entityTag[1..^1];
            return true;
        }

        private static void SkipWhitespace(ReadOnlySpan<char> value, ref int position)
        {
            while (position < value.Length && value[position] is ' ' or '\t')
            {
                position++;
            }
        }
    }
}
