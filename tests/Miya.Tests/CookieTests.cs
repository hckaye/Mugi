using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Features;

namespace Miya.Tests;

public sealed class CookieTests
{
    private static readonly byte[] SigningKey = "0123456789abcdef0123456789abcdef"u8.ToArray();

    [Fact]
    public async Task CookieParsesQuotedValuesSpacesMalformedPairsAndDuplicates()
    {
        string? plain = null;
        string? quoted = null;
        string? spaced = null;
        string? empty = null;
        string? duplicate = null;
        var app = new App();
        app.Get("/", context =>
        {
            plain = context.Req.Cookie("plain");
            quoted = context.Req.Cookie("quoted");
            spaced = context.Req.Cookie("spaced");
            empty = context.Req.Cookie("empty");
            duplicate = context.Req.Cookie("duplicate");
            return context.Text("ok");
        });

        await using var response = await TestApp.Send(app, headers: new Dictionary<string, string>
        {
            ["Cookie"] = "broken; =missing; bad name=skip; plain=value; quoted=\"two words\"; " +
                " spaced = around ; empty=; duplicate=first; duplicate=second",
        });

        Assert.Equal("value", plain);
        Assert.Equal("two words", quoted);
        Assert.Equal("around", spaced);
        Assert.Equal(string.Empty, empty);
        Assert.Equal("first", duplicate);
    }

    [Fact]
    public async Task CookieReturnsNullForEmptyHeaderAndMissingName()
    {
        string? value = "not read";
        var app = new App();
        app.Get("/", context =>
        {
            value = context.Req.Cookie("missing");
            return context.Text("ok");
        });

        await using var response = await TestApp.Send(app, headers: new Dictionary<string, string>
        {
            ["Cookie"] = string.Empty,
        });

        Assert.Null(value);
    }

    [Fact]
    public async Task CookieHandlesHugeHeader()
    {
        var expected = new string('x', 100_000);
        string? value = null;
        var app = new App();
        app.Get("/", context =>
        {
            value = context.Req.Cookie("large");
            return context.Text("ok");
        });

        await using var response = await TestApp.Send(app, headers: new Dictionary<string, string>
        {
            ["Cookie"] = string.Concat("large=", expected),
        });

        Assert.Equal(expected, value);
    }

    [Fact]
    public async Task CookieParsingIsCachedForOneRequest()
    {
        string? first = null;
        string? second = null;
        var app = new App();
        app.Get("/", context =>
        {
            first = context.Req.Cookie("value");
            var request = context.Features.Get<IHttpRequestFeature>()!;
            request.Headers.Cookie = "value=changed";
            second = context.Req.Cookie("value");
            return context.Text("ok");
        });

        await using var response = await TestApp.Send(app, headers: new Dictionary<string, string>
        {
            ["Cookie"] = "value=original",
        });

        Assert.Equal("original", first);
        Assert.Equal("original", second);
    }

    [Fact]
    public async Task CookieCacheIsClearedWhenContextIsReused()
    {
        var values = new List<string?>();
        var app = new App();
        app.Get("/", context =>
        {
            values.Add(context.Req.Cookie("value"));
            return context.Text("ok");
        });

        await using (var first = await TestApp.Send(app, headers: new Dictionary<string, string>
        {
            ["Cookie"] = "value=first",
        }))
        {
        }

        await using (var second = await TestApp.Send(app, headers: new Dictionary<string, string>
        {
            ["Cookie"] = "value=second",
        }))
        {
        }

        Assert.Equal(["first", "second"], values);
    }

    [Fact]
    public async Task SetCookieWritesAllAttributesInRequiredOrder()
    {
        var expires = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var header = await WriteCookie(context => context.SetCookie("session", "abc-123", new CookieOptions
        {
            MaxAge = TimeSpan.FromSeconds(90),
            Expires = expires,
            Domain = "example.com",
            Path = "/account",
            Secure = true,
            HttpOnly = true,
            SameSite = SameSite.Strict,
        }));

        Assert.Equal(
            "session=abc-123; Max-Age=90; Expires=Wed, 02 Jan 2030 03:04:05 GMT; " +
            "Domain=example.com; Path=/account; Secure; HttpOnly; SameSite=Strict",
            header);
    }

    [Fact]
    public async Task SetCookieUsesDefaultPathAndSameSite()
    {
        var header = await WriteCookie(context => context.SetCookie("name", "value"));

        Assert.Equal("name=value; Path=/; SameSite=Lax", header);
    }

    [Fact]
    public async Task SetCookieAppendsMultipleHeaders()
    {
        var app = new App();
        app.Get("/", context =>
        {
            context.SetCookie("first", "one");
            context.SetCookie("second", "two");
            return context.Text("ok");
        });

        await using var response = await TestApp.Send(app);

        Assert.Equal(2, response.Response.Headers.SetCookie.Count);
        Assert.Equal("first=one; Path=/; SameSite=Lax", response.Response.Headers.SetCookie[0]);
        Assert.Equal("second=two; Path=/; SameSite=Lax", response.Response.Headers.SetCookie[1]);
    }

    [Fact]
    public async Task DeleteCookieWritesEmptyValueAndZeroMaxAge()
    {
        var header = await WriteCookie(context => context.DeleteCookie("session", new CookieOptions
        {
            MaxAge = TimeSpan.FromDays(10),
            Domain = "example.com",
            Path = "/account",
            Secure = true,
            HttpOnly = true,
            SameSite = SameSite.Strict,
        }));

        Assert.Equal(
            "session=; Max-Age=0; Domain=example.com; Path=/account; Secure; HttpOnly; SameSite=Strict",
            header);
    }

    [Fact]
    public async Task SameSiteNoneRequiresSecure()
    {
        var exception = await CaptureCookieException(context => context.SetCookie("name", "value", new CookieOptions
        {
            SameSite = SameSite.None,
        }));

        var invalid = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("browsers reject", invalid.Message, StringComparison.Ordinal);

        var header = await WriteCookie(context => context.SetCookie("name", "value", new CookieOptions
        {
            Secure = true,
            SameSite = SameSite.None,
        }));
        Assert.Equal("name=value; Path=/; Secure; SameSite=None", header);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("has,comma")]
    [InlineData("has;semicolon")]
    [InlineData("has=equals")]
    [InlineData("has/slash")]
    [InlineData("has\\backslash")]
    [InlineData("has\"quote")]
    [InlineData("line\r\nbreak")]
    [InlineData("日本語")]
    public async Task CookieNameRejectsInvalidTokens(string name)
    {
        var exception = await CaptureCookieException(context => context.SetCookie(name, "value"));

        Assert.IsType<ArgumentException>(exception);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("has,comma")]
    [InlineData("has;semicolon")]
    [InlineData("has\\backslash")]
    [InlineData("has\"quote")]
    [InlineData("line\r\nbreak")]
    [InlineData("control\u0001")]
    [InlineData("日本語")]
    public async Task CookieValueRejectsUnsafeCharacters(string value)
    {
        var exception = await CaptureCookieException(context => context.SetCookie("name", value));

        Assert.IsType<ArgumentException>(exception);
    }

    [Fact]
    public async Task CookieAttributesRejectSemicolonsControlsAndCrlf()
    {
        var pathSemicolon = await CaptureCookieException(context => context.SetCookie("name", "value", new CookieOptions
        {
            Path = "/safe; injected=true",
        }));
        var pathControl = await CaptureCookieException(context => context.SetCookie("name", "value", new CookieOptions
        {
            Path = "/safe\u0001",
        }));
        var domainCrlf = await CaptureCookieException(context => context.SetCookie("name", "value", new CookieOptions
        {
            Domain = "example.com\r\nInjected: true",
        }));

        Assert.IsType<ArgumentException>(pathSemicolon);
        Assert.IsType<ArgumentException>(pathControl);
        Assert.IsType<ArgumentException>(domainCrlf);
    }

    [Theory]
    [InlineData("")]
    [InlineData("example .com")]
    [InlineData("example..com")]
    [InlineData("-example.com")]
    [InlineData("example-.com")]
    [InlineData("..example.com")]
    [InlineData("miyä.example")]
    public async Task CookieDomainRejectsInvalidHosts(string domain)
    {
        var exception = await CaptureCookieException(context => context.SetCookie("name", "value", new CookieOptions
        {
            Domain = domain,
        }));

        Assert.IsType<ArgumentException>(exception);
    }

    [Fact]
    public async Task CookieDomainAllowsASingleLeadingDot()
    {
        var header = await WriteCookie(context => context.SetCookie("name", "value", new CookieOptions
        {
            Domain = ".example.com",
        }));

        Assert.Contains("Domain=.example.com", header, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CookiePrefixesEnforceBrowserRequirements()
    {
        var hostWithoutSecure = await CaptureCookieException(context =>
            context.SetCookie("__Host-token", "value"));
        var hostWrongPath = await CaptureCookieException(context =>
            context.SetCookie("__Host-token", "value", new CookieOptions
            {
                Secure = true,
                Path = "/account",
            }));
        var hostWithDomain = await CaptureCookieException(context =>
            context.SetCookie("__Host-token", "value", new CookieOptions
            {
                Secure = true,
                Domain = "example.com",
            }));
        var secureWithoutSecure = await CaptureCookieException(context =>
            context.SetCookie("__Secure-token", "value"));

        Assert.IsType<ArgumentException>(hostWithoutSecure);
        Assert.IsType<ArgumentException>(hostWrongPath);
        Assert.IsType<ArgumentException>(hostWithDomain);
        Assert.IsType<ArgumentException>(secureWithoutSecure);

        var header = await WriteCookie(context => context.SetCookie("__Host-token", "value", new CookieOptions
        {
            Secure = true,
        }));
        Assert.Equal("__Host-token=value; Path=/; Secure; SameSite=Lax", header);
    }

    [Fact]
    public async Task SetCookieAllowsExactly4096BytesAndRejectsMore()
    {
        var maximumValue = new string('x', 4072);
        var header = await WriteCookie(context => context.SetCookie("a", maximumValue));
        var exception = await CaptureCookieException(context => context.SetCookie("a", string.Concat(maximumValue, "x")));

        Assert.Equal(4096, Encoding.UTF8.GetByteCount(header));
        Assert.IsType<ArgumentException>(exception);
    }

    [Fact]
    public async Task SetCookieSizeLimitCountsUtf8Bytes()
    {
        var exception = await CaptureCookieException(context => context.SetCookie("a", "value", new CookieOptions
        {
            Path = string.Concat("/", new string('\u00e9', 2040)),
        }));

        Assert.IsType<ArgumentException>(exception);
    }

    [Fact]
    public async Task SignedCookieRoundTripsAndUsesExpectedEncoding()
    {
        var pair = await WriteSignedCookiePair("signed-value", SigningKey);
        var dot = pair.LastIndexOf('.');
        var expectedSignature = Convert.ToBase64String(
                HMACSHA256.HashData(SigningKey, CreateSignedCookieMacInput("token", "signed-value")))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        Assert.Equal("token=signed-value", pair[..dot]);
        Assert.Equal(expectedSignature, pair[(dot + 1)..]);
        Assert.Equal("signed-value", await ReadSignedCookie(pair, SigningKey));
    }

    [Fact]
    public async Task SignedCookieCannotBeReplayedUnderAnotherName()
    {
        var pair = await WriteSignedCookiePair("source", "signed-value", SigningKey);
        var signedValue = pair[(pair.IndexOf('=') + 1)..];

        Assert.Null(await ReadSignedCookie("target", string.Concat("target=", signedValue), SigningKey));
        Assert.Equal("signed-value", await ReadSignedCookie("source", pair, SigningKey));
    }

    [Fact]
    public async Task SignedCookieAcceptsValuesContainingDots()
    {
        var pair = await WriteSignedCookiePair("one.two.three", SigningKey);

        Assert.Equal("one.two.three", await ReadSignedCookie(pair, SigningKey));
    }

    [Fact]
    public async Task SignedCookieAcceptsEmptyValueAndReturnsNullWhenAbsent()
    {
        var pair = await WriteSignedCookiePair(string.Empty, SigningKey);

        Assert.Equal(string.Empty, await ReadSignedCookie(pair, SigningKey));
        Assert.Null(await ReadSignedCookie("other=value", SigningKey));
    }

    [Fact]
    public async Task SignedCookieReturnsNullForTamperingAndMalformedSignatures()
    {
        var pair = await WriteSignedCookiePair("original", SigningKey);
        var equals = pair.IndexOf('=');
        var signedValue = pair[(equals + 1)..];
        var dot = signedValue.LastIndexOf('.');
        var signature = signedValue[(dot + 1)..];
        var changedSignature = string.Concat(
            (signature[0] == 'A' ? 'B' : 'A').ToString(),
            signature[1..]);

        Assert.Null(await ReadSignedCookie(string.Concat("token=changed.", signature), SigningKey));
        Assert.Null(await ReadSignedCookie(string.Concat("token=original.", changedSignature), SigningKey));
        Assert.Null(await ReadSignedCookie(pair, "fedcba9876543210fedcba9876543210"u8.ToArray()));
        Assert.Null(await ReadSignedCookie(string.Concat("token=", signedValue[..^1]), SigningKey));
        Assert.Null(await ReadSignedCookie("token=original", SigningKey));
        Assert.Null(await ReadSignedCookie(string.Concat("token=original.", new string('!', 43)), SigningKey));
    }

    [Fact]
    public async Task SignedCookieRequiresAtLeast32KeyBytes()
    {
        var setException = await CaptureCookieException(context =>
            context.SetSignedCookie("token", "value", new byte[31]));

        Exception? readException = null;
        var app = new App();
        app.Get("/", context =>
        {
            readException = Record.Exception(() => context.Req.SignedCookie("token", new byte[31]));
            return context.Text("ok");
        });
        await using var response = await TestApp.Send(app, headers: new Dictionary<string, string>
        {
            ["Cookie"] = "token=value.signature",
        });

        Assert.IsType<ArgumentException>(setException);
        Assert.IsType<ArgumentException>(readException);
    }

    [Fact]
    public async Task SignedCookieAcceptsA32ByteKey()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        var pair = await WriteSignedCookiePair("value", key);

        Assert.Equal("value", await ReadSignedCookie(pair, key));
    }

    [Fact]
    public async Task CookieReadRejectsInvalidName()
    {
        Exception? observed = null;
        var app = new App();
        app.Get("/", context =>
        {
            observed = Record.Exception(() => context.Req.Cookie("bad name"));
            return context.Text("ok");
        });

        await using var response = await TestApp.Send(app);

        Assert.IsType<ArgumentException>(observed);
    }

    private static async Task<string> WriteCookie(Action<Context> write)
    {
        var app = new App();
        app.Get("/", context =>
        {
            write(context);
            return context.Text("ok");
        });

        await using var response = await TestApp.Send(app);
        return Assert.Single(response.Response.Headers.SetCookie)!;
    }

    private static async Task<Exception?> CaptureCookieException(Action<Context> action)
    {
        Exception? observed = null;
        var app = new App();
        app.Get("/", context =>
        {
            observed = Record.Exception(() => action(context));
            return context.Text("ok");
        });

        await using var response = await TestApp.Send(app);
        return observed;
    }

    private static async Task<string> WriteSignedCookiePair(string value, byte[] key)
        => await WriteSignedCookiePair("token", value, key);

    private static async Task<string> WriteSignedCookiePair(string name, string value, byte[] key)
    {
        var header = await WriteCookie(context => context.SetSignedCookie(name, value, key));
        var separator = header.IndexOf(';');
        return separator < 0 ? header : header[..separator];
    }

    private static async Task<string?> ReadSignedCookie(string pair, byte[] key)
        => await ReadSignedCookie("token", pair, key);

    private static async Task<string?> ReadSignedCookie(string name, string pair, byte[] key)
    {
        string? value = null;
        var app = new App();
        app.Get("/", context =>
        {
            value = context.Req.SignedCookie(name, key);
            return context.Text("ok");
        });

        await using var response = await TestApp.Send(app, headers: new Dictionary<string, string>
        {
            ["Cookie"] = pair,
        });
        return value;
    }

    private static byte[] CreateSignedCookieMacInput(string name, string value)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var valueBytes = Encoding.UTF8.GetBytes(value);
        var input = new byte[sizeof(int) + nameBytes.Length + sizeof(int) + valueBytes.Length];
        BinaryPrimitives.WriteInt32BigEndian(input, nameBytes.Length);
        nameBytes.CopyTo(input, sizeof(int));
        var valueLengthOffset = sizeof(int) + nameBytes.Length;
        BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(valueLengthOffset), valueBytes.Length);
        valueBytes.CopyTo(input, valueLengthOffset + sizeof(int));
        return input;
    }
}
