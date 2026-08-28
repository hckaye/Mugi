using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Miya.Middleware;

/// <summary>
/// HTTP Basic authentication middleware. Failed requests return 401 and do not call the next handler.
/// </summary>
public static class BasicAuth
{
    internal const int ComparisonDigestSize = HMACSHA256.HashSizeInBytes;
    private const int MaximumStackCredentialBytes = 256;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Creates Basic authentication middleware from the given options. Options are validated immediately.
    /// </summary>
    /// <param name="options">Fixed credentials or a validation callback, and the challenge realm.</param>
    /// <returns>Middleware that requires a valid <c>Authorization: Basic</c> header.</returns>
    public static Middleware<Context> Middleware(BasicAuthOptions options)
    {
        var state = State.Create(options);
        return (context, next) => Invoke(context, next, state, assignUser: false);
    }

    /// <summary>
    /// Creates Basic authentication middleware that stores the authenticated user name on
    /// <see cref="IAuthContext.AuthUser"/>.
    /// </summary>
    /// <typeparam name="TContext">A context type that implements <see cref="IAuthContext"/>.</typeparam>
    /// <param name="options">Fixed credentials or a validation callback, and the challenge realm.</param>
    /// <returns>Middleware that requires a valid <c>Authorization: Basic</c> header.</returns>
    public static Middleware<TContext> Middleware<TContext>(BasicAuthOptions options)
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
        if (!TryParse(context.Req.Header("Authorization"), out var user, out var password)
            || !state.Matches(user, password))
        {
            return Reject(context, state.Challenge);
        }

        if (assignUser)
        {
            ((IAuthContext)context).AuthUser = user;
        }

        return next(context);
    }

    private static ValueTask Reject(Context context, string challenge)
    {
        context.Status(StatusCodes.Status401Unauthorized);
        context.Header("WWW-Authenticate", challenge);
        return context.Text("Unauthorized");
    }

    private static bool TryParse(string? header, out string user, out string password)
    {
        user = "";
        password = "";
        if (header is null || header.Length < 7)
        {
            return false;
        }

        if (!header.AsSpan(0, 5).Equals("Basic", StringComparison.OrdinalIgnoreCase)
            || header[5] != ' ')
        {
            return false;
        }

        var token = header.AsSpan(6);
        if (!IsStrictBase64(token))
        {
            return false;
        }

        var maxBytes = (token.Length / 4) * 3;
        Span<byte> decoded = maxBytes <= 256 ? stackalloc byte[256] : new byte[maxBytes];
        if (!Convert.TryFromBase64Chars(token, decoded, out var written) || written == 0)
        {
            return false;
        }

        try
        {
            var text = StrictUtf8.GetString(decoded[..written]);
            var colon = text.IndexOf(':');
            if (colon < 0)
            {
                return false;
            }

            user = text[..colon];
            password = text[(colon + 1)..];
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsStrictBase64(ReadOnlySpan<char> token)
    {
        if (token.Length == 0 || token.Length % 4 != 0)
        {
            return false;
        }

        var padding = 0;
        for (var i = 0; i < token.Length; i++)
        {
            var character = token[i];
            if (character == '=')
            {
                padding++;
                if (padding > 2)
                {
                    return false;
                }
            }
            else if (padding > 0 || !IsBase64Char(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsBase64Char(char character) =>
        character is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '+' or '/';

    internal static void ComputeComparisonDigest(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<char> credential,
        Span<byte> destination)
    {
        var byteCount = StrictUtf8.GetByteCount(credential);
        byte[]? rented = null;
        Span<byte> bytes = byteCount <= MaximumStackCredentialBytes
            ? stackalloc byte[MaximumStackCredentialBytes]
            : (rented = ArrayPool<byte>.Shared.Rent(byteCount)).AsSpan(0, byteCount);
        bytes = bytes[..byteCount];
        try
        {
            _ = StrictUtf8.GetBytes(credential, bytes);
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
            byte[]? expectedUser,
            byte[]? expectedPassword,
            Func<string, string, bool>? validate,
            string challenge)
        {
            ComparisonKey = comparisonKey;
            ExpectedUser = expectedUser;
            ExpectedPassword = expectedPassword;
            Validate = validate;
            Challenge = challenge;
        }

        public byte[]? ComparisonKey { get; }

        public byte[]? ExpectedUser { get; }

        public byte[]? ExpectedPassword { get; }

        public Func<string, string, bool>? Validate { get; }

        public string Challenge { get; }

        public static State Create(BasicAuthOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            ValidateRealm(options.Realm);

            var hasUser = options.Username is not null;
            var hasPassword = options.Password is not null;
            var hasValidate = options.Validate is not null;
            if (hasValidate)
            {
                if (hasUser || hasPassword)
                {
                    throw new ArgumentException(
                        "Set either Username and Password, or Validate, not both.",
                        nameof(options));
                }

                return new State(
                    comparisonKey: null,
                    expectedUser: null,
                    expectedPassword: null,
                    options.Validate,
                    FormatChallenge(options.Realm));
            }

            if (!hasUser || !hasPassword)
            {
                throw new ArgumentException(
                    "Basic authentication requires Username and Password, or a Validate callback.",
                    nameof(options));
            }

            var comparisonKey = RandomNumberGenerator.GetBytes(ComparisonDigestSize);
            var expectedUser = new byte[ComparisonDigestSize];
            var expectedPassword = new byte[ComparisonDigestSize];
            ComputeComparisonDigest(comparisonKey, options.Username, expectedUser);
            ComputeComparisonDigest(comparisonKey, options.Password, expectedPassword);
            return new State(
                comparisonKey,
                expectedUser,
                expectedPassword,
                validate: null,
                FormatChallenge(options.Realm));
        }

        public bool Matches(string user, string password)
        {
            if (Validate is not null)
            {
                return Validate(user, password);
            }

            Span<byte> userDigest = stackalloc byte[ComparisonDigestSize];
            Span<byte> passwordDigest = stackalloc byte[ComparisonDigestSize];
            try
            {
                ComputeComparisonDigest(ComparisonKey, user, userDigest);
                ComputeComparisonDigest(ComparisonKey, password, passwordDigest);
                var userOk = CryptographicOperations.FixedTimeEquals(userDigest, ExpectedUser);
                var passwordOk = CryptographicOperations.FixedTimeEquals(passwordDigest, ExpectedPassword);
                return userOk && passwordOk;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(userDigest);
                CryptographicOperations.ZeroMemory(passwordDigest);
            }
        }

        private static string FormatChallenge(string realm) =>
            string.Concat("Basic realm=\"", realm, "\", charset=\"UTF-8\"");

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
/// Options for <see cref="BasicAuth.Middleware"/>. Exactly one of a fixed username/password pair
/// or <see cref="Validate"/> must be set.
/// </summary>
public sealed class BasicAuthOptions
{
    /// <summary>
    /// Gets the expected user name for fixed-pair authentication.
    /// </summary>
    public string? Username { get; init; }

    /// <summary>
    /// Gets the expected password for fixed-pair authentication. The password may contain colons.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Gets an alternative callback that receives the decoded user name and password.
    /// </summary>
    public Func<string, string, bool>? Validate { get; init; }

    /// <summary>
    /// Gets the realm used in the <c>WWW-Authenticate</c> challenge. Quotes and CR/LF are rejected.
    /// </summary>
    public string Realm { get; init; } = "Restricted";
}
