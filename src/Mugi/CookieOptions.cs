using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Mugi;

/// <summary>Specifies attributes for a response cookie.</summary>
public sealed class CookieOptions
{
    /// <summary>Gets the path to which the cookie applies.</summary>
    public string Path { get; init; } = "/";

    /// <summary>Gets the ASCII host to which the cookie applies. A single leading dot is accepted.</summary>
    public string? Domain { get; init; }

    /// <summary>Gets the maximum lifetime of the cookie.</summary>
    public TimeSpan? MaxAge { get; init; }

    /// <summary>Gets the date and time at which the cookie expires.</summary>
    public DateTimeOffset? Expires { get; init; }

    /// <summary>Gets whether the cookie is sent only over secure connections.</summary>
    public bool Secure { get; init; }

    /// <summary>Gets whether client-side scripts are prevented from accessing the cookie.</summary>
    public bool HttpOnly { get; init; }

    /// <summary>Gets the cross-site request policy for the cookie.</summary>
    public SameSite SameSite { get; init; } = SameSite.Lax;
}

/// <summary>Specifies when a cookie is included with cross-site requests.</summary>
public enum SameSite
{
    /// <summary>Sends the cookie with same-site and cross-site requests.</summary>
    None,

    /// <summary>Sends the cookie with same-site requests and top-level cross-site navigations.</summary>
    Lax,

    /// <summary>Sends the cookie only with same-site requests.</summary>
    Strict,
}

internal static class CookieUtility
{
    internal const int MinimumSigningKeyBytes = 32;
    private const int MaximumStackMacInputBytes = 4096;

    internal static void ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!IsValidName(name.AsSpan()))
        {
            throw new ArgumentException("The cookie name is not a valid RFC 6265 token.", nameof(name));
        }
    }

    internal static bool IsValidName(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty)
        {
            return false;
        }

        foreach (var character in name)
        {
            if (character is < '!' or > '~'
                || character is '(' or ')' or '<' or '>' or '@' or ',' or ';' or ':' or '\\'
                    or '"' or '/' or '[' or ']' or '?' or '=' or '{' or '}')
            {
                return false;
            }
        }

        return true;
    }

    internal static void ValidateValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        foreach (var character in value)
        {
            if (character is < '!' or > '~' or '"' or ',' or ';' or '\\')
            {
                throw new ArgumentException("The cookie value contains an invalid character.", nameof(value));
            }
        }
    }

    internal static void ValidateSigningKey(ReadOnlySpan<byte> key)
    {
        if (key.Length < MinimumSigningKeyBytes)
        {
            throw new ArgumentException(
                $"The signing key must contain at least {MinimumSigningKeyBytes} bytes.",
                nameof(key));
        }
    }

    internal static void ComputeSignedCookieSignature(
        ReadOnlySpan<char> name,
        ReadOnlySpan<char> value,
        ReadOnlySpan<byte> key,
        Span<byte> destination)
    {
        var nameByteCount = Encoding.UTF8.GetByteCount(name);
        var valueByteCount = Encoding.UTF8.GetByteCount(value);
        var inputLength = checked(sizeof(int) + nameByteCount + sizeof(int) + valueByteCount);
        byte[]? rented = null;
        Span<byte> input = inputLength <= MaximumStackMacInputBytes
            ? stackalloc byte[inputLength]
            : (rented = ArrayPool<byte>.Shared.Rent(inputLength)).AsSpan(0, inputLength);
        try
        {
            BinaryPrimitives.WriteInt32BigEndian(input, nameByteCount);
            var offset = sizeof(int);
            offset += Encoding.UTF8.GetBytes(name, input[offset..]);
            BinaryPrimitives.WriteInt32BigEndian(input[offset..], valueByteCount);
            offset += sizeof(int);
            _ = Encoding.UTF8.GetBytes(value, input[offset..]);
            _ = HMACSHA256.HashData(key, input, destination);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    internal static void ValidateDomain(string domain)
    {
        var host = domain.AsSpan();
        if (!host.IsEmpty && host[0] == '.')
        {
            host = host[1..];
        }

        if (host.IsEmpty || host.Length > 253 || host[^1] == '.')
        {
            throw InvalidDomain();
        }

        var labelStart = 0;
        for (var index = 0; index <= host.Length; index++)
        {
            if (index < host.Length && host[index] != '.')
            {
                var character = host[index];
                if (character is not (>= 'A' and <= 'Z')
                    and not (>= 'a' and <= 'z')
                    and not (>= '0' and <= '9')
                    and not '-')
                {
                    throw InvalidDomain();
                }

                continue;
            }

            var labelLength = index - labelStart;
            if (labelLength is 0 or > 63
                || host[labelStart] == '-'
                || host[index - 1] == '-')
            {
                throw InvalidDomain();
            }

            labelStart = index + 1;
        }
    }

    internal static void ValidatePrefix(
        string name,
        string path,
        string? domain,
        bool secure)
    {
        if (name.StartsWith("__Host-", StringComparison.Ordinal)
            && (!secure || path != "/" || domain is not null))
        {
            throw new ArgumentException(
                "A __Host- cookie must set Secure, use Path=/, and omit Domain.",
                nameof(name));
        }

        if (name.StartsWith("__Secure-", StringComparison.Ordinal) && !secure)
        {
            throw new ArgumentException("A __Secure- cookie must set Secure.", nameof(name));
        }
    }

    internal static void ValidateAttribute(string value, string attributeName)
    {
        foreach (var character in value)
        {
            if (character == ';' || char.IsControl(character))
            {
                throw new ArgumentException(
                    $"The cookie {attributeName} contains an invalid character.",
                    attributeName);
            }
        }
    }

    private static ArgumentException InvalidDomain() =>
        new("The cookie domain must be a valid ASCII host.", nameof(CookieOptions.Domain));
}
