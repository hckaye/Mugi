using System.Text.Json;
using Mugi.Generators.Core;

namespace Mugi.Generators.Tests;

public sealed class SchemaPartOpenApiTests
{
    [Fact]
    public void Part_query_field_is_exported_as_an_openapi_parameter()
    {
        const string source = """
            using Mugi;
            using Mugi.Schema;

            internal interface IPaging { int Page { get; } }
            internal sealed record Input(string Search, int Page) : IPaging;

            internal static class Routes
            {
                internal static void Map()
                {
                    var app = new App();
                    var paging = Schemas.Part<IPaging>()
                        .Query(input => input.Page, rules => rules.Default(1).Range(1, 50));
                    var schema = Schemas.For<Input>()
                        .Query(input => input.Search)
                        .Use(paging);
                    app.Get("/search", schema, static (context, input) => context.Json(input));
                }
            }
            """;
        var compilation = GeneratorTestHelper.CreateCompilation(source);

        var json = OpenApiDocumentBuilder.Build(compilation);

        using var document = JsonDocument.Parse(json);
        var operation = document.RootElement.GetProperty("paths").GetProperty("/search").GetProperty("get");
        var page = operation.GetProperty("parameters").EnumerateArray().Single(parameter =>
            parameter.GetProperty("in").GetString() == "query"
            && parameter.GetProperty("name").GetString() == "Page");
        Assert.False(page.GetProperty("required").GetBoolean());
        Assert.Equal(1, page.GetProperty("schema").GetProperty("default").GetInt32());
        Assert.Equal(1, page.GetProperty("schema").GetProperty("minimum").GetInt32());
        Assert.Equal(50, page.GetProperty("schema").GetProperty("maximum").GetInt32());
    }
}
