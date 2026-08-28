using System.Text;
using JwtApi = Mugi.Jwt.Jwt;

namespace Mugi.Jwt.Tests;

public sealed class JwtClaimValidationTests
{
    private readonly JwtKey _key = JwtKey.HS256(JwtTestHelpers.Secret);

    [Fact]
    public void ExpirationBoundaryIsExclusiveWithoutSkew()
    {
        var token = Token("{\"exp\":1000}");

        var atBoundary = JwtApi.Verify(token, _key, JwtTestHelpers.At(1000));
        var beforeBoundary = JwtApi.Verify(token, _key, JwtTestHelpers.At(999.9999));

        Assert.Equal(JwtError.Expired, atBoundary.Error);
        Assert.True(beforeBoundary.IsValid);
    }

    [Fact]
    public void ExpirationSkewBoundaryIsExclusive()
    {
        var token = Token("{\"exp\":1000}");
        var atBoundary = Validation(1060, TimeSpan.FromSeconds(60));
        var oneTickBefore = Validation(
            DateTimeOffset.UnixEpoch.AddSeconds(1060).AddTicks(-1),
            TimeSpan.FromSeconds(60));

        Assert.Equal(JwtError.Expired, JwtApi.Verify(token, _key, atBoundary).Error);
        Assert.True(JwtApi.Verify(token, _key, oneTickBefore).IsValid);
    }

    [Fact]
    public void NotBeforeSkewBoundaryIsInclusive()
    {
        var token = Token("{\"exp\":2000,\"nbf\":1000}");
        var atBoundary = Validation(940, TimeSpan.FromSeconds(60));
        var oneTickBefore = Validation(
            DateTimeOffset.UnixEpoch.AddSeconds(940).AddTicks(-1),
            TimeSpan.FromSeconds(60));

        Assert.True(JwtApi.Verify(token, _key, atBoundary).IsValid);
        Assert.Equal(JwtError.NotYetValid, JwtApi.Verify(token, _key, oneTickBefore).Error);
    }

    [Fact]
    public void FractionalNumericDatesArePreservedForBoundaryChecks()
    {
        var token = Token("{\"exp\":1000.5,\"nbf\":900.25,\"iat\":800.75}");

        var valid = JwtApi.Verify(token, _key, JwtTestHelpers.At(1000.4999));
        var expired = JwtApi.Verify(token, _key, JwtTestHelpers.At(1000.5));

        Assert.True(valid.IsValid);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(1000.5), valid.Payload!.ExpiresAt);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(900.25), valid.Payload.NotBefore);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(800.75), valid.Payload.IssuedAt);
        Assert.Null(valid.Payload.GetInt64("exp"));
        Assert.Equal(JwtError.Expired, expired.Error);
    }

    [Fact]
    public void MissingExpirationCanBeRequiredOrOptional()
    {
        var token = Token("{\"sub\":\"subject\"}");

        Assert.Equal(JwtError.MissingExpiration, JwtApi.Verify(token, _key).Error);
        var optional = JwtApi.Verify(token, _key, JwtTestHelpers.At(100, requireExpiration: false));
        Assert.True(optional.IsValid);
        Assert.Equal("subject", optional.Payload!.Subject);
    }

    [Fact]
    public void IssuerMustMatchExactlyWhenConfigured()
    {
        var token = Token("{\"iss\":\"issuer\",\"exp\":200}");

        Assert.True(JwtApi.Verify(token, _key, Validation(100, issuer: "issuer")).IsValid);
        Assert.Equal(
            JwtError.IssuerMismatch,
            JwtApi.Verify(token, _key, Validation(100, issuer: "Issuer")).Error);
        Assert.Equal(
            JwtError.IssuerMismatch,
            JwtApi.Verify(Token("{\"exp\":200}"), _key, Validation(100, issuer: "issuer")).Error);
    }

    [Fact]
    public void StringAudienceMustMatchExactlyWhenConfigured()
    {
        var token = Token("{\"aud\":\"api\",\"exp\":200}");

        Assert.True(JwtApi.Verify(token, _key, Validation(100, audience: "api")).IsValid);
        Assert.Equal(
            JwtError.AudienceMismatch,
            JwtApi.Verify(token, _key, Validation(100, audience: "API")).Error);
    }

    [Fact]
    public void AudienceArrayMatchesMembershipWithoutSurfacingAsString()
    {
        var token = Token("{\"aud\":[\"web\",\"api\",\"mobile\"],\"exp\":200}");

        var result = JwtApi.Verify(token, _key, Validation(100, audience: "api"));
        var mismatch = JwtApi.Verify(token, _key, Validation(100, audience: "other"));

        Assert.True(result.IsValid);
        Assert.Null(result.Payload!.Audience);
        Assert.Null(result.Payload.GetString("aud"));
        Assert.Equal(JwtError.AudienceMismatch, mismatch.Error);
    }

    [Fact]
    public void NestedAndUnsupportedCustomClaimsAreSkipped()
    {
        var token = Token(
            "{\"exp\":200,\"text\":\"value\",\"count\":7,\"enabled\":true," +
            "\"fraction\":1.5,\"nothing\":null,\"object\":{\"nested\":[1,2]},\"array\":[\"x\"]}");

        var result = JwtApi.Verify(token, _key, JwtTestHelpers.At(100));

        Assert.True(result.IsValid);
        Assert.Equal("value", result.Payload!.GetString("text"));
        Assert.Equal(7, result.Payload.GetInt64("count"));
        Assert.True(result.Payload.GetBool("enabled"));
        Assert.Null(result.Payload.GetInt64("fraction"));
        Assert.Null(result.Payload.GetString("nothing"));
        Assert.Null(result.Payload.GetString("object"));
        Assert.Null(result.Payload.GetString("array"));
    }

    [Theory]
    [InlineData("{\"exp\":\"200\"}")]
    [InlineData("{\"exp\":1e999}")]
    [InlineData("{\"nbf\":true,\"exp\":200}")]
    [InlineData("{\"iat\":null,\"exp\":200}")]
    [InlineData("{\"sub\":1,\"exp\":200}")]
    [InlineData("{\"iss\":null,\"exp\":200}")]
    [InlineData("{\"jti\":{},\"exp\":200}")]
    [InlineData("{\"aud\":7,\"exp\":200}")]
    [InlineData("{\"aud\":[\"api\",7],\"exp\":200}")]
    public void RegisteredClaimsRejectWrongJsonTypes(string payload)
    {
        Assert.Equal(
            JwtError.Malformed,
            JwtApi.Verify(Token(payload), _key, JwtTestHelpers.At(100)).Error);
    }

    [Fact]
    public void DuplicatePayloadClaimIsMalformed()
    {
        var token = Token("{\"exp\":200,\"sub\":\"a\",\"sub\":\"b\"}");

        Assert.Equal(JwtError.Malformed, JwtApi.Verify(token, _key, JwtTestHelpers.At(100)).Error);
    }

    [Fact]
    public void PayloadMustBeAJsonObject()
    {
        Assert.Equal(
            JwtError.Malformed,
            JwtApi.Verify(Token("[]"), _key, JwtTestHelpers.At(100)).Error);
        Assert.Equal(
            JwtError.Malformed,
            JwtApi.Verify(Token("not-json"), _key, JwtTestHelpers.At(100)).Error);
    }

    [Fact]
    public void JsonDepthLimitAppliesToSkippedClaims()
    {
        var builder = new StringBuilder("{\"exp\":200,\"nested\":");
        for (var i = 0; i < 65; i++)
        {
            builder.Append("{\"v\":");
        }

        builder.Append('0');
        for (var i = 0; i < 65; i++)
        {
            builder.Append('}');
        }

        builder.Append('}');

        Assert.Equal(
            JwtError.Malformed,
            JwtApi.Verify(Token(builder.ToString()), _key, JwtTestHelpers.At(100)).Error);
    }

    [Fact]
    public void ClockIsNotInvokedWhenSignatureIsInvalid()
    {
        var token = Token("{\"exp\":200}");
        token = JwtTestHelpers.ReplaceSegment(token, 2, JwtTestHelpers.Encode(new byte[32]));
        var invoked = false;
        var validation = new JwtValidation
        {
            Clock = () =>
            {
                invoked = true;
                return DateTimeOffset.UnixEpoch;
            },
        };

        Assert.Equal(JwtError.InvalidSignature, JwtApi.Verify(token, _key, validation).Error);
        Assert.False(invoked);
    }

    [Fact]
    public void NegativeClockSkewIsRejected()
    {
        var validation = new JwtValidation { ClockSkew = TimeSpan.FromTicks(-1) };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            JwtApi.Verify(Token("{\"exp\":200}"), _key, validation));
    }

    private static JwtValidation Validation(
        double numericDate,
        TimeSpan? skew = null,
        string? issuer = null,
        string? audience = null) => Validation(
            DateTimeOffset.UnixEpoch.AddSeconds(numericDate),
            skew,
            issuer,
            audience);

    private static JwtValidation Validation(
        DateTimeOffset now,
        TimeSpan? skew = null,
        string? issuer = null,
        string? audience = null) => new()
        {
            Clock = () => now,
            ClockSkew = skew ?? TimeSpan.Zero,
            Issuer = issuer,
            Audience = audience,
        };

    private static string Token(string payload) => JwtTestHelpers.CreateHsToken(
        "{\"alg\":\"HS256\",\"typ\":\"JWT\"}",
        payload);
}
