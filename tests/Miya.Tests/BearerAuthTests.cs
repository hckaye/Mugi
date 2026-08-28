using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Miya.Middleware;

namespace Miya.Tests;

public sealed class BearerAuthTests
{
    [Fact]
    public async Task ValidTokenCallsNext()
    {
        var app = CreateApp(new BearerAuthOptions { Token = "s3cret-token" });

        await using var response = await Send(app, "Bearer s3cret-token");

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal("ok", response.BodyText);
        Assert.False(response.Response.Headers.ContainsKey("WWW-Authenticate"));
    }

    [Fact]
    public async Task WrongTokenReturnsInvalidToken()
    {
        var called = false;
        var app = CreateApp(new BearerAuthOptions { Token = "s3cret-token" }, () => called = true);

        await using var response = await Send(app, "Bearer wrong-token");

        Assert.False(called);
        AssertUnauthorized(response, error: "invalid_token");
    }

    [Fact]
    public async Task MissingHeaderReturnsBareRealmChallenge()
    {
        var called = false;
        var app = CreateApp(new BearerAuthOptions { Token = "s3cret-token" }, () => called = true);

        await using var response = await TestApp.Send(app);

        Assert.False(called);
        Assert.Equal(StatusCodes.Status401Unauthorized, response.Response.StatusCode);
        Assert.Equal("Unauthorized", response.BodyText);
        Assert.Equal("Bearer realm=\"Restricted\"", response.Response.Headers["WWW-Authenticate"].ToString());
    }

    [Theory]
    [InlineData("Token s3cret-token")]
    [InlineData("Basic s3cret-token")]
    [InlineData("Bear s3cret-token")]
    public async Task MalformedSchemeReturnsMissingStyleChallenge(string header)
    {
        var app = CreateApp(new BearerAuthOptions { Token = "s3cret-token" });

        await using var response = await Send(app, header);

        Assert.Equal(StatusCodes.Status401Unauthorized, response.Response.StatusCode);
        Assert.Equal("Bearer realm=\"Restricted\"", response.Response.Headers["WWW-Authenticate"].ToString());
    }

    [Theory]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    [InlineData("Bearer  s3cret-token")]
    [InlineData("Bearers3cret-token")]
    [InlineData("Bearer s3cret token")]
    public async Task MalformedBearerLayoutReturnsInvalidRequest(string header)
    {
        var app = CreateApp(new BearerAuthOptions { Token = "s3cret-token" });

        await using var response = await Send(app, header);

        AssertInvalidRequest(response);
    }

    [Theory]
    [InlineData("Bearer hello!")]
    [InlineData("Bearer abc=def")]
    [InlineData("Bearer =abc")]
    [InlineData("Bearer tok\ten")]
    public async Task BadCharsetReturnsInvalidRequest(string header)
    {
        var app = CreateApp(new BearerAuthOptions { Token = "s3cret-token" });

        await using var response = await Send(app, header);

        AssertInvalidRequest(response);
    }

    [Fact]
    public async Task SchemeIsCaseInsensitive()
    {
        var app = CreateApp(new BearerAuthOptions { Token = "s3cret-token" });

        await using var response = await Send(app, "bearer s3cret-token");

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
    }

    [Fact]
    public async Task HeaderNameIsMatchedCaseInsensitively()
    {
        var app = CreateApp(new BearerAuthOptions { Token = "s3cret-token" });

        await using var response = await TestApp.Send(
            app,
            headers: new Dictionary<string, string> { ["AUTHORIZATION"] = "Bearer s3cret-token" });

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
    }

    [Fact]
    public async Task TokenMayIncludeRfc6750Padding()
    {
        var app = CreateApp(new BearerAuthOptions { Token = "abc==" });

        await using var response = await Send(app, "Bearer abc==");

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
    }

    [Fact]
    public async Task ValidateCallbackModeAcceptsMatchingToken()
    {
        var seen = "";
        var app = CreateApp(new BearerAuthOptions
        {
            Validate = token =>
            {
                seen = token;
                return token == "callback-token";
            },
        });

        await using var response = await Send(app, "Bearer callback-token");

        Assert.Equal("callback-token", seen);
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
    }

    [Fact]
    public async Task ValidateCallbackModeRejectsFailedCallback()
    {
        var app = CreateApp(new BearerAuthOptions
        {
            Validate = static token => token == "ok",
        });

        await using var response = await Send(app, "Bearer nope");

        AssertUnauthorized(response, error: "invalid_token");
    }

    [Fact]
    public async Task ConstantTimePathRejectsSameLengthAndDifferentLengthTokens()
    {
        var app = CreateApp(new BearerAuthOptions { Token = "s3cret-token" });

        await using var sameLength = await Send(app, "Bearer x3cret-token");
        await using var shorter = await Send(app, "Bearer s3cret");
        await using var longer = await Send(app, "Bearer s3cret-token-extra");

        AssertUnauthorized(sameLength, error: "invalid_token");
        AssertUnauthorized(shorter, error: "invalid_token");
        AssertUnauthorized(longer, error: "invalid_token");
    }

    [Fact]
    public void FixedTokenComparisonUsesEqualLengthDigests()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        Span<byte> expected = stackalloc byte[BearerAuth.ComparisonDigestSize];
        Span<byte> supplied = stackalloc byte[BearerAuth.ComparisonDigestSize];
        BearerAuth.ComputeComparisonDigest(key, "short", expected);
        BearerAuth.ComputeComparisonDigest(key, "a-much-longer-supplied-token", supplied);

        Assert.Equal(SHA256.HashSizeInBytes, expected.Length);
        Assert.Equal(expected.Length, supplied.Length);
        Assert.False(CryptographicOperations.FixedTimeEquals(expected, supplied));
    }

    [Fact]
    public void NeitherModeThrowsAtFactory()
    {
        var exception = Assert.Throws<ArgumentException>(() => BearerAuth.Middleware(new BearerAuthOptions()));
        Assert.Contains("exactly one", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BothModesThrowAtFactory()
    {
        Assert.Throws<ArgumentException>(() => BearerAuth.Middleware(new BearerAuthOptions
        {
            Token = "s3cret-token",
            Validate = static _ => true,
        }));
    }

    [Fact]
    public void InvalidConfiguredTokenCharsetThrowsAtFactory()
    {
        Assert.Throws<ArgumentException>(() => BearerAuth.Middleware(new BearerAuthOptions
        {
            Token = "not valid!",
        }));
    }

    [Fact]
    public void RealmWithQuoteThrowsAtFactory()
    {
        Assert.Throws<ArgumentException>(() => BearerAuth.Middleware(new BearerAuthOptions
        {
            Token = "s3cret-token",
            Realm = "api\"prod",
        }));
    }

    [Fact]
    public async Task ChallengeQuotesCustomRealm()
    {
        var app = CreateApp(new BearerAuthOptions
        {
            Token = "s3cret-token",
            Realm = "api",
        });

        await using var response = await TestApp.Send(app);

        Assert.Equal("Bearer realm=\"api\"", response.Response.Headers["WWW-Authenticate"].ToString());
    }

    [Fact]
    public async Task TypedMiddlewareStoresValidatedToken()
    {
        var app = new App<AuthContext>();
        app.Use(BearerAuth.Middleware<AuthContext>(new BearerAuthOptions
        {
            Token = "s3cret-token",
        }));
        app.Get("/", context => context.Text(context.AuthUser ?? ""));

        await using var response = await TestApp.Send(
            app,
            headers: new Dictionary<string, string> { ["Authorization"] = "Bearer s3cret-token" });

        Assert.Equal("s3cret-token", response.BodyText);
    }

    [Fact]
    public async Task EmptyAuthorizationHeaderIsTreatedAsMissing()
    {
        var app = CreateApp(new BearerAuthOptions { Token = "s3cret-token" });

        await using var response = await TestApp.Send(
            app,
            headers: new Dictionary<string, string> { ["Authorization"] = "" });

        Assert.Equal(StatusCodes.Status401Unauthorized, response.Response.StatusCode);
        Assert.Equal("Bearer realm=\"Restricted\"", response.Response.Headers["WWW-Authenticate"].ToString());
    }

    private static App CreateApp(BearerAuthOptions options, Action? onHandler = null)
    {
        var app = new App();
        app.Use(BearerAuth.Middleware(options));
        app.Get("/", context =>
        {
            onHandler?.Invoke();
            return context.Text("ok");
        });
        return app;
    }

    private static Task<TestExchange> Send(App app, string authorization) =>
        TestApp.Send(app, headers: new Dictionary<string, string> { ["Authorization"] = authorization });

    private static void AssertUnauthorized(TestExchange response, string error)
    {
        Assert.Equal(StatusCodes.Status401Unauthorized, response.Response.StatusCode);
        Assert.Equal("Unauthorized", response.BodyText);
        Assert.Equal(
            $"Bearer realm=\"Restricted\", error=\"{error}\"",
            response.Response.Headers["WWW-Authenticate"].ToString());
    }

    private static void AssertInvalidRequest(TestExchange response)
    {
        Assert.Equal(StatusCodes.Status400BadRequest, response.Response.StatusCode);
        Assert.Equal("Bad Request", response.BodyText);
        Assert.Equal(
            "Bearer realm=\"Restricted\", error=\"invalid_request\"",
            response.Response.Headers["WWW-Authenticate"].ToString());
    }

    private sealed class AuthContext : Context, IAuthContext
    {
        public string? AuthUser { get; set; }
    }
}
