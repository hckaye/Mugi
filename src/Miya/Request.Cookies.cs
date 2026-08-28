using System.Security.Cryptography;

namespace Miya;

public sealed partial class Request
{
    private Dictionary<string, string>? _cookies;

    /// <summary>Gets the first request cookie with the specified name.</summary>
    /// <param name="name">The cookie name.</param>
    /// <returns>The cookie value, or <see langword="null" /> when the cookie is absent.</returns>
    public string? Cookie(string name)
    {
        _context.EnsureActive();
        CookieUtility.ValidateName(name);
        return GetCookies().TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>Gets and verifies the first HMAC-SHA256 signed request cookie with the specified name.</summary>
    /// <param name="name">The cookie name.</param>
    /// <param name="key">A cryptographically random signing key of at least 32 bytes.</param>
    /// <returns>The unsigned value, or <see langword="null" /> when the cookie is absent or invalid.</returns>
    public string? SignedCookie(string name, ReadOnlySpan<byte> key)
    {
        _context.EnsureActive();
        CookieUtility.ValidateName(name);
        CookieUtility.ValidateSigningKey(key);

        if (!GetCookies().TryGetValue(name, out var signedValue))
        {
            return null;
        }

        var dot = signedValue.LastIndexOf('.');
        if (dot < 0)
        {
            return null;
        }

        var value = signedValue.AsSpan(0, dot);
        var encodedSignature = signedValue.AsSpan(dot + 1);
        Span<byte> suppliedSignature = stackalloc byte[HMACSHA256.HashSizeInBytes];
        if (!TryDecodeSignature(encodedSignature, suppliedSignature))
        {
            return null;
        }

        Span<byte> expectedSignature = stackalloc byte[HMACSHA256.HashSizeInBytes];
        try
        {
            CookieUtility.ComputeSignedCookieSignature(name, value, key, expectedSignature);
            return CryptographicOperations.FixedTimeEquals(expectedSignature, suppliedSignature)
                ? value.ToString()
                : null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedSignature);
            CryptographicOperations.ZeroMemory(suppliedSignature);
        }
    }

    private Dictionary<string, string> GetCookies()
    {
        if (_cookies is not null)
        {
            return _cookies;
        }

        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Feature.Headers.TryGetValue("Cookie", out var headerValues))
        {
            for (var index = 0; index < headerValues.Count; index++)
            {
                var header = headerValues[index];
                if (!string.IsNullOrEmpty(header))
                {
                    ParseCookieHeader(header.AsSpan(), cookies);
                }
            }
        }

        _cookies = cookies;
        return cookies;
    }

    private static void ParseCookieHeader(ReadOnlySpan<char> header, Dictionary<string, string> cookies)
    {
        var start = 0;
        while (start <= header.Length)
        {
            var separator = header[start..].IndexOf(';');
            var end = separator < 0 ? header.Length : start + separator;
            var pair = TrimCookieWhitespace(header[start..end]);
            var equals = pair.IndexOf('=');
            if (equals > 0)
            {
                var name = TrimCookieWhitespace(pair[..equals]);
                var value = TrimCookieWhitespace(pair[(equals + 1)..]);
                if (CookieUtility.IsValidName(name))
                {
                    if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                    {
                        value = value[1..^1];
                    }

                    var cookieName = name.ToString();
                    if (!cookies.ContainsKey(cookieName))
                    {
                        cookies.Add(cookieName, value.ToString());
                    }
                }
            }

            if (separator < 0)
            {
                break;
            }

            start = end + 1;
        }
    }

    private static ReadOnlySpan<char> TrimCookieWhitespace(ReadOnlySpan<char> value)
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

    private static bool TryDecodeSignature(ReadOnlySpan<char> value, Span<byte> destination)
    {
        if (value.Length != 43 || destination.Length < HMACSHA256.HashSizeInBytes)
        {
            return false;
        }

        Span<char> base64 = stackalloc char[44];
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            base64[index] = character switch
            {
                '-' => '+',
                '_' => '/',
                >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' => character,
                _ => '\0',
            };

            if (base64[index] == '\0')
            {
                return false;
            }
        }

        base64[^1] = '=';
        return Convert.TryFromBase64Chars(base64, destination, out var bytesWritten)
            && bytesWritten == HMACSHA256.HashSizeInBytes;
    }
}
