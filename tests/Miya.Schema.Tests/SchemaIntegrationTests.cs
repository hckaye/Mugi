using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Miya.Schema;

namespace Miya.Schema.Tests;

public sealed class SchemaIntegrationTests
{
    [Fact]
    public async Task Route_query_and_default_values_are_bound()
    {
        var app = new App();
        var schema = Schemas.For<SearchInput>()
            .Route(input => input.Id, rules => rules.Positive())
            .Query(input => input.Filter, rules => rules.Optional())
            .Query(input => input.Limit, rules => rules.Default(25).Range(1, 100));
        app.Get(
            "/items/:Id",
            schema,
            static (context, input) => context.Json(input));

        await using var server = await Start(app);
        using var client = Client(server);
        using var response = await client.GetAsync("/items/42?Filter=active");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(42, body.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("active", body.RootElement.GetProperty("filter").GetString());
        Assert.Equal(25, body.RootElement.GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task Body_fields_are_bound_and_validated()
    {
        var app = CreatePersonApp(out _);
        await using var server = await Start(app);
        using var client = Client(server);
        using var response = await client.PostAsJsonAsync("/people", new { name = "Ada", age = 37 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("Ada", body.RootElement.GetProperty("name").GetString());
        Assert.Equal(37, body.RootElement.GetProperty("age").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("note").ValueKind);
    }

    [Fact]
    public async Task Range_failure_returns_structured_400_without_calling_handler()
    {
        var app = CreatePersonApp(out var handlerState);
        await using var server = await Start(app);
        using var client = Client(server);
        using var response = await client.PostAsJsonAsync("/people", new { name = "Ada", age = 121 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.False(handlerState.Called);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        var error = Assert.Single(body.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Equal("age", error.GetProperty("field").GetString());
        Assert.Equal("must be between 0 and 120", error.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("{\"name\":\"Ada\",\"age\":\"old\"}")]
    [InlineData("{\"age\":37}")]
    [InlineData("{\"name\":\"Ada\",\"age\":")]
    public async Task Invalid_type_missing_required_field_and_invalid_json_return_400(string json)
    {
        var app = CreatePersonApp(out var handlerState);
        await using var server = await Start(app);
        using var client = Client(server);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/people", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(handlerState.Called);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        Assert.NotEmpty(body.RootElement.GetProperty("errors").EnumerateArray());
    }

    [Fact]
    public async Task Typed_context_receives_validated_input()
    {
        var app = new App<TestContext>();
        var schema = Schemas.For<HeaderInput>()
            .Header(input => input.RequestId, "X-Request-Id");
        app.Get(
            "/header",
            schema,
            static (context, input) =>
            {
                context.Seen = input.RequestId;
                return context.Text(input.RequestId.ToString("D"));
            });

        await using var server = await Start(app);
        using var client = Client(server);
        var id = Guid.NewGuid();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/header");
        request.Headers.Add("X-Request-Id", id.ToString("D"));
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(id.ToString("D"), await response.Content.ReadAsStringAsync());
    }

    private static App CreatePersonApp(out HandlerState state)
    {
        var app = new App();
        state = new HandlerState();
        var captured = state;
        var schema = Schemas.For<PersonInput>()
            .Body(input => input.Name, rules => rules.NotEmpty())
            .Body(input => input.Age, rules => rules.Range(0, 120))
            .Body(input => input.Note, rules => rules.Optional());
        app.Post(
            "/people",
            schema,
            async (context, input) =>
            {
                captured.Called = true;
                await context.Json(input);
            });
        return app;
    }

    private static async Task<Server> Start<C>(App<C> app)
        where C : Context, new() => await app.StartAsync(new AppOptions
        {
            Port = 0,
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        });

    private static HttpClient Client(Server server) => new()
    {
        BaseAddress = new Uri(server.Addresses[0]),
        Timeout = TimeSpan.FromSeconds(5),
    };

    private sealed class HandlerState
    {
        internal bool Called { get; set; }
    }

    internal sealed record SearchInput(int Id, string? Filter, int Limit);

    internal sealed record PersonInput(string Name, int Age, string? Note);

    internal sealed record HeaderInput(Guid RequestId);

    public sealed class TestContext : Context
    {
        internal Guid Seen { get; set; }
    }
}
