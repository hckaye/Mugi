using System.Text;
using Mugi;

namespace Mugi.Jwt;

/// <summary>Exposes the verified JWT payload on a custom Mugi context.</summary>
public interface IJwtContext
{
    /// <summary>Gets or sets the verified JWT payload for the current request.</summary>
    JwtPayload? Jwt { get; set; }
}

/// <summary>Creates bearer-token authentication middleware.</summary>
public static class JwtAuth
{
    /// <summary>Creates middleware that validates a bearer token before calling the next handler.</summary>
    public static Middleware<Context> Middleware(JwtAuthOptions options)
    {
        ValidateOptions(options);
        var middleware = new JwtAuthMiddleware(options);
        return middleware.Invoke;
    }

    /// <summary>
    /// Creates middleware that validates a bearer token and stores its payload on a custom context.
    /// </summary>
    public static Middleware<TContext> Middleware<TContext>(JwtAuthOptions options)
        where TContext : Context, IJwtContext, new()
    {
        ValidateOptions(options);
        var middleware = new JwtAuthMiddleware<TContext>(options);
        return middleware.Invoke;
    }

    private static void ValidateOptions(JwtAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Key);
        ArgumentNullException.ThrowIfNull(options.Realm);
        if (options.Validation?.ClockSkew < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "ClockSkew cannot be negative.");
        }
    }
}

/// <summary>Configures bearer-token authentication middleware.</summary>
public sealed class JwtAuthOptions
{
    /// <summary>Gets the key used to verify bearer tokens.</summary>
    public required JwtKey Key { get; init; }

    /// <summary>Gets the registered-claim validation settings.</summary>
    public JwtValidation? Validation { get; init; }

    /// <summary>Gets the authentication realm included in challenge responses.</summary>
    public string Realm { get; init; } = "Restricted";
}

internal abstract class JwtAuthMiddlewareBase
{
    protected JwtAuthMiddlewareBase(JwtAuthOptions options)
    {
        Key = options.Key;
        Validation = options.Validation;
        MissingChallenge = CreateChallenge(options.Realm, invalidToken: false);
        InvalidChallenge = CreateChallenge(options.Realm, invalidToken: true);
    }

    protected JwtKey Key { get; }

    protected JwtValidation? Validation { get; }

    protected string MissingChallenge { get; }

    protected string InvalidChallenge { get; }

    protected static bool TryGetBearerToken(string authorization, out ReadOnlySpan<char> token)
    {
        token = default;
        var value = authorization.AsSpan().Trim();
        var separator = value.IndexOfAny(' ', '\t');
        if (separator <= 0
            || !value[..separator].Equals("Bearer", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var tokenStart = separator;
        while (tokenStart < value.Length && value[tokenStart] is ' ' or '\t')
        {
            tokenStart++;
        }

        if (tokenStart == value.Length)
        {
            return false;
        }

        token = value[tokenStart..].TrimEnd();
        return !token.IsEmpty;
    }

    protected static ValueTask Reject(Context context, string challenge)
    {
        context.Status(401);
        context.Header("WWW-Authenticate", challenge);
        return ValueTask.CompletedTask;
    }

    private static string CreateChallenge(string realm, bool invalidToken)
    {
        var builder = new StringBuilder(realm.Length + 48);
        builder.Append("Bearer realm=\"");
        for (var i = 0; i < realm.Length; i++)
        {
            var character = realm[i];
            if (character < 0x20 || character == 0x7F)
            {
                throw new ArgumentException("Realm cannot contain HTTP control characters.", nameof(realm));
            }

            if (character is '"' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(character);
        }

        builder.Append('"');
        if (invalidToken)
        {
            builder.Append(", error=\"invalid_token\"");
        }

        return builder.ToString();
    }
}

internal sealed class JwtAuthMiddleware : JwtAuthMiddlewareBase
{
    internal JwtAuthMiddleware(JwtAuthOptions options)
        : base(options)
    {
    }

    internal ValueTask Invoke(Context context, Handler<Context> next)
    {
        var authorization = context.Req.Header("Authorization");
        if (authorization is null)
        {
            return Reject(context, MissingChallenge);
        }

        if (!TryGetBearerToken(authorization, out var token))
        {
            return Reject(context, InvalidChallenge);
        }

        var result = global::Mugi.Jwt.Jwt.VerifyCore(token, Key, Validation);
        return result.IsValid ? next(context) : Reject(context, InvalidChallenge);
    }
}

internal sealed class JwtAuthMiddleware<TContext> : JwtAuthMiddlewareBase
    where TContext : Context, IJwtContext, new()
{
    internal JwtAuthMiddleware(JwtAuthOptions options)
        : base(options)
    {
    }

    internal ValueTask Invoke(TContext context, Handler<TContext> next)
    {
        context.Jwt = null;
        var authorization = context.Req.Header("Authorization");
        if (authorization is null)
        {
            return Reject(context, MissingChallenge);
        }

        if (!TryGetBearerToken(authorization, out var token))
        {
            return Reject(context, InvalidChallenge);
        }

        var result = global::Mugi.Jwt.Jwt.VerifyCore(token, Key, Validation);
        if (!result.IsValid)
        {
            return Reject(context, InvalidChallenge);
        }

        context.Jwt = result.Payload;
        return next(context);
    }
}
