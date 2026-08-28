using System.Security.Cryptography;
using System.Text;

namespace Mugi.Jwt.Tests;

internal static class JwtTestHelpers
{
    public static readonly byte[] Secret = Encoding.ASCII.GetBytes(
        "0123456789abcdef0123456789abcdef");

    public static string CreateHsToken(string headerJson, string payloadJson, byte[]? secret = null)
    {
        var header = Encode(Encoding.UTF8.GetBytes(headerJson));
        var payload = Encode(Encoding.UTF8.GetBytes(payloadJson));
        var input = string.Concat(header, ".", payload);
        var signature = HMACSHA256.HashData(secret ?? Secret, Encoding.ASCII.GetBytes(input));
        return string.Concat(input, ".", Encode(signature));
    }

    public static string CreateRsToken(string headerJson, string payloadJson, RSA rsa)
    {
        var header = Encode(Encoding.UTF8.GetBytes(headerJson));
        var payload = Encode(Encoding.UTF8.GetBytes(payloadJson));
        var input = string.Concat(header, ".", payload);
        var signature = rsa.SignData(
            Encoding.ASCII.GetBytes(input),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return string.Concat(input, ".", Encode(signature));
    }

    public static string CreateEsToken(
        string headerJson,
        string payloadJson,
        ECDsa ecdsa,
        DSASignatureFormat format = DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
    {
        var header = Encode(Encoding.UTF8.GetBytes(headerJson));
        var payload = Encode(Encoding.UTF8.GetBytes(payloadJson));
        var input = string.Concat(header, ".", payload);
        var signature = ecdsa.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, format);
        return string.Concat(input, ".", Encode(signature));
    }

    public static string Encode(ReadOnlySpan<byte> value)
    {
        return Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static byte[] Decode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = (base64.Length % 4) switch
        {
            2 => string.Concat(base64, "=="),
            3 => string.Concat(base64, "="),
            _ => base64,
        };
        return Convert.FromBase64String(base64);
    }

    public static string DecodeSegment(string token, int segment)
    {
        var parts = token.Split('.');
        return Encoding.UTF8.GetString(Decode(parts[segment]));
    }

    public static string ReplaceSegment(string token, int segment, string replacement)
    {
        var parts = token.Split('.');
        parts[segment] = replacement;
        return string.Join('.', parts);
    }

    public static JwtValidation At(double numericDate, bool requireExpiration = true) => new()
    {
        Clock = () => DateTimeOffset.UnixEpoch.AddSeconds(numericDate),
        ClockSkew = TimeSpan.Zero,
        RequireExpiration = requireExpiration,
    };
}
