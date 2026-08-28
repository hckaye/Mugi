using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Miya.Middleware;

namespace Miya.Tests;

public sealed class BasicAuthTests
{
    [Fact]
    public async Task ValidCredentialsCallNext()
    {
        var app = CreateApp(new BasicAuthOptions { Username = "ada", Password = "s3cret" });

        await using var response = await Send(app, "ada", "s3cret");

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal("ok", response.BodyText);
        Assert.False(response.Response.Headers.ContainsKey("WWW-Authenticate"));
    }

    [Fact]
    public async Task WrongUserReturnsUnauthorizedWithoutCallingNext()
    {
        var called = false;
        var app = CreateApp(new BasicAuthOptions { Username = "ada", Password = "s3cret" }, () => called = true);

        await using var response = await Send(app, "grace", "s3cret");

        Assert.False(called);
        AssertUnauthorized(response);
    }

    [Fact]
    public async Task WrongPasswordReturnsUnauthorizedWithoutCallingNext()
    {
        var called = false;
        var app = CreateApp(new BasicAuthOptions { Username = "ada", Password = "s3cret" }, () => called = true);

        await using var response = await Send(app, "ada", "wrong");

        Assert.False(called);
        AssertUnauthorized(response);
    }

    [Fact]
    public async Task PasswordMayContainColons()
    {
        var app = CreateApp(new BasicAuthOptions { Username = "ada", Password = "p:ass:word" });

        await using var response = await Send(app, "ada", "p:ass:word");

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal("ok", response.BodyText);
    }

    [Fact]
    public async Task EmptyDecodedCredentialsAreRejected()
    {
        var app = CreateApp(new BasicAuthOptions { Username = "ada", Password = "s3cret" });

        await using var response = await SendRaw(app, "Basic " + Convert.ToBase64String(":"u8.ToArray()));

        AssertUnauthorized(response);
    }

    [Fact]
    public async Task EmptyAuthorizationValueIsRejected()
    {
        var app = CreateApp(new BasicAuthOptions { Username = "ada", Password = "s3cret" });

        await using var response = await TestApp.Send(
            app,
            headers: new Dictionary<string, string> { ["Authorization"] = "" });

        AssertUnauthorized(response);
    }

    [Theory]
    [InlineData("Basic")]
    [InlineData("Basic ")]
    [InlineData("Basic YQ")]
    [InlineData("Basic %%%%")]
    [InlineData("Basic abcd!!")]
    [InlineData("Basic YQ==YQ==")]
    [InlineData("Basic YQ==\n")]
    [InlineData("Basic YQ== ")]
    [InlineData("Bearer YWRhOnMzY3JldA==")]
    public async Task MalformedBase64OrSchemeReturnsUnauthorized(string header)
    {
        var app = CreateApp(new BasicAuthOptions { Username = "ada", Password = "s3cret" });

        await using var response = await SendRaw(app, header);

        AssertUnauthorized(response);
    }

    [Fact]
    public async Task NonUtf8BytesReturnUnauthorized()
    {
        var app = CreateApp(new BasicAuthOptions { Username = "ada", Password = "s3cret" });
        var header = "Basic " + Convert.ToBase64String([0xFF, 0xFE, (byte)':', (byte)'x']);

        await using var response = await SendRaw(app, header);

        AssertUnauthorized(response);
    }

    [Fact]
    public async Task MissingHeaderReturnsUnauthorized()
    {
        var called = false;
        var app = CreateApp(new BasicAuthOptions { Username = "ada", Password = "s3cret" }, () => called = true);

        await using var response = await TestApp.Send(app);

        Assert.False(called);
        AssertUnauthorized(response);
    }

    [Fact]
    public async Task SchemeIsCaseInsensitive()
    {
        var app = CreateApp(new BasicAuthOptions { Username = "ada", Password = "s3cret" });
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes("ada:s3cret"));

        await using var response = await SendRaw(app, "basic " + token);

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
    }

    [Fact]
    public async Task HeaderNameIsMatchedCaseInsensitively()
    {
        var app = CreateApp(new BasicAuthOptions { Username = "ada", Password = "s3cret" });
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes("ada:s3cret"));

        await using var response = await TestApp.Send(
            app,
            headers: new Dictionary<string, string> { ["authorization"] = "Basic " + token });

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
    }

    [Fact]
    public async Task MissingColonIsRejected()
    {
        var app = CreateApp(new BasicAuthOptions { Username = "ada", Password = "s3cret" });
        var header = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("ada"));

        await using var response = await SendRaw(app, header);

        AssertUnauthorized(response);
    }

    [Fact]
    public async Task ValidateCallbackModeAcceptsMatchingCredentials()
    {
        var seenUser = "";
        var seenPassword = "";
        var app = CreateApp(new BasicAuthOptions
        {
            Validate = (user, password) =>
            {
                seenUser = user;
                seenPassword = password;
                return user == "ada" && password == "callback:pass";
            },
        });

        await using var response = await Send(app, "ada", "callback:pass");

        Assert.Equal("ada", seenUser);
        Assert.Equal("callback:pass", seenPassword);
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
    }

    [Fact]
    public async Task ValidateCallbackModeRejectsFailedCallback()
    {
        var app = CreateApp(new BasicAuthOptions
        {
            Validate = static (user, password) => user == "ada" && password == "ok",
        });

        await using var response = await Send(app, "ada", "nope");

        AssertUnauthorized(response);
    }

    [Fact]
    public void NeitherModeThrowsAtFactory()
    {
        var exception = Assert.Throws<ArgumentException>(() => BasicAuth.Middleware(new BasicAuthOptions()));
        Assert.Contains("Username and Password", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UsernameWithoutPasswordThrowsAtFactory()
    {
        Assert.Throws<ArgumentException>(() => BasicAuth.Middleware(new BasicAuthOptions
        {
            Username = "ada",
        }));
    }

    [Fact]
    public void BothModesThrowAtFactory()
    {
        var exception = Assert.Throws<ArgumentException>(() => BasicAuth.Middleware(new BasicAuthOptions
        {
            Username = "ada",
            Password = "s3cret",
            Validate = static (_, _) => true,
        }));
        Assert.Contains("not both", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RealmWithQuoteThrowsAtFactory()
    {
        var exception = Assert.Throws<ArgumentException>(() => BasicAuth.Middleware(new BasicAuthOptions
        {
            Username = "ada",
            Password = "s3cret",
            Realm = "Staff \"HQ\"",
        }));
        Assert.Contains("realm", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Staff\rHQ")]
    [InlineData("Staff\nHQ")]
    public void RealmWithCrLfThrowsAtFactory(string realm)
    {
        Assert.Throws<ArgumentException>(() => BasicAuth.Middleware(new BasicAuthOptions
        {
            Username = "ada",
            Password = "s3cret",
            Realm = realm,
        }));
    }

    [Fact]
    public async Task ChallengeQuotesRealmAndDeclaresUtf8()
    {
        var app = CreateApp(new BasicAuthOptions
        {
            Username = "ada",
            Password = "s3cret",
            Realm = "Staff Area",
        });

        await using var response = await TestApp.Send(app);

        Assert.Equal(
            "Basic realm=\"Staff Area\", charset=\"UTF-8\"",
            response.Response.Headers["WWW-Authenticate"].ToString());
    }

    [Fact]
    public async Task TypedMiddlewareStoresAuthenticatedUsername()
    {
        var app = new App<AuthContext>();
        app.Use(BasicAuth.Middleware<AuthContext>(new BasicAuthOptions
        {
            Username = "ada",
            Password = "s3cret",
        }));
        app.Get("/", context => context.Text(context.AuthUser ?? ""));

        await using var response = await Send(app, "ada", "s3cret");

        Assert.Equal("ada", response.BodyText);
    }

    [Fact]
    public async Task ConstantTimePathRejectsLengthMismatchedUserAndPassword()
    {
        var app = CreateApp(new BasicAuthOptions { Username = "ada", Password = "s3cret" });

        await using var shortUser = await Send(app, "a", "s3cret");
        await using var longUser = await Send(app, "adaaaaa", "s3cret");
        await using var shortPassword = await Send(app, "ada", "x");
        await using var longPassword = await Send(app, "ada", "s3cret!!!!");

        AssertUnauthorized(shortUser);
        AssertUnauthorized(longUser);
        AssertUnauthorized(shortPassword);
        AssertUnauthorized(longPassword);
    }

    [Fact]
    public void FixedCredentialComparisonUsesEqualLengthDigests()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        Span<byte> expected = stackalloc byte[BasicAuth.ComparisonDigestSize];
        Span<byte> supplied = stackalloc byte[BasicAuth.ComparisonDigestSize];
        BasicAuth.ComputeComparisonDigest(key, "ada", expected);
        BasicAuth.ComputeComparisonDigest(key, "a much longer supplied user name", supplied);

        Assert.Equal(SHA256.HashSizeInBytes, expected.Length);
        Assert.Equal(expected.Length, supplied.Length);
        Assert.False(CryptographicOperations.FixedTimeEquals(expected, supplied));
    }

    [Fact]
    public async Task EmptyUsernameAndPasswordCanBeConfigured()
    {
        var app = CreateApp(new BasicAuthOptions { Username = "", Password = "" });

        await using var response = await Send(app, "", "");

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
    }

    private static App CreateApp(BasicAuthOptions options, Action? onHandler = null)
    {
        var app = new App();
        app.Use(BasicAuth.Middleware(options));
        app.Get("/", context =>
        {
            onHandler?.Invoke();
            return context.Text("ok");
        });
        return app;
    }

    private static Task<TestExchange> Send(App app, string user, string password) =>
        SendRaw(app, "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password)));

    private static Task<TestExchange> Send<TContext>(App<TContext> app, string user, string password)
        where TContext : Context, new() =>
        TestApp.Send(
            app,
            headers: new Dictionary<string, string>
            {
                ["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password)),
            });

    private static Task<TestExchange> SendRaw(App app, string authorization) =>
        TestApp.Send(app, headers: new Dictionary<string, string> { ["Authorization"] = authorization });

    private static void AssertUnauthorized(TestExchange response)
    {
        Assert.Equal(StatusCodes.Status401Unauthorized, response.Response.StatusCode);
        Assert.Equal("Unauthorized", response.BodyText);
        Assert.Equal(
            "Basic realm=\"Restricted\", charset=\"UTF-8\"",
            response.Response.Headers["WWW-Authenticate"].ToString());
        Assert.StartsWith("text/plain", response.Response.Headers.ContentType.ToString(), StringComparison.Ordinal);
    }

    private sealed class AuthContext : Context, IAuthContext
    {
        public string? AuthUser { get; set; }
    }
}
