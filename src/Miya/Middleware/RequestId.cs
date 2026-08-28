using System.Security.Cryptography;

namespace Miya.Middleware;

/// <summary>
/// Middleware that assigns a request identifier, echoing a trusted incoming header or generating a new value.
/// </summary>
public static class RequestId
{
    private const string DefaultHeaderName = "X-Request-Id";
    private const int MaxTrustedLength = 128;

    /// <summary>
    /// Creates middleware that sets the request identifier as a response header before calling next.
    /// </summary>
    /// <param name="options">Optional header name and incoming-header policy.</param>
    /// <returns>Middleware written against <see cref="Context"/>.</returns>
    public static Middleware<Context> Middleware(RequestIdOptions? options = null)
    {
        var headerName = ResolveHeaderName(options);
        var trustIncoming = options?.TrustIncoming ?? true;
        return (context, next) =>
        {
            Assign(context, headerName, trustIncoming);
            return next(context);
        };
    }

    /// <summary>
    /// Creates middleware that sets the request identifier as a response header and stores it on
    /// <see cref="IRequestIdContext.RequestId"/> before calling next.
    /// </summary>
    /// <param name="options">Optional header name and incoming-header policy.</param>
    /// <typeparam name="TContext">A context type that can store the request identifier.</typeparam>
    /// <returns>Middleware written against <typeparamref name="TContext"/>.</returns>
    public static Middleware<TContext> Middleware<TContext>(RequestIdOptions? options = null)
        where TContext : Context, IRequestIdContext, new()
    {
        var headerName = ResolveHeaderName(options);
        var trustIncoming = options?.TrustIncoming ?? true;
        return (context, next) =>
        {
            context.RequestId = Assign(context, headerName, trustIncoming);
            return next(context);
        };
    }

    private static string ResolveHeaderName(RequestIdOptions? options)
    {
        if (options is null)
        {
            return DefaultHeaderName;
        }

        var headerName = options.HeaderName;
        ArgumentException.ThrowIfNullOrEmpty(headerName);
        Context.ThrowIfInvalidUserHeader(headerName, "0");
        return headerName;
    }

    private static string Assign(Context context, string headerName, bool trustIncoming)
    {
        var id = trustIncoming ? AcceptOrGenerate(context.Req.Header(headerName)) : Generate();
        context.Header(headerName, id);
        return id;
    }

    private static string AcceptOrGenerate(string? incoming) =>
        incoming is not null && IsTrusted(incoming) ? incoming : Generate();

    private static bool IsTrusted(string value)
    {
        var length = value.Length;
        if (length is < 1 or > MaxTrustedLength)
        {
            return false;
        }

        for (var index = 0; index < length; index++)
        {
            var character = value[index];
            if (character is (>= '0' and <= '9')
                or (>= 'A' and <= 'Z')
                or (>= 'a' and <= 'z')
                or '.' or '_' or '-')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static string Generate()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }
}

/// <summary>
/// Options for <see cref="RequestId"/>.
/// </summary>
public sealed class RequestIdOptions
{
    /// <summary>
    /// Gets the request and response header that carries the identifier. The default is <c>X-Request-Id</c>.
    /// </summary>
    public string HeaderName { get; init; } = "X-Request-Id";

    /// <summary>
    /// Gets whether a well-formed incoming identifier is reused. The default is <see langword="true"/>.
    /// </summary>
    public bool TrustIncoming { get; init; } = true;
}
