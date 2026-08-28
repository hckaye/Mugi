namespace Miya.Jwt;

/// <summary>Contains the result of JWT verification.</summary>
public readonly struct JwtResult
{
    internal JwtResult(bool isValid, JwtPayload? payload, JwtError error)
    {
        IsValid = isValid;
        Payload = payload;
        Error = error;
    }

    /// <summary>Gets whether the token passed signature and claim validation.</summary>
    public bool IsValid { get; }

    /// <summary>Gets the verified payload, or null when verification failed.</summary>
    public JwtPayload? Payload { get; }

    /// <summary>Gets the verification error.</summary>
    public JwtError Error { get; }

    internal static JwtResult Valid(JwtPayload payload) => new(true, payload, JwtError.None);

    internal static JwtResult Invalid(JwtError error) => new(false, null, error);
}
