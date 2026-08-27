using System.Text;
using Microsoft.AspNetCore.Http;

namespace Miya.Tests;

public sealed class RequestTests
{
    [Fact]
    public async Task RequestBodyCanOnlyBeConsumedOnce()
    {
        Exception? observed = null;
        var app = new App();
        app.Post("/", async context =>
        {
            var text = await context.Req.Text();
            observed = await Record.ExceptionAsync(async () => await context.Req.Text());
            await context.Text(text);
        });

        await using var response = await TestApp.Send(
            app,
            method: "POST",
            body: Encoding.UTF8.GetBytes("request"));

        Assert.IsType<InvalidOperationException>(observed);
        Assert.Equal("request", response.BodyText);
    }

    [Fact]
    public async Task BodyReaderClaimsTheRequestBody()
    {
        Exception? observed = null;
        var app = new App();
        app.Post("/", async context =>
        {
            _ = context.Req.BodyReader;
            observed = await Record.ExceptionAsync(async () => await context.Req.Text());
            await context.Text("ok");
        });

        await using var response = await TestApp.Send(app, method: "POST");

        Assert.IsType<InvalidOperationException>(observed);
        Assert.Equal("ok", response.BodyText);
    }

    [Fact]
    public async Task ContentLengthIsCheckedBeforeReadingBody()
    {
        var app = new App();
        app.Post("/", async context =>
        {
            _ = await context.Req.Text();
            await context.Text("unreachable");
        });

        await using var response = await TestApp.Send(
            app,
            method: "POST",
            body: Encoding.UTF8.GetBytes("12345"),
            headers: new Dictionary<string, string> { ["Content-Length"] = "5" },
            options: new MiyaOptions { MaxRequestBodyBytes = 4 });

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, response.Response.StatusCode);
        Assert.Equal("Payload Too Large", response.BodyText);
    }

    [Fact]
    public async Task ChunkedBodyIsStoppedAtConfiguredLimit()
    {
        var app = new App();
        app.Post("/", async context =>
        {
            _ = await context.Req.Text();
            await context.Text("unreachable");
        });

        await using var response = await TestApp.Send(
            app,
            method: "POST",
            body: Encoding.UTF8.GetBytes("12345"),
            options: new MiyaOptions { MaxRequestBodyBytes = 4 });

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, response.Response.StatusCode);
    }

    [Fact]
    public async Task QueryParsingIsDecodedAndUsesFirstValue()
    {
        var app = new App();
        app.Get("/", context => context.Text(
            $"{context.Query("name")}|{context.Req.Query("missing") ?? "none"}"));

        await using var response = await TestApp.Send(
            app,
            queryString: "?name=hello+world&name=ignored&encoded=a%2Fb");

        Assert.Equal("hello world|none", response.BodyText);
    }
}
