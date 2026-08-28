using System.Security.Cryptography;
using JwtApi = Miya.Jwt.Jwt;

namespace Miya.Jwt.Tests;

public sealed class Rfc7515VectorTests
{
    private const string Payload =
        "eyJpc3MiOiJqb2UiLA0KICJleHAiOjEzMDA4MTkzODAsDQogImh0dHA6Ly9leGFt" +
        "cGxlLmNvbS9pc19yb290Ijp0cnVlfQ";

    private static readonly JwtValidation Validation = JwtTestHelpers.At(1_300_819_300);

    [Fact]
    public void AppendixA1Hs256VectorVerifiesAndPayloadCanBeResigned()
    {
        const string token =
            "eyJ0eXAiOiJKV1QiLA0KICJhbGciOiJIUzI1NiJ9." + Payload +
            ".dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        var secret = JwtTestHelpers.Decode(
            "AyM1SysPpbyDfgZld3umj1qzKObwVMkoqQ-EstJQLr_T-1qS0gZH75" +
            "aKtMN3Yj0iPS4hcgUuTwjAzZr1Z9CAow");
        var key = JwtKey.HS256(secret);

        var result = JwtApi.Verify(token, key, Validation);
        var resigned = JwtApi.Sign(result.Payload!, key);
        var resignedResult = JwtApi.Verify(resigned, key, Validation);

        Assert.True(result.IsValid);
        Assert.Equal("joe", result.Payload!.Issuer);
        Assert.True(result.Payload.GetBool("http://example.com/is_root"));
        Assert.True(resignedResult.IsValid);
        Assert.True(resignedResult.Payload!.GetBool("http://example.com/is_root"));
    }

    [Fact]
    public void AppendixA2Rs256VectorVerifies()
    {
        const string token =
            "eyJhbGciOiJSUzI1NiJ9." + Payload + "." +
            "cC4hiUPoj9Eetdgtv3hF80EGrhuB__dzERat0XF9g2VtQgr9PJbu3XOiZj5RZmh7" +
            "AAuHIm4Bh-0Qc_lF5YKt_O8W2Fp5jujGbds9uJdbF9CUAr7t1dnZcAcQjbKBYNX4" +
            "BAynRFdiuB--f_nZLgrnbyTyWzO75vRK5h6xBArLIARNPvkSjtQBMHlb1L07Qe7K" +
            "0GarZRmB_eSN9383LcOLn6_dO--xi12jzDwusC-eOkHWEsqtFZESc6BfI7noOPqv" +
            "hJ1phCnvWh6IeYI2w9QOYEUipUTI8np6LbgGY9Fs98rqVt5AXLIhWkWywlVmtVrB" +
            "p0igcN_IoypGlUPQGe77Rw";
        using var rsa = RSA.Create(new RSAParameters
        {
            Modulus = JwtTestHelpers.Decode(
                "ofgWCuLjybRlzo0tZWJjNiuSfb4p4fAkd_wWJcyQoTbji9k0l8W26mPddx" +
                "HmfHQp-Vaw-4qPCJrcS2mJPMEzP1Pt0Bm4d4QlL-yRT-SFd2lZS-pCgNMs" +
                "D1W_YpRPEwOWvG6b32690r2jZ47soMZo9wGzjb_7OMg0LOL-bSf63kpaSH" +
                "SXndS5z5rexMdbBYUsLA9e-KXBdQOS-UTo7WTBEMa2R2CapHg665xsmtdV" +
                "MTBQY4uDZlxvb3qCo5ZwKh9kG4LT6_I5IhlJH7aGhyxXFvUK-DWNmoudF8" +
                "NAco9_h9iaGNj8q2ethFkMLs91kzk2PAcDTW9gb54h4FRWyuXpoQ"),
            Exponent = JwtTestHelpers.Decode("AQAB"),
        });

        var result = JwtApi.Verify(token, JwtKey.RS256(rsa), Validation);

        Assert.True(result.IsValid);
        Assert.Equal("joe", result.Payload!.Issuer);
        Assert.True(result.Payload.GetBool("http://example.com/is_root"));
    }

    [Fact]
    public void AppendixA3Es256VectorVerifies()
    {
        const string token =
            "eyJhbGciOiJFUzI1NiJ9." + Payload + "." +
            "DtEhU3ljbEg8L38VWAfUAqOyKAM6-Xx-F4GawxaepmXFCgfTjDxw5djxLa8ISlSA" +
            "pmWQxfKTUJqPP3-Kg6NU1Q";
        using var ecdsa = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = JwtTestHelpers.Decode("f83OJ3D2xF1Bg8vub9tLe1gHMzV76e8Tus9uPHvRVEU"),
                Y = JwtTestHelpers.Decode("x_FEzRu9m36HLN_tue659LNpXW6pCyStikYjKIWI5a0"),
            },
        });

        var result = JwtApi.Verify(token, JwtKey.ES256(ecdsa), Validation);

        Assert.True(result.IsValid);
        Assert.Equal("joe", result.Payload!.Issuer);
        Assert.True(result.Payload.GetBool("http://example.com/is_root"));
    }
}
