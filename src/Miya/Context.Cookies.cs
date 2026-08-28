using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Miya;

public partial class Context
{
    private const int MaximumCookieBytes = 4096;

    /// <summary>Adds a cookie to the response.</summary>
    /// <param name="name">The cookie name.</param>
    /// <param name="value">The cookie value.</param>
    /// <param name="options">The cookie attributes, or <see langword="null" /> to use the defaults.</param>
    public void SetCookie(string name, string value, CookieOptions? options = null) =>
        AppendCookie(name, value, options, maxAgeOverride: null, overrideMaxAge: false);

    /// <summary>Adds an HMAC-SHA256 signed cookie to the response.</summary>
    /// <param name="name">The cookie name.</param>
    /// <param name="value">The cookie value.</param>
    /// <param name="key">A cryptographically random signing key of at least 32 bytes.</param>
    /// <param name="options">The cookie attributes, or <see langword="null" /> to use the defaults.</param>
    public void SetSignedCookie(
        string name,
        string value,
        ReadOnlySpan<byte> key,
        CookieOptions? options = null)
    {
        EnsureActive();
        CookieUtility.ValidateName(name);
        CookieUtility.ValidateValue(value);
        CookieUtility.ValidateSigningKey(key);

        const int signatureLength = 43;
        if ((long)name.Length + value.Length + signatureLength + 2 > MaximumCookieBytes)
        {
            throw CookieTooLarge();
        }

        Span<byte> signature = stackalloc byte[HMACSHA256.HashSizeInBytes];
        Span<char> base64 = stackalloc char[44];
        try
        {
            CookieUtility.ComputeSignedCookieSignature(name, value, key, signature);
            if (!Convert.TryToBase64Chars(signature, base64, out var base64Length))
            {
                throw new InvalidOperationException("The cookie signature could not be encoded.");
            }

            for (var index = 0; index < base64Length; index++)
            {
                base64[index] = base64[index] switch
                {
                    '+' => '-',
                    '/' => '_',
                    _ => base64[index],
                };
            }

            while (base64Length > 0 && base64[base64Length - 1] == '=')
            {
                base64Length--;
            }

            Span<char> signedValue = stackalloc char[value.Length + 1 + base64Length];
            value.AsSpan().CopyTo(signedValue);
            signedValue[value.Length] = '.';
            base64[..base64Length].CopyTo(signedValue[(value.Length + 1)..]);
            SetCookie(name, new string(signedValue), options);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    /// <summary>Expires a cookie in the response.</summary>
    /// <param name="name">The cookie name.</param>
    /// <param name="options">The cookie attributes, or <see langword="null" /> to use the defaults.</param>
    public void DeleteCookie(string name, CookieOptions? options = null) =>
        AppendCookie(name, string.Empty, options, TimeSpan.Zero, overrideMaxAge: true);

    private void AppendCookie(
        string name,
        string value,
        CookieOptions? options,
        TimeSpan? maxAgeOverride,
        bool overrideMaxAge)
    {
        EnsureActive();
        CookieUtility.ValidateName(name);
        CookieUtility.ValidateValue(value);
        if ((long)name.Length + value.Length + 1 > MaximumCookieBytes)
        {
            throw CookieTooLarge();
        }

        var path = options is null ? "/" : options.Path;
        if (path is null)
        {
            throw new ArgumentException("The cookie path must not be null.", nameof(options));
        }

        var domain = options?.Domain;
        var maxAge = overrideMaxAge ? maxAgeOverride : options?.MaxAge;
        var expires = options?.Expires;
        var secure = options?.Secure ?? false;
        var httpOnly = options?.HttpOnly ?? false;
        var sameSite = options?.SameSite ?? SameSite.Lax;

        CookieUtility.ValidateAttribute(path, nameof(CookieOptions.Path));
        if (domain is not null)
        {
            CookieUtility.ValidateDomain(domain);
        }

        CookieUtility.ValidatePrefix(name, path, domain, secure);

        if (sameSite == SameSite.None && !secure)
        {
            throw new InvalidOperationException(
                "SameSite=None cookies must also set Secure because browsers reject them otherwise.");
        }

        var builder = new StringBuilder();
        builder.Append(name).Append('=').Append(value);
        if (maxAge.HasValue)
        {
            builder.Append("; Max-Age=").Append((long)maxAge.Value.TotalSeconds);
        }

        if (expires.HasValue)
        {
            builder.Append("; Expires=")
                .Append(expires.Value.UtcDateTime.ToString("R", CultureInfo.InvariantCulture));
        }

        if (domain is not null)
        {
            builder.Append("; Domain=").Append(domain);
        }

        builder.Append("; Path=").Append(path);
        if (secure)
        {
            builder.Append("; Secure");
        }

        if (httpOnly)
        {
            builder.Append("; HttpOnly");
        }

        builder.Append("; SameSite=").Append(sameSite switch
        {
            SameSite.None => "None",
            SameSite.Lax => "Lax",
            SameSite.Strict => "Strict",
            _ => throw new ArgumentOutOfRangeException(nameof(options), "The SameSite value is invalid."),
        });

        var header = builder.ToString();
        if (Encoding.UTF8.GetByteCount(header) > MaximumCookieBytes)
        {
            throw CookieTooLarge();
        }

        AppendHeader("Set-Cookie", header);
    }

    private static ArgumentException CookieTooLarge() =>
        new("The Set-Cookie header exceeds the 4096-byte limit.");
}
