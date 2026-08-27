using System.Net;
using System.Text;
using Miya.Json;

namespace Miya.IntegrationTests;

public sealed class JsonIntegrationTests
{
    private const int TestTimeoutMilliseconds = 10_000;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task GetReturnsCamelCaseJsonWithUtf8ContentType()
    {
        var app = CreateApp();

        await using var server = await StartAsync(app);
        using var client = CreateClient(server);
        using var response = await client.GetAsync("/users/42");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal("{\"id\":42,\"name\":\"Ada\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task PostReadsAndReturnsJsonBody()
    {
        var app = CreateApp();

        await using var server = await StartAsync(app);
        using var client = CreateClient(server);
        using var content = new StringContent(
            "{\"id\":7,\"name\":\"Grace\"}",
            Encoding.UTF8,
            "application/json");
        using var response = await client.PostAsync("/users", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal("{\"id\":7,\"name\":\"Grace\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task InvalidJsonIsMappedToBadRequestByDefaultErrorHandler()
    {
        var app = CreateApp();

        await using var server = await StartAsync(app);
        using var client = CreateClient(server);
        using var content = new StringContent(
            "{\"id\":",
            Encoding.UTF8,
            "application/json");
        using var response = await client.PostAsync("/users", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Bad Request", await response.Content.ReadAsStringAsync());
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task BufferedJsonPromotesToStreamingWithoutResettingConnection()
    {
        var promoted = false;
        var app = new App();
        app.Get("/large", context =>
        {
            var operation = context.Json(
                new PromotionPayload("123456789"),
                PromotionPayloadCodec.Instance);
            promoted = context.ResponseStarted;
            return operation;
        });

        await using var server = await app.StartAsync(new Options
        {
            Port = 0,
            MaxBufferedResponseBytes = 10,
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        });
        using var client = CreateClient(server);
        using var response = await client.GetAsync("/large");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(promoted);
        Assert.Equal("{\"value\":\"123456789\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task MissingOutputCodecIsMappedToInternalServerError()
    {
        var app = new App();
        app.Get("/missing-codec", context => WriteGenericJson(
            context,
            new UnregisteredOutputPayload(42)));

        await using var server = await StartAsync(app);
        using var client = CreateClient(server);
        using var response = await client.GetAsync("/missing-codec");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("Internal Server Error", await response.Content.ReadAsStringAsync());
    }

    private static App CreateApp()
    {
        global::Miya.Json.Json.Register(JsonUserCodec.Instance);

        var app = new App();
        app.Get("/users/:id", context =>
            context.Json(new JsonUser(int.Parse(context.Param("id")), "Ada")));
        app.Post("/users", async context =>
        {
            var user = await context.Req.Json<JsonUser>();
            await context.Json(user);
        });
        return app;
    }

    private static ValueTask WriteGenericJson<T>(Context context, T value) => context.Json(value);

    private static Task<Server> StartAsync(App app) => app.StartAsync(
        new Options
        {
            Port = 0,
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        });

    private static HttpClient CreateClient(Server server) => new()
    {
        BaseAddress = new Uri(server.Addresses[0]),
        Timeout = OperationTimeout,
    };
}

internal sealed record JsonUser(int Id, string Name);

internal sealed record PromotionPayload(string Value);

internal sealed record UnregisteredOutputPayload(int Value);

internal sealed class PromotionPayloadCodec : IJsonCodec<PromotionPayload>
{
    public static PromotionPayloadCodec Instance { get; } = new();

    public void Write(ref JsonWriter writer, PromotionPayload? value)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteRaw("{\"value\":"u8);
        writer.Flush();
        writer.WriteString(value.Value);
        writer.WriteRaw("}"u8);
    }

    public PromotionPayload? Read(ref JsonReader reader) => throw new NotSupportedException();
}

internal sealed class JsonUserCodec : IJsonCodec<JsonUser>
{
    public static JsonUserCodec Instance { get; } = new();

    public void Write(ref JsonWriter writer, JsonUser? value)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteRaw("{\"id\":"u8);
        writer.WriteNumber(value.Id);
        writer.WriteRaw(",\"name\":"u8);
        writer.WriteString(value.Name);
        writer.WriteRaw("}"u8);
    }

    public JsonUser? Read(ref JsonReader reader)
    {
        if (reader.TryReadNull())
        {
            return null;
        }

        var id = 0;
        var name = string.Empty;
        reader.ReadBeginObject();
        while (!reader.TryReadEndObject())
        {
            var propertyName = reader.ReadPropertyName();
            if (propertyName.SequenceEqual("id"u8))
            {
                id = reader.ReadInt32();
            }
            else if (propertyName.SequenceEqual("name"u8))
            {
                name = reader.ReadString() ?? throw new JsonException("The name cannot be null.");
            }
            else
            {
                reader.SkipValue();
            }
        }

        return new JsonUser(id, name);
    }
}
