// OpenApiClientIntegration.Generated.cs was produced from OpenApiClientIntegrationSpec.json
// with OpenApiClientGenerator.Generate. Keeping that output in this test project lets the
// generated client and its codec registration run against the real Kestrel transport.
using System.Net;
using Microsoft.AspNetCore.Http;
using Mugi.IntegrationTests.Client;

namespace Mugi.IntegrationTests;

public sealed class OpenApiClientIntegrationTests
{
    [Fact(Timeout = 10_000)]
    public async Task Generated_client_round_trips_parameters_body_unicode_and_errors()
    {
        var app = new App();
        app.Get("/items/:id", context =>
        {
            var id = context.Param("id");
            if (id == "missing")
            {
                context.Status(StatusCodes.Status404NotFound);
                return context.Text("gone");
            }

            if (id == "long-error")
            {
                context.Status(StatusCodes.Status404NotFound);
                return context.Text(new string('x', 5_000));
            }

            return context.Json(new Item(id, context.Query("note") ?? "omitted", "get"));
        });
        app.Post("/items", async context =>
        {
            var body = await context.Req.Json<CreateItemBody>();
            if (body is null)
            {
                context.Status(StatusCodes.Status400BadRequest);
                await context.Text("body required");
                return;
            }

            context.Status(StatusCodes.Status201Created);
            await context.Json(new Item(body.Name, body.Note ?? "omitted", "post"));
        });

        await using var server = await app.StartAsync(new AppOptions
        {
            Port = 0,
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        });
        using var http = new HttpClient
        {
            BaseAddress = new Uri(server.Addresses[0]),
            Timeout = TimeSpan.FromSeconds(5),
        };
        var client = new E2eClient(http);

        var unicode = await client.GetItem("東京・猫");
        Assert.Equal("東京・猫", unicode.Id);
        Assert.Equal("omitted", unicode.Name);
        Assert.Equal("get", unicode.Note);

        var queried = await client.GetItem("東京・猫", "メモ & 値");
        Assert.Equal("メモ & 値", queried.Name);

        var created = await client.CreateItem(new CreateItemBody("名前", "本文🙂"));
        Assert.Equal("名前", created.Id);
        Assert.Equal("本文🙂", created.Name);
        Assert.Equal("post", created.Note);

        var exception = await Assert.ThrowsAsync<ApiException>(() => client.GetItem("missing"));
        Assert.Equal((int)HttpStatusCode.NotFound, exception.Status);
        Assert.Equal("gone", exception.Body);

        var longException = await Assert.ThrowsAsync<ApiException>(() => client.GetItem("long-error"));
        Assert.Equal(4_096, longException.Body!.Length);
    }
}
