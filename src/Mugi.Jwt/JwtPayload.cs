namespace Mugi.Jwt;

/// <summary>Contains registered JWT claims and ordered custom scalar claims.</summary>
public sealed class JwtPayload
{
    private readonly List<JwtClaim> _claims;

    /// <summary>Creates an empty JWT payload.</summary>
    public JwtPayload()
    {
        _claims = [];
    }

    private JwtPayload(JwtPayload source, List<JwtClaim> claims)
    {
        Subject = source.Subject;
        Issuer = source.Issuer;
        Audience = source.Audience;
        ExpiresAt = source.ExpiresAt;
        NotBefore = source.NotBefore;
        IssuedAt = source.IssuedAt;
        TokenId = source.TokenId;
        AudienceValues = source.AudienceValues;
        ExpiresAtNumber = source.ExpiresAtNumber;
        NotBeforeNumber = source.NotBeforeNumber;
        IssuedAtNumber = source.IssuedAtNumber;
        _claims = claims;
    }

    /// <summary>Gets the subject claim.</summary>
    public string? Subject { get; init; }

    /// <summary>Gets the issuer claim.</summary>
    public string? Issuer { get; init; }

    /// <summary>Gets the audience when it uses the single-string form.</summary>
    public string? Audience { get; init; }

    /// <summary>Gets the expiration time.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Gets the time before which the token is not valid.</summary>
    public DateTimeOffset? NotBefore { get; init; }

    /// <summary>Gets the time at which the token was issued.</summary>
    public DateTimeOffset? IssuedAt { get; init; }

    /// <summary>Gets the token identifier.</summary>
    public string? TokenId { get; init; }

    internal string[]? AudienceValues { get; init; }

    internal double? ExpiresAtNumber { get; init; }

    internal double? NotBeforeNumber { get; init; }

    internal double? IssuedAtNumber { get; init; }

    internal IReadOnlyList<JwtClaim> Claims => _claims;

    /// <summary>Returns a copy containing the specified custom string claim.</summary>
    public JwtPayload WithClaim(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return WithClaimCore(name, JwtClaim.String(name, value));
    }

    /// <summary>Returns a copy containing the specified custom integer claim.</summary>
    public JwtPayload WithClaim(string name, long value) =>
        WithClaimCore(name, JwtClaim.Int64(name, value));

    /// <summary>Returns a copy containing the specified custom Boolean claim.</summary>
    public JwtPayload WithClaim(string name, bool value) =>
        WithClaimCore(name, JwtClaim.Bool(name, value));

    /// <summary>Gets a string claim, or null when the claim is absent or has another type.</summary>
    public string? GetString(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var registered = name switch
        {
            "sub" => Subject,
            "iss" => Issuer,
            "aud" => Audience,
            "jti" => TokenId,
            _ => null,
        };

        if (registered is not null || IsRegisteredStringClaim(name))
        {
            return registered;
        }

        for (var i = 0; i < _claims.Count; i++)
        {
            var claim = _claims[i];
            if (claim.Kind == JwtClaimKind.String
                && string.Equals(claim.Name, name, StringComparison.Ordinal))
            {
                return claim.StringValue;
            }
        }

        return null;
    }

    /// <summary>Gets an integer claim, or null when the claim is absent or has another type.</summary>
    public long? GetInt64(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        switch (name)
        {
            case "exp":
                return GetNumericDateInt64(ExpiresAt, ExpiresAtNumber);
            case "nbf":
                return GetNumericDateInt64(NotBefore, NotBeforeNumber);
            case "iat":
                return GetNumericDateInt64(IssuedAt, IssuedAtNumber);
        }

        for (var i = 0; i < _claims.Count; i++)
        {
            var claim = _claims[i];
            if (claim.Kind == JwtClaimKind.Int64
                && string.Equals(claim.Name, name, StringComparison.Ordinal))
            {
                return claim.Int64Value;
            }
        }

        return null;
    }

    /// <summary>Gets a Boolean claim, or null when the claim is absent or has another type.</summary>
    public bool? GetBool(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        for (var i = 0; i < _claims.Count; i++)
        {
            var claim = _claims[i];
            if (claim.Kind == JwtClaimKind.Bool
                && string.Equals(claim.Name, name, StringComparison.Ordinal))
            {
                return claim.BoolValue;
            }
        }

        return null;
    }

    internal void AddParsedClaim(JwtClaim claim) => _claims.Add(claim);

    internal bool ContainsAudience(string audience)
    {
        if (string.Equals(Audience, audience, StringComparison.Ordinal))
        {
            return true;
        }

        var values = AudienceValues;
        if (values is null)
        {
            return false;
        }

        for (var i = 0; i < values.Length; i++)
        {
            if (string.Equals(values[i], audience, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private JwtPayload WithClaimCore(string name, JwtClaim claim)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (IsRegisteredClaim(name))
        {
            throw new ArgumentException("Registered claim names cannot be used for custom claims.", nameof(name));
        }

        var claims = new List<JwtClaim>(_claims);
        for (var i = 0; i < claims.Count; i++)
        {
            if (string.Equals(claims[i].Name, name, StringComparison.Ordinal))
            {
                claims[i] = claim;
                return new JwtPayload(this, claims);
            }
        }

        claims.Add(claim);
        return new JwtPayload(this, claims);
    }

    private static long? GetNumericDateInt64(DateTimeOffset? date, double? number)
    {
        if (number is { } raw)
        {
            return raw >= long.MinValue && raw <= long.MaxValue && Math.Truncate(raw) == raw
                ? (long)raw
                : null;
        }

        return date?.ToUnixTimeSeconds();
    }

    private static bool IsRegisteredStringClaim(string name) =>
        name is "sub" or "iss" or "aud" or "jti";

    private static bool IsRegisteredClaim(string name) =>
        name is "sub" or "iss" or "aud" or "exp" or "nbf" or "iat" or "jti";
}

internal readonly struct JwtClaim
{
    private JwtClaim(string name, JwtClaimKind kind, string? stringValue, long int64Value, bool boolValue)
    {
        Name = name;
        Kind = kind;
        StringValue = stringValue;
        Int64Value = int64Value;
        BoolValue = boolValue;
    }

    internal string Name { get; }

    internal JwtClaimKind Kind { get; }

    internal string? StringValue { get; }

    internal long Int64Value { get; }

    internal bool BoolValue { get; }

    internal static JwtClaim String(string name, string value) =>
        new(name, JwtClaimKind.String, value, 0, false);

    internal static JwtClaim Int64(string name, long value) =>
        new(name, JwtClaimKind.Int64, null, value, false);

    internal static JwtClaim Bool(string name, bool value) =>
        new(name, JwtClaimKind.Bool, null, 0, value);
}

internal enum JwtClaimKind
{
    String,
    Int64,
    Bool,
}
