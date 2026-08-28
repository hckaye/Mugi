namespace Mugi.Jwt;

/// <summary>Configures registered-claim validation for a JWT.</summary>
public sealed class JwtValidation
{
    /// <summary>Gets the issuer that must match the token exactly.</summary>
    public string? Issuer { get; init; }

    /// <summary>Gets the audience that must be present in the token.</summary>
    public string? Audience { get; init; }

    /// <summary>Gets the time allowed on either side of temporal claim boundaries.</summary>
    public TimeSpan ClockSkew { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Gets whether the token must contain an expiration time.</summary>
    public bool RequireExpiration { get; init; } = true;

    /// <summary>Gets the optional clock used during verification.</summary>
    public Func<DateTimeOffset>? Clock { get; init; }
}
