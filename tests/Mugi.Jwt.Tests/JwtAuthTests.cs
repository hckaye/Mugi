using System.Net;
using Mugi;
using JwtApi = Mugi.Jwt.Jwt;

namespace Mugi.Jwt.Tests;

public sealed class JwtAuthTests
{
    private static readonly JwtKey Key = JwtKey.HS256(JwtTestHelpers.Secret);

    [Fact(Timeout = 10_000)]
    public async Task MissingAuthorizationReturnsBearerChallenge()
    {
        var app = CreateApp();
        await using var server = await Start(app);
        using var client = Client(server);

        using var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer realm=\"Restricted\"", Challenge(response));
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Theory(Timeout = 10_000)]
    [InlineData("")]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    [InlineData("Basic abc")]
    [InlineData("Token abc")]
    [InlineData("Bearer not-a-jwt")]
    public async Task MalformedAuthorizationReturnsInvalidTokenChallenge(string authorization)
    {
        var app = CreateApp();
        await using var server = await Start(app);
        using var client = Client(server);
        using var request = Request(authorization);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(
            "Bearer realm=\"Restricted\", error=\"invalid_token\"",
            Challenge(response));
    }

    [Fact(Timeout = 10_000)]
    public async Task InvalidSignatureAndExpiredTokensReturnInvalidTokenChallenge()
    {
        var valid = JwtApi.Sign(
            new JwtPayload { Subject = "alice", ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(200) },
            Key);
        var invalid = JwtTestHelpers.ReplaceSegment(valid, 2, JwtTestHelpers.Encode(new byte[32]));
        var expired = JwtApi.Sign(
            new JwtPayload { ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(100) },
            Key);
        var app = CreateApp();
        await using var server = await Start(app);
        using var client = Client(server);

        using var invalidRequest = Request(string.Concat("Bearer ", invalid));
        using var invalidResponse = await client.SendAsync(invalidRequest);
        using var expiredRequest = Request(string.Concat("Bearer ", expired));
        using var expiredResponse = await client.SendAsync(expiredRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, invalidResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, expiredResponse.StatusCode);
        Assert.Equal(
            "Bearer realm=\"Restricted\", error=\"invalid_token\"",
            Challenge(invalidResponse));
        Assert.Equal(
            "Bearer realm=\"Restricted\", error=\"invalid_token\"",
            Challenge(expiredResponse));
    }

    [Fact(Timeout = 10_000)]
    public async Task ValidBearerTokenCallsNextAndSchemeIsCaseInsensitive()
    {
        var token = JwtApi.Sign(
            new JwtPayload { ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(200) },
            Key);
        var app = CreateApp();
        await using var server = await Start(app);
        using var client = Client(server);
        using var request = Request(string.Concat("bEaReR\t", token));

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("next", await response.Content.ReadAsStringAsync());
        Assert.False(response.Headers.Contains("WWW-Authenticate"));
    }

    [Fact(Timeout = 10_000)]
    public async Task TypedMiddlewareStoresVerifiedPayload()
    {
        var token = JwtApi.Sign(
            new JwtPayload
            {
                Subject = "alice",
                ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(200),
            },
            Key);
        var app = new App<AuthContext>();
        app.Use(JwtAuth.Middleware<AuthContext>(Options()));
        app.Get("/", static context => context.Text(context.Jwt?.Subject ?? "missing"));
        await using var server = await Start(app);
        using var client = Client(server);
        using var request = Request(string.Concat("Bearer ", token));

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("alice", await response.Content.ReadAsStringAsync());
    }

    [Fact(Timeout = 10_000)]
    public async Task RealmQuotesAndBackslashesAreEscapedInChallenge()
    {
        var app = new App();
        app.Use(JwtAuth.Middleware(new JwtAuthOptions
        {
            Key = Key,
            Realm = "quoted \"realm\" \\ path",
        }));
        app.Get("/", static context => context.Text("next"));
        await using var server = await Start(app);
        using var client = Client(server);

        using var response = await client.GetAsync("/");

        Assert.Equal(
            "Bearer realm=\"quoted \\\"realm\\\" \\\\ path\"",
            Challenge(response));
    }

    [Fact]
    public void MiddlewareValidatesOptionsAtCreation()
    {
        Assert.Throws<ArgumentNullException>(() => JwtAuth.Middleware(null!));
        Assert.Throws<ArgumentNullException>(() => JwtAuth.Middleware(new JwtAuthOptions { Key = null! }));
        Assert.Throws<ArgumentException>(() => JwtAuth.Middleware(new JwtAuthOptions
        {
            Key = Key,
            Realm = "bad\rrealm",
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => JwtAuth.Middleware(new JwtAuthOptions
        {
            Key = Key,
            Validation = new JwtValidation { ClockSkew = TimeSpan.FromTicks(-1) },
        }));
    }

    private static App CreateApp()
    {
        var app = new App();
        app.Use(JwtAuth.Middleware(Options()));
        app.Get("/", static context => context.Text("next"));
        return app;
    }

    private static JwtAuthOptions Options() => new()
    {
        Key = Key,
        Validation = new JwtValidation
        {
            Clock = static () => DateTimeOffset.FromUnixTimeSeconds(100),
            ClockSkew = TimeSpan.Zero,
        },
    };

    private static async Task<Server> Start<TContext>(App<TContext> app)
        where TContext : Context, new() => await app.StartAsync(new AppOptions
        {
            Port = 0,
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        });

    private static HttpClient Client(Server server) => new()
    {
        BaseAddress = new Uri(server.Addresses[0]),
    };

    private static HttpRequestMessage Request(string authorization)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return request;
    }

    private static string Challenge(HttpResponseMessage response) =>
        Assert.Single(response.Headers.GetValues("WWW-Authenticate"));

    private sealed class AuthContext : Context, IJwtContext
    {
        public AuthContext()
        {
        }

        public JwtPayload? Jwt { get; set; }
    }
}
