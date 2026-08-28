using System.Security.Cryptography;
using System.Text;
using JwtApi = Miya.Jwt.Jwt;

namespace Miya.Jwt.Tests;

public sealed class JwtSecurityTests
{
    [Fact]
    public void CrossAlgorithmVerificationIsRejectedBeforeSignatureVerification()
    {
        var validation = JwtTestHelpers.At(100);
        var payload = new JwtPayload { ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(200) };
        var hsKey = JwtKey.HS256(JwtTestHelpers.Secret);
        using var rsa = RSA.Create(2048);
        var rsKey = JwtKey.RS256(rsa);
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var esKey = JwtKey.ES256(ecdsa);
        var hsToken = JwtApi.Sign(payload, hsKey);
        var rsToken = JwtApi.Sign(payload, rsKey);
        var esToken = JwtApi.Sign(payload, esKey);

        Assert.Equal(JwtError.UnsupportedAlgorithm, JwtApi.Verify(hsToken, rsKey, validation).Error);
        Assert.Equal(JwtError.UnsupportedAlgorithm, JwtApi.Verify(hsToken, esKey, validation).Error);
        Assert.Equal(JwtError.UnsupportedAlgorithm, JwtApi.Verify(rsToken, hsKey, validation).Error);
        Assert.Equal(JwtError.UnsupportedAlgorithm, JwtApi.Verify(rsToken, esKey, validation).Error);
        Assert.Equal(JwtError.UnsupportedAlgorithm, JwtApi.Verify(esToken, hsKey, validation).Error);
        Assert.Equal(JwtError.UnsupportedAlgorithm, JwtApi.Verify(esToken, rsKey, validation).Error);
    }

    [Fact]
    public void NoneAndAlgorithmCaseVariantsAreRejected()
    {
        var key = JwtKey.HS256(JwtTestHelpers.Secret);
        var none = JwtTestHelpers.CreateHsToken("{\"alg\":\"none\"}", "{\"exp\":200}");
        var lower = JwtTestHelpers.CreateHsToken("{\"alg\":\"hs256\"}", "{\"exp\":200}");
        var unknown = JwtTestHelpers.CreateHsToken("{\"alg\":\"HS512\"}", "{\"exp\":200}");

        Assert.Equal(JwtError.UnsupportedAlgorithm, JwtApi.Verify(none, key).Error);
        Assert.Equal(JwtError.UnsupportedAlgorithm, JwtApi.Verify(lower, key).Error);
        Assert.Equal(JwtError.UnsupportedAlgorithm, JwtApi.Verify(unknown, key).Error);
    }

    [Fact]
    public void RsaPublicKeyCannotBeUsedForHmacConfusion()
    {
        using var rsa = RSA.Create(2048);
        var publicDer = rsa.ExportSubjectPublicKeyInfo();
        var forged = JwtTestHelpers.CreateHsToken(
            "{\"alg\":\"HS256\",\"typ\":\"JWT\"}",
            "{\"exp\":200}",
            publicDer);

        var result = JwtApi.Verify(forged, JwtKey.RS256(rsa), JwtTestHelpers.At(100));

        Assert.Equal(JwtError.UnsupportedAlgorithm, result.Error);
    }

    [Fact]
    public void TamperedHeaderPayloadAndSignatureFailSignatureVerification()
    {
        var key = JwtKey.HS256(JwtTestHelpers.Secret);
        var token = JwtTestHelpers.CreateHsToken(
            "{\"alg\":\"HS256\",\"typ\":\"JWT\",\"extra\":\"a\"}",
            "{\"sub\":\"alice\",\"exp\":200}");
        var header = JwtTestHelpers.Encode(
            Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\",\"extra\":\"b\"}"));
        var payload = JwtTestHelpers.Encode(Encoding.UTF8.GetBytes("{\"sub\":\"bob\",\"exp\":200}"));
        var signature = token.Split('.')[2].ToCharArray();
        signature[0] = signature[0] == 'A' ? 'B' : 'A';

        Assert.Equal(
            JwtError.InvalidSignature,
            JwtApi.Verify(JwtTestHelpers.ReplaceSegment(token, 0, header), key, JwtTestHelpers.At(100)).Error);
        Assert.Equal(
            JwtError.InvalidSignature,
            JwtApi.Verify(JwtTestHelpers.ReplaceSegment(token, 1, payload), key, JwtTestHelpers.At(100)).Error);
        Assert.Equal(
            JwtError.InvalidSignature,
            JwtApi.Verify(
                JwtTestHelpers.ReplaceSegment(token, 2, new string(signature)),
                key,
                JwtTestHelpers.At(100)).Error);
    }

    [Fact]
    public void InvalidSignatureWinsOverMalformedPayloadJson()
    {
        var key = JwtKey.HS256(JwtTestHelpers.Secret);
        var token = JwtTestHelpers.CreateHsToken("{\"alg\":\"HS256\"}", "not-json");
        var signature = token.Split('.')[2].ToCharArray();
        signature[0] = signature[0] == 'A' ? 'B' : 'A';

        var result = JwtApi.Verify(
            JwtTestHelpers.ReplaceSegment(token, 2, new string(signature)),
            key,
            JwtTestHelpers.At(100));

        Assert.Equal(JwtError.InvalidSignature, result.Error);
    }

    [Theory]
    [InlineData("a.b")]
    [InlineData("a.b.c.d")]
    [InlineData(".b.c")]
    [InlineData("a..c")]
    [InlineData("a.b.")]
    [InlineData("abc")]
    public void CompactStructureMustHaveThreeNonEmptySegments(string token)
    {
        Assert.Equal(
            JwtError.Malformed,
            JwtApi.Verify(token, JwtKey.HS256(JwtTestHelpers.Secret)).Error);
    }

    [Fact]
    public void EmptySignatureIsMalformed()
    {
        var token = JwtTestHelpers.CreateHsToken("{\"alg\":\"none\"}", "{}");
        token = token[..(token.LastIndexOf('.') + 1)];

        Assert.Equal(
            JwtError.Malformed,
            JwtApi.Verify(token, JwtKey.HS256(JwtTestHelpers.Secret)).Error);
    }

    [Fact]
    public void TokenLengthLimitRunsBeforeDecoding()
    {
        var token = string.Concat(new string('a', 16 * 1024), ".a.a");

        Assert.Equal(
            JwtError.Malformed,
            JwtApi.Verify(token, JwtKey.HS256(JwtTestHelpers.Secret)).Error);
    }

    [Theory]
    [InlineData(0, "=")]
    [InlineData(1, "=")]
    [InlineData(2, "=")]
    [InlineData(0, "+")]
    [InlineData(1, "/")]
    [InlineData(2, " ")]
    [InlineData(1, "\t")]
    public void Base64UrlRejectsPaddingStandardAlphabetAndWhitespace(int segment, string suffix)
    {
        var token = JwtTestHelpers.CreateHsToken("{\"alg\":\"HS256\"}", "{\"exp\":200}");
        var parts = token.Split('.');
        parts[segment] += suffix;

        Assert.Equal(
            JwtError.Malformed,
            JwtApi.Verify(string.Join('.', parts), JwtKey.HS256(JwtTestHelpers.Secret)).Error);
    }

    [Fact]
    public void Base64UrlRejectsNonCanonicalUnusedBits()
    {
        var token = JwtTestHelpers.CreateHsToken("{\"alg\":\"HS256\"}", "{\"exp\":200}");
        var parts = token.Split('.');
        parts[0] = "AB";

        Assert.Equal(
            JwtError.Malformed,
            JwtApi.Verify(string.Join('.', parts), JwtKey.HS256(JwtTestHelpers.Secret)).Error);
    }

    [Fact]
    public void HeaderMustBeObjectWithUniqueMembers()
    {
        var key = JwtKey.HS256(JwtTestHelpers.Secret);
        var array = JwtTestHelpers.CreateHsToken("[]", "{\"exp\":200}");
        var duplicate = JwtTestHelpers.CreateHsToken(
            "{\"alg\":\"HS256\",\"alg\":\"HS256\"}",
            "{\"exp\":200}");

        Assert.Equal(JwtError.Malformed, JwtApi.Verify(array, key).Error);
        Assert.Equal(JwtError.Malformed, JwtApi.Verify(duplicate, key).Error);
    }

    [Fact]
    public void HeaderTypeIsCaseInsensitiveButCritIsUnsupported()
    {
        var key = JwtKey.HS256(JwtTestHelpers.Secret);
        var lowerType = JwtTestHelpers.CreateHsToken(
            "{\"alg\":\"HS256\",\"typ\":\"jwt\",\"kid\":\"ignored\",\"jwk\":{}}",
            "{\"exp\":200}");
        var wrongType = JwtTestHelpers.CreateHsToken(
            "{\"alg\":\"HS256\",\"typ\":\"JOSE\"}",
            "{\"exp\":200}");
        var critical = JwtTestHelpers.CreateHsToken(
            "{\"alg\":\"HS256\",\"crit\":[]}",
            "{\"exp\":200}");

        Assert.True(JwtApi.Verify(lowerType, key, JwtTestHelpers.At(100)).IsValid);
        Assert.Equal(JwtError.UnsupportedHeader, JwtApi.Verify(wrongType, key).Error);
        Assert.Equal(JwtError.UnsupportedHeader, JwtApi.Verify(critical, key).Error);
    }

    [Fact]
    public void Es256RejectsDerSignatureEncoding()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var token = JwtTestHelpers.CreateEsToken(
            "{\"alg\":\"ES256\"}",
            "{\"exp\":200}",
            ecdsa,
            DSASignatureFormat.Rfc3279DerSequence);

        Assert.Equal(
            JwtError.InvalidSignature,
            JwtApi.Verify(token, JwtKey.ES256(ecdsa), JwtTestHelpers.At(100)).Error);
    }

    [Fact]
    public void WrongLengthSignaturesAreInvalid()
    {
        var hs = JwtTestHelpers.CreateHsToken("{\"alg\":\"HS256\"}", "{\"exp\":200}");
        hs = JwtTestHelpers.ReplaceSegment(hs, 2, JwtTestHelpers.Encode(new byte[31]));
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var es = JwtTestHelpers.CreateEsToken("{\"alg\":\"ES256\"}", "{\"exp\":200}", ecdsa);
        es = JwtTestHelpers.ReplaceSegment(es, 2, JwtTestHelpers.Encode(new byte[63]));

        Assert.Equal(
            JwtError.InvalidSignature,
            JwtApi.Verify(hs, JwtKey.HS256(JwtTestHelpers.Secret), JwtTestHelpers.At(100)).Error);
        Assert.Equal(
            JwtError.InvalidSignature,
            JwtApi.Verify(es, JwtKey.ES256(ecdsa), JwtTestHelpers.At(100)).Error);
    }
}
