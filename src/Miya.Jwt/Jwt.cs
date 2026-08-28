using System.Buffers;
using System.Security.Cryptography;
using Miya.Json;

namespace Miya.Jwt;

/// <summary>Signs and verifies compact JWTs with HS256, RS256, or ES256.</summary>
public static class Jwt
{
    private const int MaximumTokenLength = 16 * 1024;
    private static readonly JwtValidation DefaultValidation = new();

    /// <summary>Signs a payload with the algorithm selected by the key.</summary>
    public static string Sign(JwtPayload payload, JwtKey key)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(key);

        var encodedHeader = Base64Url.Encode(JwtJson.WriteHeader(key.Algorithm));
        var encodedPayload = Base64Url.Encode(JwtJson.WritePayload(payload));
        var signingInput = string.Concat(encodedHeader, ".", encodedPayload);
        var signature = SignAscii(signingInput.AsSpan(), key);
        return string.Concat(signingInput, ".", Base64Url.Encode(signature));
    }

    /// <summary>Verifies a compact JWT and validates its registered claims.</summary>
    public static JwtResult Verify(string token, JwtKey key, JwtValidation? validation = null)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(key);
        return VerifyCore(token.AsSpan(), key, validation);
    }

    internal static JwtResult VerifyCore(
        ReadOnlySpan<char> token,
        JwtKey key,
        JwtValidation? validation)
    {
        ArgumentNullException.ThrowIfNull(key);
        var effectiveValidation = validation ?? DefaultValidation;
        if (effectiveValidation.ClockSkew < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(validation),
                "ClockSkew cannot be negative.");
        }

        if (token.Length > MaximumTokenLength)
        {
            return JwtResult.Invalid(JwtError.Malformed);
        }

        var firstDot = token.IndexOf('.');
        if (firstDot <= 0)
        {
            return JwtResult.Invalid(JwtError.Malformed);
        }

        var afterFirstDot = token[(firstDot + 1)..];
        var secondDotOffset = afterFirstDot.IndexOf('.');
        if (secondDotOffset <= 0)
        {
            return JwtResult.Invalid(JwtError.Malformed);
        }

        var secondDot = firstDot + 1 + secondDotOffset;
        if (secondDot == token.Length - 1 || token[(secondDot + 1)..].IndexOf('.') >= 0)
        {
            return JwtResult.Invalid(JwtError.Malformed);
        }

        var encodedHeader = token[..firstDot];
        var encodedPayload = token.Slice(firstDot + 1, secondDot - firstDot - 1);
        var encodedSignature = token[(secondDot + 1)..];
        if (!Base64Url.TryDecode(encodedHeader, out var headerBytes)
            || !Base64Url.TryDecode(encodedPayload, out var payloadBytes)
            || !Base64Url.TryDecode(encodedSignature, out var signatureBytes))
        {
            return JwtResult.Invalid(JwtError.Malformed);
        }

        JwtHeader header;
        try
        {
            header = JwtJson.ReadHeader(headerBytes);
        }
        catch (JsonException)
        {
            return JwtResult.Invalid(JwtError.Malformed);
        }

        if (!string.Equals(header.Algorithm, key.AlgorithmName, StringComparison.Ordinal))
        {
            return JwtResult.Invalid(JwtError.UnsupportedAlgorithm);
        }

        if (header.Unsupported)
        {
            return JwtResult.Invalid(JwtError.UnsupportedHeader);
        }

        if (!VerifyAscii(token[..secondDot], signatureBytes, key))
        {
            return JwtResult.Invalid(JwtError.InvalidSignature);
        }

        JwtPayload payload;
        try
        {
            payload = JwtJson.ReadPayload(payloadBytes);
        }
        catch (JsonException)
        {
            return JwtResult.Invalid(JwtError.Malformed);
        }

        return Validate(payload, effectiveValidation);
    }

    private static JwtResult Validate(JwtPayload payload, JwtValidation validation)
    {
        if (validation.RequireExpiration && payload.ExpiresAt is null)
        {
            return JwtResult.Invalid(JwtError.MissingExpiration);
        }

        var now = validation.Clock?.Invoke() ?? DateTimeOffset.UtcNow;
        var nowSeconds = NumericDate(now);
        var skewSeconds = validation.ClockSkew.TotalSeconds;

        if (payload.ExpiresAt is { } expiresAt)
        {
            var expiresAtSeconds = payload.ExpiresAtNumber ?? NumericDate(expiresAt);
            if (nowSeconds - skewSeconds >= expiresAtSeconds)
            {
                return JwtResult.Invalid(JwtError.Expired);
            }
        }

        if (payload.NotBefore is { } notBefore)
        {
            var notBeforeSeconds = payload.NotBeforeNumber ?? NumericDate(notBefore);
            if (nowSeconds + skewSeconds < notBeforeSeconds)
            {
                return JwtResult.Invalid(JwtError.NotYetValid);
            }
        }

        if (validation.Issuer is not null
            && !string.Equals(payload.Issuer, validation.Issuer, StringComparison.Ordinal))
        {
            return JwtResult.Invalid(JwtError.IssuerMismatch);
        }

        if (validation.Audience is not null && !payload.ContainsAudience(validation.Audience))
        {
            return JwtResult.Invalid(JwtError.AudienceMismatch);
        }

        return JwtResult.Valid(payload);
    }

    private static byte[] SignAscii(ReadOnlySpan<char> value, JwtKey key)
    {
        var rented = ArrayPool<byte>.Shared.Rent(value.Length);
        try
        {
            CopyAscii(value, rented);
            var data = rented.AsSpan(0, value.Length);
            return key.Algorithm switch
            {
                JwtAlgorithm.HS256 => HMACSHA256.HashData(key.Secret, data),
                JwtAlgorithm.RS256 => key.RsaKey.SignData(
                    data,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1),
                _ => key.EcdsaKey.SignData(
                    data,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation),
            };
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static bool VerifyAscii(
        ReadOnlySpan<char> value,
        ReadOnlySpan<byte> signature,
        JwtKey key)
    {
        var rented = ArrayPool<byte>.Shared.Rent(value.Length);
        try
        {
            CopyAscii(value, rented);
            var data = rented.AsSpan(0, value.Length);
            try
            {
                switch (key.Algorithm)
                {
                    case JwtAlgorithm.HS256:
                        if (signature.Length != 32)
                        {
                            return false;
                        }

                        Span<byte> expected = stackalloc byte[32];
                        if (!HMACSHA256.TryHashData(key.Secret, data, expected, out var written)
                            || written != expected.Length)
                        {
                            return false;
                        }

                        return CryptographicOperations.FixedTimeEquals(expected, signature);
                    case JwtAlgorithm.RS256:
                        return key.RsaKey.VerifyData(
                            data,
                            signature,
                            HashAlgorithmName.SHA256,
                            RSASignaturePadding.Pkcs1);
                    default:
                        return signature.Length == 64
                            && key.EcdsaKey.VerifyData(
                                data,
                                signature,
                                HashAlgorithmName.SHA256,
                                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                }
            }
            catch (CryptographicException)
            {
                return false;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void CopyAscii(ReadOnlySpan<char> source, Span<byte> destination)
    {
        for (var i = 0; i < source.Length; i++)
        {
            destination[i] = (byte)source[i];
        }
    }

    private static double NumericDate(DateTimeOffset value) =>
        (value.UtcDateTime.Ticks - DateTimeOffset.UnixEpoch.Ticks) / (double)TimeSpan.TicksPerSecond;
}
