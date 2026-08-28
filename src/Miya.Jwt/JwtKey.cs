using System.Security.Cryptography;

namespace Miya.Jwt;

/// <summary>Holds the algorithm and cryptographic key used to sign or verify JWTs.</summary>
public sealed class JwtKey
{
    private JwtKey(JwtAlgorithm algorithm, byte[]? secret, RSA? rsa, ECDsa? ecdsa)
    {
        Algorithm = algorithm;
        Secret = secret!;
        RsaKey = rsa!;
        EcdsaKey = ecdsa!;
    }

    internal JwtAlgorithm Algorithm { get; }

    internal string AlgorithmName => Algorithm switch
    {
        JwtAlgorithm.HS256 => "HS256",
        JwtAlgorithm.RS256 => "RS256",
        _ => "ES256",
    };

    internal byte[] Secret { get; } = null!;

    internal RSA RsaKey { get; } = null!;

    internal ECDsa EcdsaKey { get; } = null!;

    /// <summary>Creates an HS256 key by copying a secret of at least 32 bytes.</summary>
    public static JwtKey HS256(ReadOnlySpan<byte> secret)
    {
        if (secret.Length < 32)
        {
            throw new ArgumentException("HS256 secrets must contain at least 32 bytes.", nameof(secret));
        }

        return new JwtKey(JwtAlgorithm.HS256, secret.ToArray(), null, null);
    }

    /// <summary>Creates an RS256 key from an RSA key of at least 2048 bits.</summary>
    public static JwtKey RS256(RSA rsa)
    {
        ArgumentNullException.ThrowIfNull(rsa);
        if (rsa.KeySize < 2048)
        {
            throw new ArgumentException("RS256 keys must contain at least 2048 bits.", nameof(rsa));
        }

        return new JwtKey(JwtAlgorithm.RS256, null, rsa, null);
    }

    /// <summary>Creates an ES256 key from an ECDSA key on the NIST P-256 curve.</summary>
    public static JwtKey ES256(ECDsa ecdsa)
    {
        ArgumentNullException.ThrowIfNull(ecdsa);
        if (ecdsa.KeySize != 256 || !IsP256(ecdsa))
        {
            throw new ArgumentException("ES256 keys must use the NIST P-256 curve.", nameof(ecdsa));
        }

        return new JwtKey(JwtAlgorithm.ES256, null, null, ecdsa);
    }

    private static bool IsP256(ECDsa ecdsa)
    {
        try
        {
            var parameters = ecdsa.ExportParameters(includePrivateParameters: false);
            return string.Equals(
                parameters.Curve.Oid.Value,
                ECCurve.NamedCurves.nistP256.Oid.Value,
                StringComparison.Ordinal);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}

internal enum JwtAlgorithm
{
    HS256,
    RS256,
    ES256,
}
