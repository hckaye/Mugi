using System.Text.Json;
using Miya.Generators.Core;

namespace Miya.Generators.Tests;

public sealed class OpenApiTests
{
    [Fact]
    public void Form_fields_generate_urlencoded_request_body_with_constraints()
    {
        const string source = """
            using Miya;
            using Miya.Schema;

            internal sealed record FormInput(string Name, int Age, string? Note);
            internal static class Routes
            {
                internal static void Map()
                {
                    var app = new App();
                    var schema = Schemas.For<FormInput>()
                        .Form(input => input.Name, rules => rules.NotEmpty().MaxLength(40))
                        .Form(input => input.Age, rules => rules.Range(0, 120))
                        .Form(input => input.Note, rules => rules.Optional());
                    app.Post("/form", schema, static (context, input) => context.Json(input));
                }
            }
            """;

        var json = OpenApiDocumentBuilder.Build(
            GeneratorTestHelper.CreateCompilation(source),
            new OpenApiSettings("Forms", "1.0.0"));

        using var document = JsonDocument.Parse(json);
        var schema = document.RootElement.GetProperty("paths").GetProperty("/form")
            .GetProperty("post").GetProperty("requestBody").GetProperty("content")
            .GetProperty("application/x-www-form-urlencoded").GetProperty("schema");
        var properties = schema.GetProperty("properties");
        Assert.Equal(1, properties.GetProperty("Name").GetProperty("minLength").GetInt32());
        Assert.Equal(40, properties.GetProperty("Name").GetProperty("maxLength").GetInt32());
        Assert.Equal(0, properties.GetProperty("Age").GetProperty("minimum").GetInt32());
        Assert.Equal(120, properties.GetProperty("Age").GetProperty("maximum").GetInt32());
        var required = schema.GetProperty("required").EnumerateArray()
            .Select(static item => item.GetString())
            .ToArray();
        Assert.Contains("Name", required);
        Assert.Contains("Age", required);
        Assert.DoesNotContain("Note", required);
    }

    [Fact]
    public void Component_required_members_match_json_codec_presence_rules()
    {
        const string source = """
            using Miya;

            internal sealed record PresenceShape(string? Value, int Count = 42);
            internal static class Routes
            {
                internal static void Map()
                {
                    var app = new App();
                    app.Get("/presence", static context => context.Json(new PresenceShape(null)));
                }
            }
            """;

        var json = OpenApiDocumentBuilder.Build(
            GeneratorTestHelper.CreateCompilation(source),
            new OpenApiSettings("Presence", "1.0.0"));

        using var document = JsonDocument.Parse(json);
        var required = document.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty("PresenceShape").GetProperty("required").EnumerateArray()
            .Select(static item => item.GetString())
            .ToArray();
        Assert.Contains("value", required);
        Assert.DoesNotContain("count", required);
    }

    [Fact]
    public void Representative_routes_generate_openapi_31_document()
    {
        const string source = """
            using Miya;
            using Miya.Schema;

            internal sealed record RequestInput(
                int Id,
                int Limit,
                string RequestId,
                string Name,
                int Age,
                string? Note);
            internal sealed record Person(int Id, string Name, string? Note);

            internal static class Routes
            {
                internal static void Map()
                {
                    var app = new App();
                    app.Get("/health", static context => context.Text("ok"));
                    app.Get("/users/:id", static context =>
                        context.Json(new Person(int.Parse(context.Param("id")), "Ada", null)));
                    app.Get("/files/*path", static context => context.Text(context.Param("path")));
                    app.Get("/html", static context => context.Html("<p>ok</p>"));

                    var schema = Schemas.For<RequestInput>()
                        .Route(input => input.Id, rules => rules.Range(1, 999))
                        .Query(input => input.Limit, rules => rules.Default(20).Range(1, 100))
                        .Header(input => input.RequestId, "X-Request-Id", rules =>
                            rules.MinLength(8).MaxLength(40).Pattern("^[a-z]+$"))
                        .Body(input => input.Name, rules => rules.NotEmpty().Length(1, 80))
                        .Body(input => input.Age, rules => rules.Min(0).Max(120))
                        .Body(input => input.Note, rules => rules.Optional());
                    app.Post("/people/:Id", schema, static (context, input) =>
                        context.Json(new Person(input.Id, input.Name, input.Note)));
                }
            }
            """;

        var compilation = GeneratorTestHelper.CreateCompilation(source);
        var json = OpenApiDocumentBuilder.Build(
            compilation,
            new OpenApiSettings("ExampleApp", "2.3.4"));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("3.1.0", root.GetProperty("openapi").GetString());
        Assert.Equal("ExampleApp", root.GetProperty("info").GetProperty("title").GetString());
        Assert.Equal("2.3.4", root.GetProperty("info").GetProperty("version").GetString());

        var paths = root.GetProperty("paths");
        var health = paths.GetProperty("/health").GetProperty("get");
        Assert.Equal(
            "string",
            health.GetProperty("responses").GetProperty("200").GetProperty("content")
                .GetProperty("text/plain").GetProperty("schema").GetProperty("type").GetString());

        var untypedParameter = FindParameter(
            paths.GetProperty("/users/{id}").GetProperty("get"),
            "path",
            "id");
        Assert.True(untypedParameter.GetProperty("required").GetBoolean());
        Assert.Equal("string", untypedParameter.GetProperty("schema").GetProperty("type").GetString());
        Assert.Equal(
            "#/components/schemas/Person",
            paths.GetProperty("/users/{id}").GetProperty("get")
                .GetProperty("responses").GetProperty("200").GetProperty("content")
                .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());

        var wildcardParameter = FindParameter(
            paths.GetProperty("/files/{path}").GetProperty("get"),
            "path",
            "path");
        Assert.True(wildcardParameter.GetProperty("required").GetBoolean());
        Assert.False(paths.GetProperty("/html").GetProperty("get").GetProperty("responses")
            .GetProperty("200").TryGetProperty("content", out _));

        var create = paths.GetProperty("/people/{Id}").GetProperty("post");
        var id = FindParameter(create, "path", "Id");
        Assert.True(id.GetProperty("required").GetBoolean());
        Assert.Equal("integer", id.GetProperty("schema").GetProperty("type").GetString());
        Assert.Equal(1, id.GetProperty("schema").GetProperty("minimum").GetInt32());
        Assert.Equal(999, id.GetProperty("schema").GetProperty("maximum").GetInt32());

        var limit = FindParameter(create, "query", "Limit");
        Assert.False(limit.GetProperty("required").GetBoolean());
        Assert.Equal(20, limit.GetProperty("schema").GetProperty("default").GetInt32());
        Assert.Equal(1, limit.GetProperty("schema").GetProperty("minimum").GetInt32());
        Assert.Equal(100, limit.GetProperty("schema").GetProperty("maximum").GetInt32());

        var requestId = FindParameter(create, "header", "X-Request-Id");
        Assert.Equal(8, requestId.GetProperty("schema").GetProperty("minLength").GetInt32());
        Assert.Equal(40, requestId.GetProperty("schema").GetProperty("maxLength").GetInt32());
        Assert.Equal("^[a-z]+$", requestId.GetProperty("schema").GetProperty("pattern").GetString());

        var requestBody = create.GetProperty("requestBody");
        Assert.True(requestBody.GetProperty("required").GetBoolean());
        var bodySchema = requestBody.GetProperty("content").GetProperty("application/json").GetProperty("schema");
        var bodyProperties = bodySchema.GetProperty("properties");
        Assert.Equal(1, bodyProperties.GetProperty("name").GetProperty("minLength").GetInt32());
        Assert.Equal(80, bodyProperties.GetProperty("name").GetProperty("maxLength").GetInt32());
        Assert.Equal(0, bodyProperties.GetProperty("age").GetProperty("minimum").GetInt32());
        Assert.Equal(120, bodyProperties.GetProperty("age").GetProperty("maximum").GetInt32());
        Assert.Contains("name", bodySchema.GetProperty("required").EnumerateArray().Select(static item => item.GetString()));
        Assert.DoesNotContain("note", bodySchema.GetProperty("required").EnumerateArray().Select(static item => item.GetString()));

        Assert.Equal(
            "#/components/schemas/ValidationErrorResponse",
            create.GetProperty("responses").GetProperty("400").GetProperty("content")
                .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());

        var schemas = root.GetProperty("components").GetProperty("schemas");
        Assert.Equal("object", schemas.GetProperty("Person").GetProperty("type").GetString());
        Assert.Equal("array", schemas.GetProperty("ValidationErrorResponse").GetProperty("properties")
            .GetProperty("errors").GetProperty("type").GetString());
    }

    private static JsonElement FindParameter(JsonElement operation, string location, string name) =>
        operation.GetProperty("parameters").EnumerateArray().Single(parameter =>
            parameter.GetProperty("in").GetString() == location
            && parameter.GetProperty("name").GetString() == name);
}
