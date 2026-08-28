using System.Security.Cryptography;
using System.Text;
using JwtApi = Mugi.Jwt.Jwt;

namespace Mugi.Jwt.Tests;

public sealed class JwtRoundTripTests
{
    [Fact]
    public void HS256RoundTripsRegisteredAndCustomClaims()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(2_000_000_000);
        var key = JwtKey.HS256(JwtTestHelpers.Secret);
        var payload = CreatePayload(now);

        var result = JwtApi.Verify(JwtApi.Sign(payload, key), key, ValidationAt(now));

        Assert.True(result.IsValid);
        AssertPayload(result.Payload!, now);
    }

    [Fact]
    public void RS256RoundTripsRegisteredAndCustomClaims()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(2_000_000_000);
        using var rsa = RSA.Create(2048);
        var key = JwtKey.RS256(rsa);

        var result = JwtApi.Verify(JwtApi.Sign(CreatePayload(now), key), key, ValidationAt(now));

        Assert.True(result.IsValid);
        AssertPayload(result.Payload!, now);
    }

    [Fact]
    public void ES256RoundTripsRegisteredAndCustomClaims()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(2_000_000_000);
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var key = JwtKey.ES256(ecdsa);

        var token = JwtApi.Sign(CreatePayload(now), key);
        var result = JwtApi.Verify(token, key, ValidationAt(now));

        Assert.True(result.IsValid);
        Assert.Equal(64, JwtTestHelpers.Decode(token.Split('.')[2]).Length);
        AssertPayload(result.Payload!, now);
    }

    [Fact]
    public void SignUsesDeterministicClaimOrderAndIntegerNumericDates()
    {
        var payload = new JwtPayload
        {
            Subject = "subject",
            Issuer = "issuer",
            Audience = "audience",
            ExpiresAt = DateTimeOffset.UnixEpoch.AddSeconds(10.9),
            NotBefore = DateTimeOffset.UnixEpoch.AddSeconds(2.9),
            IssuedAt = DateTimeOffset.UnixEpoch.AddSeconds(1.9),
            TokenId = "id",
        }.WithClaim("z", true).WithClaim("a", 7L);

        var token = JwtApi.Sign(payload, JwtKey.HS256(JwtTestHelpers.Secret));

        Assert.Equal("{\"alg\":\"HS256\",\"typ\":\"JWT\"}", JwtTestHelpers.DecodeSegment(token, 0));
        Assert.Equal(
            "{\"sub\":\"subject\",\"iss\":\"issuer\",\"aud\":\"audience\",\"exp\":10," +
            "\"nbf\":2,\"iat\":1,\"jti\":\"id\",\"z\":true,\"a\":7}",
            JwtTestHelpers.DecodeSegment(token, 1));
    }

    [Fact]
    public void UnicodeClaimNamesAndValuesRoundTrip()
    {
        var key = JwtKey.HS256(JwtTestHelpers.Secret);
        var payload = new JwtPayload
        {
            Subject = "利用者😀",
            ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(2_000_000_100),
        }.WithClaim("役割", "管理者\n東京");

        var result = JwtApi.Verify(token: JwtApi.Sign(payload, key), key, JwtTestHelpers.At(2_000_000_000));

        Assert.True(result.IsValid);
        Assert.Equal("利用者😀", result.Payload!.Subject);
        Assert.Equal("管理者\n東京", result.Payload.GetString("役割"));
    }

    [Fact]
    public void WithClaimReturnsCopyAndReplacementKeepsInsertionPosition()
    {
        var original = new JwtPayload().WithClaim("first", "one").WithClaim("second", 2L);
        var changed = original.WithClaim("first", "updated");

        Assert.Equal("one", original.GetString("first"));
        Assert.Equal("updated", changed.GetString("first"));
        var token = JwtApi.Sign(changed, JwtKey.HS256(JwtTestHelpers.Secret));
        Assert.Equal("{\"first\":\"updated\",\"second\":2}", JwtTestHelpers.DecodeSegment(token, 1));
    }

    [Theory]
    [InlineData("sub")]
    [InlineData("iss")]
    [InlineData("aud")]
    [InlineData("exp")]
    [InlineData("nbf")]
    [InlineData("iat")]
    [InlineData("jti")]
    public void WithClaimRejectsRegisteredNames(string name)
    {
        Assert.Throws<ArgumentException>(() => new JwtPayload().WithClaim(name, "value"));
    }

    [Fact]
    public void HS256FactoryCopiesSecret()
    {
        var secret = JwtTestHelpers.Secret.ToArray();
        var key = JwtKey.HS256(secret);
        var payload = new JwtPayload { ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(200) };
        var token = JwtApi.Sign(payload, key);
        secret.AsSpan().Fill(0);

        Assert.True(JwtApi.Verify(token, key, JwtTestHelpers.At(100)).IsValid);
    }

    [Fact]
    public void KeyFactoriesRejectWeakOrWrongCurveKeys()
    {
        Assert.Throws<ArgumentException>(() => JwtKey.HS256(new byte[31]));
        using var smallRsa = RSA.Create(1024);
        Assert.Throws<ArgumentException>(() => JwtKey.RS256(smallRsa));
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Throws<ArgumentException>(() => JwtKey.ES256(p384));
    }

    private static JwtPayload CreatePayload(DateTimeOffset now) => new JwtPayload
    {
        Subject = "subject",
        Issuer = "issuer",
        Audience = "audience",
        ExpiresAt = now.AddMinutes(5),
        NotBefore = now.AddMinutes(-1),
        IssuedAt = now,
        TokenId = "token-id",
    }.WithClaim("role", "admin").WithClaim("level", 7L).WithClaim("enabled", true);

    private static JwtValidation ValidationAt(DateTimeOffset now) => new()
    {
        Clock = () => now,
        ClockSkew = TimeSpan.Zero,
        Issuer = "issuer",
        Audience = "audience",
    };

    private static void AssertPayload(JwtPayload payload, DateTimeOffset now)
    {
        Assert.Equal("subject", payload.Subject);
        Assert.Equal("issuer", payload.Issuer);
        Assert.Equal("audience", payload.Audience);
        Assert.Equal(now.AddMinutes(5), payload.ExpiresAt);
        Assert.Equal(now.AddMinutes(-1), payload.NotBefore);
        Assert.Equal(now, payload.IssuedAt);
        Assert.Equal("token-id", payload.TokenId);
        Assert.Equal("admin", payload.GetString("role"));
        Assert.Equal(7, payload.GetInt64("level"));
        Assert.True(payload.GetBool("enabled"));
        Assert.Null(payload.GetBool("role"));
        Assert.Null(payload.GetString("missing"));
    }
}
