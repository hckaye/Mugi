namespace Miya.Jwt;

/// <summary>Identifies why a JWT failed verification.</summary>
public enum JwtError
{
    /// <summary>The token is valid.</summary>
    None,

    /// <summary>The compact token, Base64url data, or JSON claims are malformed.</summary>
    Malformed,

    /// <summary>The token algorithm does not exactly match the supplied key.</summary>
    UnsupportedAlgorithm,

    /// <summary>The JOSE header contains an unsupported value.</summary>
    UnsupportedHeader,

    /// <summary>The signature does not verify with the supplied key.</summary>
    InvalidSignature,

    /// <summary>The token has no expiration time.</summary>
    MissingExpiration,

    /// <summary>The token has expired.</summary>
    Expired,

    /// <summary>The token cannot be used yet.</summary>
    NotYetValid,

    /// <summary>The issuer does not match the required issuer.</summary>
    IssuerMismatch,

    /// <summary>The audience does not contain the required audience.</summary>
    AudienceMismatch,
}
