using System.Text.Json;
using Mugi.Schema;
using Mugi.Schema.Tests.Parts;
using Mugi.Testing;

namespace Mugi.Schema.Tests;

public sealed class SchemaPartIntegrationTests
{
    [Fact]
    public async Task Paging_part_is_shared_by_two_input_schemas_with_defaults_and_ranges()
    {
        var app = new App();
        var firstSchema = Schemas.For<FirstPagingInput>()
            .Query(input => input.Search)
            .Use(SharedSchemaParts.Paging);
        var secondSchema = Schemas.For<SecondPagingInput>()
            .Query(input => input.Category)
            .Use(SharedSchemaParts.Paging);
        app.Get("/first", firstSchema, static (context, input) => context.Json(input));
        app.Get("/second", secondSchema, static (context, input) => context.Json(input));

        var first = await app.Request("GET", "/first?Search=books");
        var second = await app.Request("GET", "/second?Category=tools&Page=2&PageSize=40");
        var invalid = await app.Request("GET", "/first?Search=books&Page=0");
        var mustInvalid = await app.Request("GET", "/second?Category=tools&Page=51&PageSize=40");

        Assert.Equal(200, first.Status);
        using (var body = JsonDocument.Parse(first.Body.ToArray()))
        {
            Assert.Equal(1, body.RootElement.GetProperty("page").GetInt32());
            Assert.Equal(20, body.RootElement.GetProperty("pageSize").GetInt32());
        }

        Assert.Equal(200, second.Status);
        using (var body = JsonDocument.Parse(second.Body.ToArray()))
        {
            Assert.Equal(2, body.RootElement.GetProperty("page").GetInt32());
            Assert.Equal(40, body.RootElement.GetProperty("pageSize").GetInt32());
        }

        Assert.Equal(400, invalid.Status);
        using var invalidBody = JsonDocument.Parse(invalid.Body.ToArray());
        Assert.Contains(
            invalidBody.RootElement.GetProperty("errors").EnumerateArray(),
            static error => error.GetProperty("field").GetString() == "page"
                && error.GetProperty("message").GetString() == "must be between 1 and 50");

        Assert.Equal(400, mustInvalid.Status);
        using var mustInvalidBody = JsonDocument.Parse(mustInvalid.Body.ToArray());
        Assert.Contains(
            mustInvalidBody.RootElement.GetProperty("errors").EnumerateArray(),
            static error => error.GetProperty("field").GetString() == "page"
                && error.GetProperty("message").GetString() == "is not an allowed page");
    }

    [Fact]
    public async Task Concrete_schema_field_overrides_the_part_binding_and_rules()
    {
        var app = new App();
        var schema = Schemas.For<OverridePagingInput>()
            .Query(input => input.Page, rules => rules.Default(7).Range(5, 10))
            .Use(SharedSchemaParts.Paging);
        app.Get("/override", schema, static (context, input) => context.Json(input));

        var defaulted = await app.Request("GET", "/override");
        var rejected = await app.Request("GET", "/override?Page=4&PageSize=30");

        Assert.Equal(200, defaulted.Status);
        using (var body = JsonDocument.Parse(defaulted.Body.ToArray()))
        {
            Assert.Equal(7, body.RootElement.GetProperty("page").GetInt32());
            Assert.Equal(20, body.RootElement.GetProperty("pageSize").GetInt32());
        }

        Assert.Equal(400, rejected.Status);
        using var rejectedBody = JsonDocument.Parse(rejected.Body.ToArray());
        var error = Assert.Single(rejectedBody.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Equal("page", error.GetProperty("field").GetString());
        Assert.Equal("must be between 5 and 10", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Part_can_bind_a_request_header()
    {
        var app = new App();
        var schema = Schemas.For<HeaderPartInput>()
            .Query(input => input.Value)
            .Use(SharedSchemaParts.RequestMetadata);
        app.Get("/header", schema, static (context, input) => context.Json(input));

        var response = await app.Request(
            "GET",
            "/header?Value=3",
            new TestRequestOptions
            {
                Headers = [new KeyValuePair<string, string>("X-Request-Id", "request-42")],
            });

        Assert.Equal(200, response.Status);
        using var body = JsonDocument.Parse(response.Body.ToArray());
        Assert.Equal("request-42", body.RootElement.GetProperty("requestId").GetString());
        Assert.Equal(3, body.RootElement.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task Part_can_bind_form_fields()
    {
        var app = new App();
        var schema = Schemas.For<FormPartInput>().Use(SharedSchemaParts.FormPerson);
        app.Post("/form", schema, static (context, input) => context.Json(input));

        var response = await app.Request(
            "POST",
            "/form",
            new TestRequestOptions
            {
                TextBody = "Name=Ada&Age=37",
                Headers =
                [
                    new KeyValuePair<string, string>(
                        "Content-Type",
                        "application/x-www-form-urlencoded"),
                ],
            });

        Assert.Equal(200, response.Status);
        using var body = JsonDocument.Parse(response.Body.ToArray());
        Assert.Equal("Ada", body.RootElement.GetProperty("name").GetString());
        Assert.Equal(37, body.RootElement.GetProperty("age").GetInt32());
    }

    [Fact]
    public async Task Part_route_field_matches_the_endpoint_parameter()
    {
        var app = new App();
        var schema = Schemas.For<RoutePartInput>().Use(SharedSchemaParts.RouteIdentity);
        app.Get("/items/:Id", schema, static (context, input) => context.Text(input.Id.ToString()));

        var valid = await app.Request("GET", "/items/42");
        var invalid = await app.Request("GET", "/items/0");

        Assert.Equal(200, valid.Status);
        Assert.Equal("42", valid.Text());
        Assert.Equal(400, invalid.Status);
    }

    internal sealed record FirstPagingInput(int Page, string Search, int PageSize) : IPaging;

    internal sealed record SecondPagingInput(int PageSize, string Category, int Page) : IPaging;

    internal sealed record OverridePagingInput(int Page, int PageSize) : IPaging;

    internal sealed record HeaderPartInput(int Value, string RequestId) : IRequestMetadata;

    internal sealed record FormPartInput(string Name, int Age) : IFormPerson;

    internal sealed record RoutePartInput(int Id) : IRouteIdentity;
}
