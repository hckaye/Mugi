using Microsoft.AspNetCore.Http;

namespace Miya.Middleware;

/// <summary>
/// Origin-check CSRF middleware (no tokens). It applies only to
/// non-safe methods whose content type is form-like. JSON requests pass.
/// When <see cref="CsrfOptions.Origins"/> is omitted, the Origin host is compared to
/// the request <c>Host</c> header and its scheme must match the request scheme.
/// </summary>
public static class Csrf
{
    /// <summary>
    /// Creates CSRF middleware. Passing <see langword="null"/> uses same-origin host matching.
    /// </summary>
    /// <param name="options">Allowed origins, a validation callback, or <see langword="null"/> for defaults.</param>
    /// <returns>Middleware that rejects forged form submissions with 403.</returns>
    public static Middleware<Context> Middleware(CsrfOptions? options = null)
    {
        var compiled = new CsrfMiddleware(options ?? new CsrfOptions());
        return compiled.Invoke;
    }

    private sealed class CsrfMiddleware
    {
        private readonly string[]? _origins;
        private readonly Func<string, bool>? _validateOrigin;
        private readonly bool _sameOriginOnly;

        public CsrfMiddleware(CsrfOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            _validateOrigin = options.ValidateOrigin;
            if (options.Origins is not null)
            {
                _origins = Copy(options.Origins);
            }

            _sameOriginOnly = _origins is null && _validateOrigin is null;
        }

        public ValueTask Invoke(Context context, Handler<Context> next)
        {
            if (!RequiresCheck(context.Req.Method, context.Req.Header("Content-Type")))
            {
                return next(context);
            }

            var origin = context.Req.Header("Origin");
            if (origin is null
                || string.Equals(origin, "null", StringComparison.Ordinal)
                || !IsAllowed(origin, context))
            {
                return Forbid(context);
            }

            return next(context);
        }

        private bool IsAllowed(string origin, Context context)
        {
            if (_validateOrigin is not null && _validateOrigin(origin))
            {
                return true;
            }

            if (_origins is not null)
            {
                for (var i = 0; i < _origins.Length; i++)
                {
                    if (string.Equals(_origins[i], origin, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            return _sameOriginOnly
                && OriginMatchesRequest(origin, context.Req.Header("Host"), context.Req.IsHttps);
        }

        private static ValueTask Forbid(Context context)
        {
            context.Status(StatusCodes.Status403Forbidden);
            return context.Text("Forbidden");
        }

        private static bool RequiresCheck(string method, string? contentType)
        {
            if (string.Equals(method, "GET", StringComparison.Ordinal)
                || string.Equals(method, "HEAD", StringComparison.Ordinal)
                || string.Equals(method, "OPTIONS", StringComparison.Ordinal))
            {
                return false;
            }

            return IsFormLike(contentType);
        }

        private static bool IsFormLike(string? contentType)
        {
            if (string.IsNullOrEmpty(contentType))
            {
                return true;
            }

            var mediaType = contentType.AsSpan();
            var separator = mediaType.IndexOf(';');
            if (separator >= 0)
            {
                mediaType = mediaType[..separator];
            }

            mediaType = mediaType.Trim();
            return mediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
                || mediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
                || mediaType.Equals("text/plain", StringComparison.OrdinalIgnoreCase);
        }

        private static bool OriginMatchesRequest(string origin, string? hostHeader, bool isHttps)
        {
            if (string.IsNullOrEmpty(hostHeader))
            {
                return false;
            }

            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            {
                return false;
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var expectedScheme = isHttps ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
            return string.Equals(uri.Scheme, expectedScheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(uri.Authority, hostHeader, StringComparison.OrdinalIgnoreCase);
        }

        private static string[] Copy(IReadOnlyList<string> origins)
        {
            var copy = new string[origins.Count];
            for (var i = 0; i < origins.Count; i++)
            {
                var origin = origins[i];
                if (string.IsNullOrEmpty(origin))
                {
                    throw new ArgumentException("Origins cannot contain null or empty entries.", nameof(CsrfOptions.Origins));
                }

                foreach (var character in origin)
                {
                    if (character is '\r' or '\n' or '\0' or '\u007f'
                        || (character < ' ' && character != '\t'))
                    {
                        throw new ArgumentException("An allowed origin contains an invalid character.", nameof(CsrfOptions.Origins));
                    }
                }

                copy[i] = origin;
            }

            return copy;
        }
    }
}

/// <summary>
/// Options for <see cref="Csrf.Middleware"/>.
/// When <see cref="Origins"/> and <see cref="ValidateOrigin"/> are both omitted, only
/// same-origin form submissions are allowed. The Origin scheme and host must match the request.
/// </summary>
public sealed class CsrfOptions
{
    /// <summary>
    /// Gets the allowed origins. <see langword="null"/> selects same-origin host matching
    /// when <see cref="ValidateOrigin"/> is also omitted. Matching is case-sensitive and exact.
    /// </summary>
    public IReadOnlyList<string>? Origins { get; init; }

    /// <summary>
    /// Gets an optional callback that can allow an origin. Combined with <see cref="Origins"/>
    /// using OR; it does not fall back to same-origin matching.
    /// </summary>
    public Func<string, bool>? ValidateOrigin { get; init; }
}
