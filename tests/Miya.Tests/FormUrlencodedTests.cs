using System.Text;
using Microsoft.AspNetCore.Http;

namespace Miya.Tests;

public sealed class FormUrlencodedTests
{
    private const string ContentType = "application/x-www-form-urlencoded";

    [Fact]
    public async Task ParsesEncodingDuplicatesAndRequestOrder()
    {
        FormData? captured = null;
        var app = new App();
        app.Post("/", async context =>
        {
            captured = await context.Req.Form();
            await context.Text("ok");
        });

        await using var response = await Send(
            app,
            "name=hello+world&utf=%E3%81%BF%E3%82%84&symbol=%2B%26%3D&name=second");

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.NotNull(captured);
        Assert.Equal("hello world", captured.Get("name"));
        Assert.Equal(["hello world", "second"], captured.GetAll("name"));
        Assert.Equal("みや", captured.Get("utf"));
        Assert.Equal("+&=", captured.Get("symbol"));
        Assert.Equal(["name", "utf", "symbol", "name"], captured.Fields.Select(static field => field.Key));
        Assert.Empty(captured.Files);
        Assert.Null(captured.File("name"));
    }

    [Fact]
    public async Task IgnoresContentTypeParameters()
    {
        FormData? captured = null;
        var app = new App();
        app.Post("/", async context =>
        {
            captured = await context.Req.Form();
            await context.Text("ok");
        });

        await using var response = await Send(app, "a=b", "Application/X-Www-Form-Urlencoded; charset=UTF-8");

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal("b", Assert.IsType<FormData>(captured).Get("a"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("&&")]
    public async Task EmptyBodyAndSegmentsProduceNoFields(string body)
    {
        FormData? captured = null;
        var app = new App();
        app.Post("/", async context =>
        {
            captured = await context.Req.Form();
            await context.Text("ok");
        });

        await using var response = await Send(app, body);

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Empty(Assert.IsType<FormData>(captured).Fields);
    }

    [Fact]
    public async Task BareAndEmptyKeysArePreserved()
    {
        FormData? captured = null;
        var app = new App();
        app.Post("/", async context =>
        {
            captured = await context.Req.Form();
            await context.Text("ok");
        });

        await using var response = await Send(app, "bare&=empty-name&empty=&equals=a=b");

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal(
            [
                new KeyValuePair<string, string>("bare", string.Empty),
                new KeyValuePair<string, string>(string.Empty, "empty-name"),
                new KeyValuePair<string, string>("empty", string.Empty),
                new KeyValuePair<string, string>("equals", "a=b"),
            ],
            Assert.IsType<FormData>(captured).Fields);
    }

    [Theory]
    [InlineData("a=%")]
    [InlineData("a=%0")]
    [InlineData("a=%GG")]
    [InlineData("%GG=a")]
    [InlineData("a=%C3%28")]
    public async Task InvalidEncodingReturnsBadRequest(string body)
    {
        var app = new App();
        app.Post("/", async context =>
        {
            await context.Req.Form();
            await context.Text("unreachable");
        });

        await using var response = await Send(app, body);

        Assert.Equal(StatusCodes.Status400BadRequest, response.Response.StatusCode);
        Assert.Equal("Bad Request", response.BodyText);
    }

    [Fact]
    public async Task FieldLimitReturnsBadRequest()
    {
        var app = new App();
        app.Post("/", async context =>
        {
            await context.Req.Form();
            await context.Text("unreachable");
        });

        await using var response = await Send(
            app,
            "a=1&b=2",
            options: new AppOptions { MaxFormFields = 1 });

        Assert.Equal(StatusCodes.Status400BadRequest, response.Response.StatusCode);
    }

    [Fact]
    public async Task BodyLimitReturnsPayloadTooLarge()
    {
        var app = new App();
        app.Post("/", async context =>
        {
            await context.Req.Form();
            await context.Text("unreachable");
        });

        await using var response = await Send(
            app,
            "a=1234",
            options: new AppOptions { MaxFormBodyBytes = 4 });

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, response.Response.StatusCode);
        Assert.Equal("Payload Too Large", response.BodyText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("text/plain")]
    [InlineData("application/json")]
    public async Task WrongContentTypeReturnsUnsupportedMediaType(string? contentType)
    {
        var app = new App();
        app.Post("/", async context =>
        {
            await context.Req.Form();
            await context.Text("unreachable");
        });

        var headers = contentType is null
            ? null
            : new Dictionary<string, string> { ["Content-Type"] = contentType };
        await using var response = await TestApp.Send(
            app,
            method: "POST",
            body: "a=b"u8.ToArray(),
            headers: headers);

        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, response.Response.StatusCode);
        Assert.Equal("Unsupported Media Type", response.BodyText);
    }

    [Fact]
    public async Task FormClaimsTheBodyOnce()
    {
        Exception? captured = null;
        var app = new App();
        app.Post("/", async context =>
        {
            await context.Req.Form();
            captured = await Record.ExceptionAsync(async () => await context.Req.Text());
            await context.Text("ok");
        });

        await using var response = await Send(app, "a=b");

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.IsType<InvalidOperationException>(captured);
    }

    private static Task<TestExchange> Send(
        App app,
        string body,
        string contentType = ContentType,
        AppOptions? options = null) =>
        TestApp.Send(
            app,
            method: "POST",
            body: Encoding.UTF8.GetBytes(body),
            headers: new Dictionary<string, string>
            {
                ["Content-Type"] = contentType,
                ["Content-Length"] = Encoding.UTF8.GetByteCount(body).ToString(),
            },
            options: options);
}
