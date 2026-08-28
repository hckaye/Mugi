using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace Miya.Middleware;

/// <summary>
/// Cross-origin resource sharing middleware. Register it before routing so preflight
/// <c>OPTIONS</c> requests never reach the implicit router <c>Allow</c> response.
/// </summary>
public static class Cors
{
    /// <summary>
    /// Creates CORS middleware from the given options. Options are validated immediately.
    /// </summary>
    /// <param name="options">Allowed origins, methods, headers, and related flags.</param>
    /// <returns>Middleware that emits CORS headers and short-circuits preflight requests.</returns>
    public static Middleware<Context> Middleware(CorsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var compiled = new CorsMiddleware(options);
        return compiled.Invoke;
    }

    private sealed class CorsMiddleware
    {
        private readonly string[] _origins;
        private readonly bool _allowAny;
        private readonly bool _credentials;
        private readonly bool _appendVary;
        private readonly string? _allowMethods;
        private readonly string? _allowHeaders;
        private readonly string? _exposeHeaders;
        private readonly string? _maxAge;

        public CorsMiddleware(CorsOptions options)
        {
            _origins = Copy(options.Origins, nameof(options.Origins), allowWildcard: true);
            _allowAny = ContainsWildcard(_origins);
            _credentials = options.Credentials;
            if (_allowAny && _credentials)
            {
                throw new ArgumentException(
                    "Origins cannot contain '*' when Credentials is true. The CORS spec forbids Access-Control-Allow-Origin: * with credentials.",
                    nameof(options));
            }

            _appendVary = !_allowAny;
            _allowMethods = Join(Copy(options.Methods, nameof(options.Methods), allowWildcard: false));
            var headers = Copy(options.Headers, nameof(options.Headers), allowWildcard: false);
            _allowHeaders = headers.Length == 0 ? null : Join(headers);
            var expose = Copy(options.ExposeHeaders, nameof(options.ExposeHeaders), allowWildcard: false);
            _exposeHeaders = expose.Length == 0 ? null : Join(expose);

            if (options.MaxAge is { } maxAge)
            {
                if (maxAge < TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(options),
                        "MaxAge cannot be negative.");
                }

                _maxAge = ((long)maxAge.TotalSeconds).ToString(CultureInfo.InvariantCulture);
            }
        }

        public ValueTask Invoke(Context context, Handler<Context> next)
        {
            var origin = context.Req.Header("Origin");
            if (origin is null)
            {
                return next(context);
            }

            if (!TryMatch(origin, out var allowOrigin))
            {
                return next(context);
            }

            if (string.Equals(context.Req.Method, "OPTIONS", StringComparison.Ordinal)
                && context.Req.Header("Access-Control-Request-Method") is not null)
            {
                WritePreflight(context, allowOrigin);
                return ValueTask.CompletedTask;
            }

            WriteActual(context, allowOrigin);
            return next(context);
        }

        private bool TryMatch(string origin, out string allowOrigin)
        {
            if (_allowAny)
            {
                allowOrigin = "*";
                return true;
            }

            for (var i = 0; i < _origins.Length; i++)
            {
                if (string.Equals(_origins[i], origin, StringComparison.Ordinal))
                {
                    allowOrigin = origin;
                    return true;
                }
            }

            allowOrigin = null!;
            return false;
        }

        private void WritePreflight(Context context, string allowOrigin)
        {
            context.Status(StatusCodes.Status204NoContent);
            context.SetEmptyBody();
            context.Header("Access-Control-Allow-Origin", allowOrigin);
            if (_allowMethods is not null)
            {
                context.Header("Access-Control-Allow-Methods", _allowMethods);
            }

            if (_allowHeaders is not null)
            {
                context.Header("Access-Control-Allow-Headers", _allowHeaders);
            }
            else
            {
                var requested = context.Req.Header("Access-Control-Request-Headers");
                if (!string.IsNullOrEmpty(requested) && IsSafeHeaderValue(requested))
                {
                    context.Header("Access-Control-Allow-Headers", requested);
                }
            }

            if (_maxAge is not null)
            {
                context.Header("Access-Control-Max-Age", _maxAge);
            }

            if (_credentials)
            {
                context.Header("Access-Control-Allow-Credentials", "true");
            }

            if (_appendVary)
            {
                context.AppendHeader(
                    "Vary",
                    "Origin, Access-Control-Request-Method, Access-Control-Request-Headers");
            }
        }

        private void WriteActual(Context context, string allowOrigin)
        {
            context.Header("Access-Control-Allow-Origin", allowOrigin);
            if (_credentials)
            {
                context.Header("Access-Control-Allow-Credentials", "true");
            }

            if (_exposeHeaders is not null)
            {
                context.Header("Access-Control-Expose-Headers", _exposeHeaders);
            }

            if (_appendVary)
            {
                context.AppendHeader("Vary", "Origin");
            }
        }

        private static string[] Copy(IReadOnlyList<string> values, string paramName, bool allowWildcard)
        {
            ArgumentNullException.ThrowIfNull(values);
            var copy = new string[values.Count];
            for (var i = 0; i < values.Count; i++)
            {
                var value = values[i];
                if (value is null)
                {
                    throw new ArgumentException($"'{paramName}' cannot contain null entries.", paramName);
                }

                if (value.Length == 0)
                {
                    throw new ArgumentException($"'{paramName}' cannot contain empty entries.", paramName);
                }

                if (allowWildcard && value == "*")
                {
                    copy[i] = value;
                    continue;
                }

                if (allowWildcard)
                {
                    ValidateHeaderValue(value, paramName);
                }
                else if (paramName is nameof(CorsOptions.Methods))
                {
                    ValidateMethod(value, paramName);
                }
                else
                {
                    ValidateHeaderName(value, paramName);
                }

                copy[i] = value;
            }

            return copy;
        }

        private static bool ContainsWildcard(string[] origins)
        {
            for (var i = 0; i < origins.Length; i++)
            {
                if (origins[i] == "*")
                {
                    return true;
                }
            }

            return false;
        }

        private static string? Join(string[] values)
        {
            if (values.Length == 0)
            {
                return null;
            }

            return string.Join(", ", values);
        }

        private static void ValidateMethod(string method, string paramName)
        {
            foreach (var character in method)
            {
                if (character is < '!' or > '~'
                    || character is '(' or ')' or '<' or '>' or '@' or ',' or ';' or ':' or '\\'
                        or '"' or '/' or '[' or ']' or '?' or '=' or '{' or '}')
                {
                    throw new ArgumentException("A CORS method contains an invalid character.", paramName);
                }
            }
        }

        private static void ValidateHeaderName(string name, string paramName)
        {
            foreach (var character in name)
            {
                if (character is < '!' or > '~'
                    || character is '(' or ')' or '<' or '>' or '@' or ',' or ';' or ':' or '\\'
                        or '"' or '/' or '[' or ']' or '?' or '=' or '{' or '}')
                {
                    throw new ArgumentException("A CORS header name contains an invalid character.", paramName);
                }
            }
        }

        private static void ValidateHeaderValue(string value, string paramName)
        {
            if (!IsSafeHeaderValue(value))
            {
                throw new ArgumentException("A CORS origin contains an invalid character.", paramName);
            }
        }

        private static bool IsSafeHeaderValue(string value)
        {
            foreach (var character in value)
            {
                if (character is '\r' or '\n' or '\0' or '\u007f'
                    || (character < ' ' && character != '\t'))
                {
                    return false;
                }
            }

            return true;
        }
    }
}

/// <summary>
/// Options for <see cref="Cors.Middleware"/>.
/// </summary>
public sealed class CorsOptions
{
    /// <summary>
    /// Gets the allowed origins. Matching is case-sensitive and exact. A single <c>*</c>
    /// allows any origin and emits a literal <c>*</c> without <c>Vary</c>.
    /// </summary>
    public IReadOnlyList<string> Origins { get; init; } = [];

    /// <summary>
    /// Gets the methods listed in <c>Access-Control-Allow-Methods</c> on preflight responses.
    /// </summary>
    public IReadOnlyList<string> Methods { get; init; } = ["GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS"];

    /// <summary>
    /// Gets the request headers listed in <c>Access-Control-Allow-Headers</c>.
    /// An empty list echoes <c>Access-Control-Request-Headers</c> on preflight.
    /// </summary>
    public IReadOnlyList<string> Headers { get; init; } = [];

    /// <summary>
    /// Gets the headers listed in <c>Access-Control-Expose-Headers</c> on actual responses.
    /// </summary>
    public IReadOnlyList<string> ExposeHeaders { get; init; } = [];

    /// <summary>
    /// Gets whether credentialed requests are allowed. Cannot be combined with Origins <c>*</c>.
    /// </summary>
    public bool Credentials { get; init; }

    /// <summary>
    /// Gets the preflight cache duration written as <c>Access-Control-Max-Age</c> in seconds.
    /// </summary>
    public TimeSpan? MaxAge { get; init; }
}
