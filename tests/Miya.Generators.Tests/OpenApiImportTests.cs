using Microsoft.CodeAnalysis;

namespace Miya.Generators.Tests;

public sealed class OpenApiImportTests
{
    [Fact]
    public void Representative_additional_file_generates_dtos_paths_and_schemas()
    {
        var path = "api/openapi.json";
        var openApi = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "OpenApiImport",
            "openapi.json"));
        var additionalText = GeneratorTestHelper.AdditionalText(path, openApi);
        var run = GeneratorTestHelper.Run(
            GeneratorTestHelper.CreateCompilation("internal static class Application { }"),
            additionalTexts: [additionalText],
            additionalFileMetadata: Metadata(path, "MyApp.Api"));

        var generated = run.SourcesWithPrefix("Miya.OpenApi.");

        Assert.DoesNotContain(AllDiagnostics(run), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("namespace MyApp.Api", generated, StringComparison.Ordinal);
        Assert.Contains("public enum Status", generated, StringComparison.Ordinal);
        Assert.Contains("Active", generated, StringComparison.Ordinal);
        Assert.Contains("public sealed record User(", generated, StringComparison.Ordinal);
        Assert.Contains("long Id,", generated, StringComparison.Ordinal);
        Assert.Contains("Status Status,", generated, StringComparison.Ordinal);
        Assert.Contains("string[]? Tags,", generated, StringComparison.Ordinal);
        Assert.Contains("decimal? Balance,", generated, StringComparison.Ordinal);
        Assert.Contains("UserProfile Profile);", generated, StringComparison.Ordinal);
        Assert.Contains("public sealed record UserProfile(", generated, StringComparison.Ordinal);
        Assert.Contains("string Nickname);", generated, StringComparison.Ordinal);

        Assert.Contains("public static partial class Paths", generated, StringComparison.Ordinal);
        Assert.Contains(
            "public const string GetUserById = \"/users/:id\";",
            generated,
            StringComparison.Ordinal);
        Assert.Contains(
            "public const string GetHealth = \"/health\";",
            generated,
            StringComparison.Ordinal);

        Assert.Contains("public sealed record GetUserByIdInput(", generated, StringComparison.Ordinal);
        Assert.Contains("long id,", generated, StringComparison.Ordinal);
        Assert.Contains("int? limit,", generated, StringComparison.Ordinal);
        Assert.Contains("string? XTraceId);", generated, StringComparison.Ordinal);
        Assert.Contains("public sealed record CreateUserInput(", generated, StringComparison.Ordinal);
        Assert.Contains("string Name,", generated, StringComparison.Ordinal);
        Assert.Contains("int Age,", generated, StringComparison.Ordinal);
        Assert.Contains("Status Status,", generated, StringComparison.Ordinal);
        Assert.Contains("string[]? Tags);", generated, StringComparison.Ordinal);

        Assert.Contains("public static partial class ApiSchemas", generated, StringComparison.Ordinal);
        Assert.Contains(
            "Schema<GetUserByIdInput> GetUserById",
            generated,
            StringComparison.Ordinal);
        Assert.Contains(
            ".Route(input => input.id, rules => rules.Range(1L, 99L))",
            generated,
            StringComparison.Ordinal);
        Assert.Contains(
            ".Query(input => input.limit, rules => rules.Default(20).Range(1, 100).Optional())",
            generated,
            StringComparison.Ordinal);
        Assert.Contains(
            ".Header(input => input.XTraceId, \"X-Trace-Id\", rules => rules.Length(8, 40).Pattern(\"^[a-z0-9]+$\").Optional())",
            generated,
            StringComparison.Ordinal);
        Assert.Contains(
            ".Body(input => input.Name, rules => rules.Length(1, 80))",
            generated,
            StringComparison.Ordinal);
        Assert.Contains(
            ".Body(input => input.Tags, rules => rules.Optional())",
            generated,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Unsupported_schema_shapes_and_name_collisions_report_import_diagnostics()
    {
        const string path = "api/unsupported.json";
        const string openApi = """
            {
              "openapi": "3.0.3",
              "paths": {
                "/search": {
                  "get": {
                    "operationId": "search",
                    "parameters": [
                      {
                        "name": "tags",
                        "in": "query",
                        "schema": {
                          "type": "array",
                          "items": { "type": "string" }
                        }
                      }
                    ]
                  }
                }
              },
              "components": {
                "schemas": {
                  "Choice": {
                    "oneOf": [
                      { "type": "string" },
                      { "type": "integer" }
                    ]
                  },
                  "Map": {
                    "type": "object",
                    "additionalProperties": { "type": "string" }
                  },
                  "foo-bar": { "type": "object" },
                  "foo bar": { "type": "object" }
                }
              }
            }
            """;

        var run = GeneratorTestHelper.Run(
            GeneratorTestHelper.CreateCompilation("internal static class Application { }"),
            additionalTexts: [GeneratorTestHelper.AdditionalText(path, openApi)],
            additionalFileMetadata: Metadata(path, "MyApp.Api"));
        var diagnostics = AllDiagnostics(run).ToArray();

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "MIYA021");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "MIYA022");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "MIYA023");
    }

    [Fact]
    public void Invalid_json_reports_miya020_without_generating_import_source()
    {
        const string path = "api/invalid.json";
        var run = GeneratorTestHelper.Run(
            GeneratorTestHelper.CreateCompilation("internal static class Application { }"),
            additionalTexts: [GeneratorTestHelper.AdditionalText(path, "{ \"openapi\": \"3.1.0\",")],
            additionalFileMetadata: Metadata(path, "MyApp.Api"));

        Assert.Contains(AllDiagnostics(run), diagnostic => diagnostic.Id == "MIYA020");
        Assert.Empty(run.SourcesWithPrefix("Miya.OpenApi."));
    }

    [Fact]
    public void Namespace_defaults_to_the_project_root_namespace()
    {
        const string path = "api/minimal.json";
        const string openApi = """{ "openapi": "3.1.0", "paths": {} }""";
        var run = GeneratorTestHelper.Run(
            GeneratorTestHelper.CreateCompilation("internal static class Application { }"),
            additionalTexts: [GeneratorTestHelper.AdditionalText(path, openApi)],
            additionalFileMetadata: Metadata(path, targetNamespace: null),
            rootNamespace: "Example.Project");

        Assert.Contains(
            "namespace Example.Project",
            run.SourcesWithPrefix("Miya.OpenApi."),
            StringComparison.Ordinal);
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Metadata(
        string path,
        string? targetNamespace)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MiyaOpenApi"] = "true",
        };
        if (targetNamespace is not null)
        {
            values["MiyaOpenApiNamespace"] = targetNamespace;
        }

        return new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            [path] = values,
        };
    }

    private static IEnumerable<Diagnostic> AllDiagnostics(GeneratorRun run) =>
        run.DriverDiagnostics.Concat(run.Result.Diagnostics).Concat(run.Compilation.GetDiagnostics());
}
