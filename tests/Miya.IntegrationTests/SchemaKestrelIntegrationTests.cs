using System.Net;
using System.Text;
using Miya.Schema;

namespace Miya.IntegrationTests;

public sealed class SchemaKestrelIntegrationTests
{
    private static readonly SchemaPart<ITransportPaging> TransportPaging =
        Schemas.Part<ITransportPaging>()
            .Query(input => input.Page, rules => rules.Default(1).Range(1, 50))
            .Header(input => input.RequestId, "X-Request-Id");

    [Fact(Timeout = 20_000)]
    public async Task Schema_part_binding_works_through_kestrel()
    {
        var app = new App();
        var schema = Schemas.For<TransportPagingInput>().Use(TransportPaging);
        app.Get(
            "/items",
            schema,
            static (context, input) => context.Text(input.Page + ":" + input.RequestId));

        await using var server = await StartAsync(app);
        using var client = CreateClient(server);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/items?Page=2");
        request.Headers.Add("X-Request-Id", "transport-42");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("2:transport-42", await response.Content.ReadAsStringAsync());
    }

    [Fact(Timeout = 20_000)]
    public async Task Urlencoded_form_schema_binding_works_through_kestrel()
    {
        var app = new App();
        var schema = Schemas.For<UrlEncodedInput>()
            .Form(input => input.Name)
            .Form(input => input.Age);
        app.Post("/form", schema, static (context, input) => context.Text(input.Name + ":" + input.Age));

        await using var server = await StartAsync(app);
        using var client = CreateClient(server);
        using var content = new StringContent(
            "Name=Ada+Lovelace&Age=37",
            Encoding.UTF8,
            "application/x-www-form-urlencoded");
        using var response = await client.PostAsync("/form", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Ada Lovelace:37", await response.Content.ReadAsStringAsync());
    }

    [Fact(Timeout = 20_000)]
    public async Task Multipart_form_schema_binding_works_through_kestrel()
    {
        var app = new App();
        var schema = Schemas.For<MultipartInput>()
            .Form(input => input.Name)
            .Form(input => input.Age);
        app.Post("/form", schema, static (context, input) => context.Text(input.Name + ":" + input.Age));

        await using var server = await StartAsync(app);
        using var client = CreateClient(server);
        using var content = new MultipartFormDataContent("schema-kestrel-boundary");
        content.Add(new StringContent("Ada"), "Name");
        content.Add(new StringContent("42"), "Age");
        using var file = new ByteArrayContent("not-bound"u8.ToArray());
        content.Add(file, "upload", "ignored.txt");
        using var response = await client.PostAsync("/form", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Ada:42", await response.Content.ReadAsStringAsync());
    }

    private static Task<Server> StartAsync(App app) => app.StartAsync(new AppOptions
    {
        Port = 0,
        ShutdownTimeout = TimeSpan.FromSeconds(2),
    });

    private static HttpClient CreateClient(Server server) => new()
    {
        BaseAddress = new Uri(server.Addresses[0]),
        Timeout = TimeSpan.FromSeconds(10),
    };

    internal sealed record UrlEncodedInput(string Name, int Age);

    internal sealed record MultipartInput(string Name, int Age);

    internal interface ITransportPaging
    {
        int Page { get; }

        string RequestId { get; }
    }

    internal sealed record TransportPagingInput(int Page, string RequestId) : ITransportPaging;
}
