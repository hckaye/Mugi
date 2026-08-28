# Mugi.Jwt

Mugi.Jwt signs and verifies compact JWTs for the [Mugi](https://www.nuget.org/packages/Mugi) web framework without reflection, so it works under NativeAOT. It supports HS256, RS256, and ES256, and ships an authentication middleware.

## Sign and verify

```csharp
using Mugi.Jwt;

var key = JwtKey.HS256("01234567890123456789012345678901"u8);
var token = Jwt.Sign(
    new JwtPayload
    {
        Subject = "alice",
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
    },
    key);

var result = Jwt.Verify(token, key);
if (result.IsValid)
{
    Console.WriteLine(result.Payload!.Subject);
}
```

`Jwt.Verify` checks the signature and registered claims, then returns a `JwtResult` rather than throwing for an invalid token. Verification fixes the accepted algorithm to the supplied key: it rejects `none`, unknown algorithms, and tokens whose algorithm does not match the key before signature validation.

`JwtKey.HS256` copies a secret of at least 32 bytes, `JwtKey.RS256` accepts an RSA key of at least 2048 bits, and `JwtKey.ES256` accepts an ECDSA key on NIST P-256. `JwtPayload` carries the registered claims and adds scalar string, integer, and Boolean claims with `WithClaim`. `JwtValidation` can require an exact `Issuer`, require an `Audience`, set `ClockSkew`, control `RequireExpiration`, and supply a `Clock`.

## Middleware

`JwtAuth.Middleware` validates a bearer token before calling the next handler. Missing or invalid tokens return 401 with a Bearer challenge. The generic overload stores the verified payload on a typed context:

```csharp
using Mugi;
using Mugi.Jwt;

public sealed class ApiContext : Context, IJwtContext
{
    public JwtPayload? Jwt { get; set; }
}

var api = new App<ApiContext>();
api.Use(JwtAuth.Middleware<ApiContext>(new JwtAuthOptions { Key = key }));
```

Full documentation is at [github.com/hckaye/Mugi](https://github.com/hckaye/Mugi).
