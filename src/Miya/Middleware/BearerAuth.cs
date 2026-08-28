using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Miya.Middleware;

/// <summary>
/// HTTP Bearer token authentication middleware. JWT verification is not performed here;
/// token-as-JWT authentication lives in Miya.Jwt. Failed requests do not call the next handler.
/// </summary>
public static class BearerAuth
{
    internal const int ComparisonDigestSize = HMACSHA256.HashSizeInBytes;
    private const int MaximumStackTokenBytes = 256;

    /// <summary>
    /// Creates Bearer authentication middleware from the given options. Options are validated immediately.
    /// </summary>
    /// <param name="options">A fixed token or a validation callback, and the challenge realm.</param>
    /// <returns>Middleware that requires a valid <c>Authorization: Bearer</c> header.</returns>
    public static Middleware<Context> Middleware(BearerAuthOptions options)
    {
        var state = State.Create(options);
        return (context, next) => Invoke(context, next, state, assignUser: false);
    }

    /// <summary>
    /// Creates Bearer authentication middleware that stores the validated token string on
    /// <see cref="IAuthContext.AuthUser"/>.
    /// </summary>
    /// <typeparam name="TContext">A context type that implements <see cref="IAuthContext"/>.</typeparam>
    /// <param name="options">A fixed token or a validation callback, and the challenge realm.</param>
    /// <returns>Middleware that requires a valid <c>Authorization: Bearer</c> header.</returns>
    public static Middleware<TContext> Middleware<TContext>(BearerAuthOptions options)
        where TContext : Context, IAuthContext, new()
    {
        var state = State.Create(options);
        return (context, next) => Invoke(context, next, state, assignUser: true);
    }

    private static ValueTask Invoke<TContext>(
        TContext context,
        Handler<TContext> next,
        State state,
        bool assignUser)
        where TContext : Context
    {
        var header = context.Req.Header("Authorization");
        if (string.IsNullOrEmpty(header))
        {
            return Reject(context, state.MissingChallenge, StatusCodes.Status401Unauthorized, "Unauthorized");
        }

        if (!TryGetToken(header, out var token, out var malformed))
        {
            if (malformed)
            {
                return Reject(
                    context,
                    state.InvalidRequestChallenge,
                    StatusCodes.Status400BadRequest,
                    "Bad Request");
            }

            return Reject(context, state.MissingChallenge, StatusCodes.Status401Unauthorized, "Unauthorized");
        }

        if (!state.Matches(token))
        {
            return Reject(context, state.InvalidTokenChallenge, StatusCodes.Status401Unauthorized, "Unauthorized");
        }

        if (assignUser)
        {
            ((IAuthContext)context).AuthUser = token;
        }

        return next(context);
    }

    private static ValueTask Reject(Context context, string challenge, int status, string body)
    {
        context.Status(status);
        context.Header("WWW-Authenticate", challenge);
        return context.Text(body);
    }

    private static bool TryGetToken(string header, out string token, out bool malformed)
    {
        token = "";
        malformed = false;
        if (header.Length < 6
            || !header.AsSpan(0, 6).Equals("Bearer", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (header.Length == 6 || header[6] != ' ')
        {
            malformed = true;
            return false;
        }

        var raw = header.AsSpan(7);
        if (raw.IsEmpty || !IsB64Token(raw))
        {
            malformed = true;
            return false;
        }

        token = raw.ToString();
        return true;
    }

    private static bool IsB64Token(ReadOnlySpan<char> token)
    {
        if (token.IsEmpty)
        {
            return false;
        }

        var index = 0;
        for (; index < token.Length; index++)
        {
            if (!IsB64TokenChar(token[index]))
            {
                break;
            }
        }

        if (index == 0)
        {
            return false;
        }

        for (; index < token.Length; index++)
        {
            if (token[index] != '=')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsB64TokenChar(char character) =>
        character is (>= 'A' and <= 'Z')
            or (>= 'a' and <= 'z')
            or (>= '0' and <= '9')
            or '-' or '.' or '_' or '~' or '+' or '/';

    internal static void ComputeComparisonDigest(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<char> token,
        Span<byte> destination)
    {
        var byteCount = Encoding.UTF8.GetByteCount(token);
        byte[]? rented = null;
        Span<byte> bytes = byteCount <= MaximumStackTokenBytes
            ? stackalloc byte[MaximumStackTokenBytes]
            : (rented = ArrayPool<byte>.Shared.Rent(byteCount)).AsSpan(0, byteCount);
        bytes = bytes[..byteCount];
        try
        {
            _ = Encoding.UTF8.GetBytes(token, bytes);
            _ = HMACSHA256.HashData(key, bytes, destination);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private sealed class State
    {
        private State(
            byte[]? comparisonKey,
            byte[]? expectedToken,
            Func<string, bool>? validate,
            string missingChallenge,
            string invalidTokenChallenge,
            string invalidRequestChallenge)
        {
            ComparisonKey = comparisonKey;
            ExpectedToken = expectedToken;
            Validate = validate;
            MissingChallenge = missingChallenge;
            InvalidTokenChallenge = invalidTokenChallenge;
            InvalidRequestChallenge = invalidRequestChallenge;
        }

        public byte[]? ComparisonKey { get; }

        public byte[]? ExpectedToken { get; }

        public Func<string, bool>? Validate { get; }

        public string MissingChallenge { get; }

        public string InvalidTokenChallenge { get; }

        public string InvalidRequestChallenge { get; }

        public static State Create(BearerAuthOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            ValidateRealm(options.Realm);

            var token = options.Token;
            var hasValidate = options.Validate is not null;
            if ((token is not null) == hasValidate)
            {
                throw new ArgumentException(
                    "Bearer authentication requires exactly one of Token or Validate.",
                    nameof(options));
            }

            if (token is not null && !IsB64Token(token.AsSpan()))
            {
                throw new ArgumentException(
                    "The bearer token is empty or contains characters outside the RFC 6750 b64token charset.",
                    nameof(options));
            }

            var realm = options.Realm;
            byte[]? comparisonKey = null;
            byte[]? expectedToken = null;
            if (token is not null)
            {
                comparisonKey = RandomNumberGenerator.GetBytes(ComparisonDigestSize);
                expectedToken = new byte[ComparisonDigestSize];
                ComputeComparisonDigest(comparisonKey, token, expectedToken);
            }

            return new State(
                comparisonKey,
                expectedToken,
                options.Validate,
                string.Concat("Bearer realm=\"", realm, "\""),
                string.Concat("Bearer realm=\"", realm, "\", error=\"invalid_token\""),
                string.Concat("Bearer realm=\"", realm, "\", error=\"invalid_request\""));
        }

        public bool Matches(string token)
        {
            if (Validate is not null)
            {
                return Validate(token);
            }

            Span<byte> digest = stackalloc byte[ComparisonDigestSize];
            try
            {
                ComputeComparisonDigest(ComparisonKey, token, digest);
                return CryptographicOperations.FixedTimeEquals(digest, ExpectedToken);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }

        private static void ValidateRealm(string realm)
        {
            ArgumentNullException.ThrowIfNull(realm);
            for (var i = 0; i < realm.Length; i++)
            {
                var character = realm[i];
                if (character is '"' or '\r' or '\n' or '\0' or '\u007f'
                    || (character < ' ' && character != '\t'))
                {
                    throw new ArgumentException("The realm cannot contain quotes or control characters.", nameof(realm));
                }
            }
        }
    }
}

/// <summary>
/// Options for <see cref="BearerAuth.Middleware"/>. Exactly one of <see cref="Token"/> or
/// <see cref="Validate"/> must be set. JWT-based authentication lives in Miya.Jwt.
/// </summary>
public sealed class BearerAuthOptions
{
    /// <summary>
    /// Gets the expected bearer token for fixed-token authentication.
    /// </summary>
    public string? Token { get; init; }

    /// <summary>
    /// Gets an alternative callback that receives the parsed token string.
    /// </summary>
    public Func<string, bool>? Validate { get; init; }

    /// <summary>
    /// Gets the realm used in the <c>WWW-Authenticate</c> challenge. Quotes and CR/LF are rejected.
    /// </summary>
    public string Realm { get; init; } = "Restricted";
}
