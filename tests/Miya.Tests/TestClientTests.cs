using System.Buffers;
using System.Text;
using Microsoft.AspNetCore.Http;
using Miya.Json;
using Miya.Testing;

namespace Miya.Tests;

public sealed class TestClientTests
{
    [Fact]
    public async Task GetRoundTripReturnsStatusBodyAndContentType()
    {
        var app = new App();
        app.Get("/", context => context.Text("Hello"));

        var response = await app.Request("GET", "/");

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Equal("Hello", response.Text());
        Assert.Equal("text/plain; charset=utf-8", response.Header("Content-Type"));
        Assert.Equal("5", response.Header("Content-Length"));
        Assert.Null(response.Header("Date"));
        Assert.Null(response.Header("Server"));
    }

    [Fact]
    public async Task PostRoundTripReadsBodyAndEchoesIt()
    {
        var app = new App();
        app.Post("/echo", async context =>
        {
            var text = await context.Req.Text();
            await context.Text(text);
        });

        var response = await app.Request(
            "POST",
            "/echo",
            new TestRequestOptions { Body = Encoding.UTF8.GetBytes("payload") });

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Equal("payload", response.Text());
        Assert.True(response.Body.Span.SequenceEqual("payload"u8));
    }

    [Fact]
    public async Task QueryStringIsParsedFromTheTarget()
    {
        string? full = null;
        string? missing = null;
        var app = new App();
        app.Get("/users/:id", context =>
        {
            full = context.Query("full");
            missing = context.Query("missing");
            return context.Text(context.Param("id"));
        });

        var response = await app.Request("GET", "/users/42?full=1");

        Assert.Equal("42", response.Text());
        Assert.Equal("1", full);
        Assert.Null(missing);
    }

    [Fact]
    public async Task RequestHeadersReachTheHandler()
    {
        string? user = null;
        string? missing = null;
        var app = new App();
        app.Get("/", context =>
        {
            user = context.Req.Header("X-User");
            missing = context.Req.Header("X-Missing");
            return context.Text("ok");
        });

        var response = await app.Request(
            "GET",
            "/",
            new TestRequestOptions
            {
                Headers = [new KeyValuePair<string, string>("X-User", "ada")],
            });

        Assert.Equal("ok", response.Text());
        Assert.Equal("ada", user);
        Assert.Null(missing);
    }

    [Fact]
    public async Task RepeatedRequestHeadersAreConcatenatedForHeaderLookup()
    {
        string? forwarded = null;
        var app = new App();
        app.Get("/", context =>
        {
            forwarded = context.Req.Header("X-Forwarded-For");
            return context.Text("ok");
        });

        await app.Request(
            "GET",
            "/",
            new TestRequestOptions
            {
                Headers =
                [
                    new KeyValuePair<string, string>("X-Forwarded-For", "1.1.1.1"),
                    new KeyValuePair<string, string>("X-Forwarded-For", "2.2.2.2"),
                ],
            });

        Assert.Equal("1.1.1.1,2.2.2.2", forwarded);
    }

    [Fact]
    public async Task ByteBodyReachesTheHandlerAndSetsContentLength()
    {
        string? contentLength = null;
        var app = new App();
        app.Post("/", async context =>
        {
            contentLength = context.Req.Header("Content-Length");
            var text = await context.Req.Text();
            await context.Text(text);
        });

        var response = await app.Request(
            "POST",
            "/",
            new TestRequestOptions { Body = "raw-bytes"u8.ToArray() });

        Assert.Equal("raw-bytes", response.Text());
        Assert.Equal("9", contentLength);
    }

    [Fact]
    public async Task TextBodyIsEncodedAsUtf8AndSetsContentLength()
    {
        string? contentLength = null;
        var app = new App();
        app.Post("/", async context =>
        {
            contentLength = context.Req.Header("Content-Length");
            var text = await context.Req.Text();
            await context.Text(text);
        });

        var response = await app.Request(
            "POST",
            "/",
            new TestRequestOptions { TextBody = "こんにちは" });

        Assert.Equal("こんにちは", response.Text());
        Assert.Equal(Encoding.UTF8.GetByteCount("こんにちは").ToString(), contentLength);
    }

    [Fact]
    public async Task EmptyTextBodyIsProvidedAndSetsContentLengthZero()
    {
        string? contentLength = null;
        string? body = null;
        var app = new App();
        app.Post("/", async context =>
        {
            contentLength = context.Req.Header("Content-Length");
            body = await context.Req.Text();
            await context.Text("ok");
        });

        await app.Request("POST", "/", new TestRequestOptions { TextBody = "" });

        Assert.Equal("0", contentLength);
        Assert.Equal("", body);
    }

    [Fact]
    public void SettingBothBodyAndTextBodyThrows()
    {
        var app = new App();
        app.Post("/", context => context.Text("unreachable"));

        var exception = Assert.Throws<ArgumentException>(() =>
        {
            _ = app.Request(
                "POST",
                "/",
                new TestRequestOptions
                {
                    Body = "bytes"u8.ToArray(),
                    TextBody = "text",
                });
        });

        Assert.Equal("options", exception.ParamName);
        Assert.Contains("Body and TextBody", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NullMethodOrTargetThrows()
    {
        var app = new App();

        Assert.Throws<ArgumentNullException>(() => { _ = app.Request(null!, "/"); });
        Assert.Throws<ArgumentException>(() => { _ = app.Request("", "/"); });
        Assert.Throws<ArgumentNullException>(() => { _ = app.Request("GET", null!); });
    }

    [Fact]
    public async Task NullOrEmptyRequestHeaderNameThrows()
    {
        var app = new App();
        app.Get("/", context => context.Text("ok"));

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            app.Request(
                "GET",
                "/",
                new TestRequestOptions
                {
                    Headers = [new KeyValuePair<string, string>(null!, "x")],
                }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            app.Request(
                "GET",
                "/",
                new TestRequestOptions
                {
                    Headers = [new KeyValuePair<string, string>("", "x")],
                }));
    }

    [Fact]
    public async Task MethodIsNormalizedToUppercase()
    {
        var app = new App();
        app.Post("/resource", context => context.Text("posted"));

        var response = await app.Request("post", "/resource");

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Equal("posted", response.Text());
    }

    [Fact]
    public async Task ExistingContentLengthHeaderIsNotReplaced()
    {
        string? contentLength = null;
        var app = new App();
        app.Post("/", context =>
        {
            contentLength = context.Req.Header("Content-Length");
            return context.Text("ok");
        });

        await app.Request(
            "POST",
            "/",
            new TestRequestOptions
            {
                Body = "hello"u8.ToArray(),
                Headers = [new KeyValuePair<string, string>("Content-Length", "3")],
            });

        Assert.Equal("3", contentLength);
    }

    [Fact]
    public async Task JsonResponseIsDecodedThroughRegisteredCodec()
    {
        global::Miya.Json.Json.Register(TestClientUserCodec.Instance);
        var app = new App();
        app.Get("/user", context => context.Json(new TestClientUser("42", "Ada")));

        var response = await app.Request("GET", "/user");
        var user = response.Json<TestClientUser>();

        Assert.Equal("application/json; charset=utf-8", response.Header("content-type"));
        Assert.NotNull(user);
        Assert.Equal("42", user.Id);
        Assert.Equal("Ada", user.Name);
    }

    [Fact]
    public async Task JsonDecodeThrowsWhenNoCodecIsRegistered()
    {
        var app = new App();
        app.Get("/", context => context.Text("{\"x\":1}"));

        var response = await app.Request("GET", "/");

        var exception = Assert.Throws<JsonException>(() => response.Json<UnregisteredDto>());
        Assert.Contains("No Json codec", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamedResponseBodyIsCollectedInFull()
    {
        var app = new App();
        app.Get("/", context => context.Stream(
            "text/plain",
            static async (writer, cancellationToken) =>
            {
                writer.Write("one"u8);
                await writer.FlushAsync(cancellationToken);
                writer.Write("-two"u8);
                await writer.FlushAsync(cancellationToken);
                writer.Write("-three"u8);
            }));

        var response = await app.Request("GET", "/");

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Equal("one-two-three", response.Text());
        Assert.Equal("text/plain", response.Header("Content-Type"));
        Assert.Null(response.Header("Content-Length"));
    }

    [Fact]
    public async Task MissingRouteReturnsNotFoundLikeTestApp()
    {
        var app = new App();
        app.Get("/", context => context.Text("Hello"));

        await using var viaSend = await TestApp.Send(app, path: "/missing");
        var viaRequest = await app.Request("GET", "/missing");

        Assert.Equal(StatusCodes.Status404NotFound, viaSend.Response.StatusCode);
        Assert.Equal("Not Found", viaSend.BodyText);
        Assert.Equal(viaSend.Response.StatusCode, viaRequest.Status);
        Assert.Equal(viaSend.BodyText, viaRequest.Text());
    }

    [Fact]
    public async Task MethodMismatchReturns405LikeTestApp()
    {
        var app = new App();
        app.Get("/resource", context => context.Text("get"));
        app.Post("/resource", context => context.Text("post"));

        await using var viaSend = await TestApp.Send(app, method: "DELETE", path: "/resource");
        var viaRequest = await app.Request("DELETE", "/resource");

        Assert.Equal(StatusCodes.Status405MethodNotAllowed, viaSend.Response.StatusCode);
        Assert.Equal("GET, HEAD, POST, OPTIONS", viaSend.Response.Headers.Allow.ToString());
        Assert.Empty(viaSend.BodyText);
        Assert.Equal(viaSend.Response.StatusCode, viaRequest.Status);
        Assert.Equal(viaSend.BodyText, viaRequest.Text());
        Assert.Equal(viaSend.Response.Headers.Allow.ToString(), viaRequest.Header("Allow"));
    }

    [Fact]
    public async Task CustomNotFoundMatchesTestApp()
    {
        var app = new App();
        app.Get("/", context => context.Text("Hello"));
        app.NotFound(context =>
        {
            context.Status(StatusCodes.Status404NotFound);
            return context.Text("gone");
        });

        await using var viaSend = await TestApp.Send(app, path: "/nope");
        var viaRequest = await app.Request("GET", "/nope");

        Assert.Equal(StatusCodes.Status404NotFound, viaSend.Response.StatusCode);
        Assert.Equal("gone", viaSend.BodyText);
        Assert.Equal(viaSend.Response.StatusCode, viaRequest.Status);
        Assert.Equal(viaSend.BodyText, viaRequest.Text());
    }

    [Fact]
    public async Task CustomOnErrorMatchesTestApp()
    {
        var app = new App();
        app.Get("/boom", context => throw new InvalidOperationException("boom"));
        app.OnError((context, exception) =>
        {
            context.Status(599);
            return context.Text(exception.Message);
        });

        await using var viaSend = await TestApp.Send(app, path: "/boom");
        var viaRequest = await app.Request("GET", "/boom");

        Assert.Equal(599, viaSend.Response.StatusCode);
        Assert.Equal("boom", viaSend.BodyText);
        Assert.Equal(viaSend.Response.StatusCode, viaRequest.Status);
        Assert.Equal(viaSend.BodyText, viaRequest.Text());
    }

    [Fact]
    public async Task MiddlewareRunsForInProcessRequests()
    {
        var order = new List<string>();
        var app = new App();
        app.Use(async (context, next) =>
        {
            order.Add("before");
            await next(context);
            order.Add("after");
            context.Header("X-After", "yes");
        });
        app.Get("/", context =>
        {
            order.Add("handler");
            return context.Text("ok");
        });

        var response = await app.Request("GET", "/");

        Assert.Equal(["before", "handler", "after"], order);
        Assert.Equal("ok", response.Text());
        Assert.Equal("yes", response.Header("X-After"));
    }

    [Fact]
    public async Task TypedAppContextIsUsed()
    {
        var app = new App<TestClientContext>();
        app.Get("/:id", context =>
        {
            context.Seen = context.Param("id");
            return context.Text(context.Seen);
        });

        var response = await app.Request("GET", "/typed");

        Assert.Equal("typed", response.Text());
    }

    [Fact]
    public async Task HeaderLookupIsCaseInsensitiveAndMissingReturnsNull()
    {
        var app = new App();
        app.Get("/", context =>
        {
            context.Header("X-Trace", "abc");
            return context.Text("ok");
        });

        var response = await app.Request("GET", "/");

        Assert.Equal("abc", response.Header("x-trace"));
        Assert.Equal("abc", response.Header("X-TRACE"));
        Assert.Null(response.Header("X-Missing"));
        Assert.Empty(response.HeaderValues("X-Missing"));
        Assert.Throws<ArgumentException>(() => response.Header(""));
        Assert.Throws<ArgumentException>(() => response.HeaderValues(""));
    }

    [Fact]
    public async Task RepeatedSetCookieHeadersAreExposedIndividually()
    {
        var app = new App();
        app.Get("/", context =>
        {
            context.AppendHeader("Set-Cookie", "a=1");
            context.AppendHeader("Set-Cookie", "b=2");
            return context.Text("ok");
        });

        var response = await app.Request("GET", "/");

        Assert.Equal("a=1", response.Header("set-cookie"));
        Assert.Equal(["a=1", "b=2"], response.HeaderValues("SET-COOKIE"));

        var cookieEntries = new List<string>();
        for (var i = 0; i < response.Headers.Count; i++)
        {
            if (string.Equals(response.Headers[i].Key, "Set-Cookie", StringComparison.OrdinalIgnoreCase))
            {
                cookieEntries.Add(response.Headers[i].Value);
            }
        }

        Assert.Equal(["a=1", "b=2"], cookieEntries);
    }

    [Fact(Timeout = 10_000)]
    public async Task ConcurrentRequestsOnOneAppInstanceAreSafe()
    {
        var app = new App();
        app.Get("/:id", context => context.Text(context.Param("id")));

        var tasks = new Task<TestResponse>[32];
        for (var i = 0; i < tasks.Length; i++)
        {
            var id = i.ToString();
            tasks[i] = app.Request("GET", "/" + id);
        }

        var results = await Task.WhenAll(tasks);

        for (var i = 0; i < results.Length; i++)
        {
            Assert.Equal(StatusCodes.Status200OK, results[i].Status);
            Assert.Equal(i.ToString(), results[i].Text());
        }
    }

    [Fact]
    public async Task PublicRequestMatchesTestAppStatusAndBody()
    {
        var app = new App();
        app.Use(async (context, next) =>
        {
            await next(context);
            context.Header("X-Mw", "1");
        });
        app.Get("/users/:id", context => context.Text($"user:{context.Param("id")}:{context.Query("full")}"));

        await using var viaSend = await TestApp.Send(app, path: "/users/42", queryString: "?full=1");
        var viaRequest = await app.Request("GET", "/users/42?full=1");

        Assert.Equal(viaSend.Response.StatusCode, viaRequest.Status);
        Assert.Equal(viaSend.BodyText, viaRequest.Text());
        Assert.Equal(viaSend.Response.Headers["X-Mw"].ToString(), viaRequest.Header("X-Mw"));
        Assert.Equal(
            viaSend.Response.Headers.ContentType.ToString(),
            viaRequest.Header("Content-Type"));
    }

    [Fact]
    public async Task PathWithoutLeadingSlashIsRejectedLikeThePipeline()
    {
        var app = new App();
        app.Get("/users", context => context.Text("ok"));

        var response = await app.Request("GET", "users");

        Assert.Equal(StatusCodes.Status400BadRequest, response.Status);
        Assert.Equal("Bad Request", response.Text());
    }

    [Fact]
    public async Task DefaultOnErrorHandlesThrownExceptions()
    {
        var app = new App();
        app.Get("/boom", context => throw new InvalidOperationException("hidden"));

        var response = await app.Request("GET", "/boom");

        Assert.Equal(StatusCodes.Status500InternalServerError, response.Status);
        Assert.Equal("Internal Server Error", response.Text());
    }

    public sealed class TestClientContext : Context
    {
        public string? Seen { get; set; }
    }

    private sealed class UnregisteredDto
    {
        public int X { get; set; }
    }
}

internal sealed class TestClientUser
{
    public TestClientUser()
    {
    }

    public TestClientUser(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Id { get; set; } = "";

    public string Name { get; set; } = "";
}

internal sealed class TestClientUserCodec : IJsonCodec<TestClientUser>
{
    public static TestClientUserCodec Instance { get; } = new();

    public void Write(ref JsonWriter writer, TestClientUser? value)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteRaw("{\"id\":"u8);
        writer.WriteString(value.Id);
        writer.WriteRaw(",\"name\":"u8);
        writer.WriteString(value.Name);
        writer.WriteRaw("}"u8);
    }

    public TestClientUser? Read(ref JsonReader reader)
    {
        if (reader.TryReadNull())
        {
            return null;
        }

        string? id = null;
        string? name = null;
        reader.ReadBeginObject();
        while (!reader.TryReadEndObject())
        {
            var property = reader.ReadPropertyName();
            if (property.SequenceEqual("id"u8))
            {
                id = reader.ReadString();
            }
            else if (property.SequenceEqual("name"u8))
            {
                name = reader.ReadString();
            }
            else
            {
                reader.SkipValue();
            }
        }

        return new TestClientUser(id ?? "", name ?? "");
    }
}
