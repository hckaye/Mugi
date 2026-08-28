using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Miya.Generators.Core;

namespace Miya.Generators.Tests;

public sealed class OpenApiClientTests
{
    [Fact]
    public void Representative_document_generates_client_operations_and_codecs()
    {
        var result = Generate(RepresentativeDocument(), "Demo.Api", "PetStoreClient");

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var source = result.Sources.Single().Source;
        Assert.Contains("public sealed class PetStoreClient", source, StringComparison.Ordinal);
        Assert.Contains(
            "public async global::System.Threading.Tasks.Task<Item> GetItem(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "int id, string q, string XTraceId, string? note = null",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "global::System.Uri.EscapeDataString(id.ToString(global::System.Globalization.CultureInfo.InvariantCulture))",
            source,
            StringComparison.Ordinal);
        Assert.Contains("query.Append(\"q\")", source, StringComparison.Ordinal);
        Assert.Contains("request.Headers.TryAddWithoutValidation(\"X-Trace-Id\"", source, StringComparison.Ordinal);
        Assert.Contains("global::Miya.Json.Json.Serialize(bodyBuffer, body)", source, StringComparison.Ordinal);
        Assert.Contains("Json.Register<global::Demo.Api.Item>", source, StringComparison.Ordinal);
        Assert.Contains("Json.ResolveCodec<global::Demo.Api.Item>", source, StringComparison.Ordinal);
        Assert.Contains("EnsureRegistered();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ModuleInitializer", source, StringComparison.Ordinal);
        Assert.Contains("public sealed class ApiException", source, StringComparison.Ordinal);
        Assert.Contains("public string? Body", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_client_compiles_with_only_miya_json_reference()
    {
        var result = Generate(RepresentativeDocument(), "Demo.Api", "PetStoreClient");
        var source = result.Sources.Single().Source;
        var compilation = CSharpCompilation.Create(
            "OpenApiClientCompile",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), "client.g.cs")],
            JsonOnlyReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable,
                optimizationLevel: OptimizationLevel.Release,
                warningLevel: 9999));

        var diagnostics = compilation.GetDiagnostics()
            .Where(static diagnostic =>
                diagnostic.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error)
            .Where(static diagnostic => diagnostic.Id != "CS1701")
            .ToArray();
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Client_metadata_can_be_enabled_without_server_import_metadata()
    {
        const string path = "api/client.json";
        var run = GeneratorTestHelper.Run(
            GeneratorTestHelper.CreateCompilation("internal static class Application { }"),
            additionalTexts: [GeneratorTestHelper.AdditionalText(path, RepresentativeDocument())],
            additionalFileMetadata: new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                [path] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["MiyaOpenApiClient"] = "true",
                    ["MiyaOpenApiNamespace"] = "Demo.Client",
                    ["MiyaOpenApiClientName"] = "CatalogClient",
                },
            });

        Assert.DoesNotContain(
            run.DriverDiagnostics.Concat(run.Result.Diagnostics).Concat(run.Compilation.GetDiagnostics()),
            static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("public sealed class CatalogClient", run.SourcesWithPrefix("Miya.OpenApiClient."), StringComparison.Ordinal);
        Assert.Contains("public sealed class ApiException", run.SourcesWithPrefix("Miya.OpenApiClient.ApiException."), StringComparison.Ordinal);
        Assert.Empty(run.SourcesWithPrefix("Miya.OpenApi."));
    }

    [Fact]
    public void Client_and_server_metadata_can_share_one_document()
    {
        const string path = "api/shared.json";
        var run = GeneratorTestHelper.Run(
            GeneratorTestHelper.CreateCompilation("internal static class Application { }"),
            additionalTexts: [GeneratorTestHelper.AdditionalText(path, RepresentativeDocument())],
            additionalFileMetadata: new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                [path] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["MiyaOpenApi"] = "true",
                    ["MiyaOpenApiClient"] = "true",
                    ["MiyaOpenApiNamespace"] = "Demo.Shared",
                    ["MiyaOpenApiClientName"] = "SharedClient",
                },
            });

        Assert.DoesNotContain(
            run.DriverDiagnostics.Concat(run.Result.Diagnostics).Concat(run.Compilation.GetDiagnostics()),
            static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("public static partial class Paths", run.SourcesWithPrefix("Miya.OpenApi."), StringComparison.Ordinal);
        Assert.Contains("public sealed class SharedClient", run.SourcesWithPrefix("Miya.OpenApiClient."), StringComparison.Ordinal);
        Assert.DoesNotContain(
            "public sealed record Item(",
            run.SourcesWithPrefix("Miya.OpenApiClient."),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Client_metadata_uses_root_namespace_when_no_namespace_is_configured()
    {
        const string path = "api/default.json";
        var run = GeneratorTestHelper.Run(
            GeneratorTestHelper.CreateCompilation("internal static class Application { }"),
            additionalTexts: [GeneratorTestHelper.AdditionalText(path, RepresentativeDocument())],
            additionalFileMetadata: new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                [path] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["MiyaOpenApiClient"] = "true",
                },
            },
            rootNamespace: "Demo.Default");

        Assert.DoesNotContain(
            run.DriverDiagnostics.Concat(run.Result.Diagnostics).Concat(run.Compilation.GetDiagnostics()),
            static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("namespace Demo.Default", run.SourcesWithPrefix("Miya.OpenApiClient."), StringComparison.Ordinal);
        Assert.Contains("public sealed class PetStoreClient", run.SourcesWithPrefix("Miya.OpenApiClient."), StringComparison.Ordinal);
        Assert.Contains("public sealed class ApiException", run.SourcesWithPrefix("Miya.OpenApiClient.ApiException."), StringComparison.Ordinal);
    }

    [Fact]
    public void Cookie_and_non_json_operations_are_skipped_with_existing_diagnostics()
    {
        const string document = """
            {
              "openapi": "3.0.3",
              "info": { "title": "Unsafe API" },
              "paths": {
                "/cookies": {
                  "get": {
                    "operationId": "cookies",
                    "parameters": [
                      { "name": "session", "in": "cookie", "schema": { "type": "string" } }
                    ],
                    "responses": { "200": { "description": "ok" } }
                  }
                },
                "/text": {
                  "get": {
                    "operationId": "text",
                    "responses": {
                      "200": {
                        "description": "ok",
                        "content": { "text/plain": { "schema": { "type": "string" } } }
                      }
                    }
                  }
                }
              }
            }
            """;

        var result = Generate(document, "Demo.Api", "UnsafeClient");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "MIYA022");
        var source = result.Sources.Single().Source;
        Assert.DoesNotContain(" Cookies(", source, StringComparison.Ordinal);
        Assert.DoesNotContain(" Text(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Arrays_enums_and_optional_numeric_parameters_generate_wire_safe_code()
    {
        var result = Generate(
            """
            {
              "openapi": "3.0.3",
              "info": { "title": "Search" },
              "paths": {
                "/search": {
                  "get": {
                    "operationId": "search",
                    "parameters": [
                      { "name": "limit", "in": "query", "schema": { "type": "integer", "format": "int32" } },
                      { "name": "enabled", "in": "query", "schema": { "type": "boolean" } },
                      { "name": "status", "in": "query", "schema": { "$ref": "#/components/schemas/Status" } }
                    ],
                    "responses": {
                      "200": {
                        "description": "ok",
                        "content": {
                          "application/json": {
                            "schema": { "type": "array", "items": { "$ref": "#/components/schemas/Item" } }
                          }
                        }
                      }
                    }
                  }
                }
              },
              "components": {
                "schemas": {
                  "Status": { "type": "string", "enum": ["in-progress", "done"] },
                  "Item": {
                    "type": "object",
                    "properties": {
                      "id": { "type": "string" },
                      "status": { "$ref": "#/components/schemas/Status" }
                    },
                    "required": ["id"]
                  }
                }
              }
            }
            """,
            "Demo.Api",
            "SearchClient");

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var source = result.Sources.Single().Source;
        Assert.Contains("Task<Item[]> Search(", source, StringComparison.Ordinal);
        Assert.Contains("int? limit = null, bool? enabled = null, Status? status = null", source, StringComparison.Ordinal);
        Assert.Contains("Status.InProgress => \"in-progress\"", source, StringComparison.Ordinal);
        Assert.Contains("global::System.Uri.EscapeDataString(limit.Value.ToString", source, StringComparison.Ordinal);
        Assert.Contains("global::System.Uri.EscapeDataString(enabled.Value ? \"true\" : \"false\")", source, StringComparison.Ordinal);

        var compilation = CSharpCompilation.Create(
            "OpenApiClientEnumCompile",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), "client.g.cs")],
            JsonOnlyReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable,
                optimizationLevel: OptimizationLevel.Release,
                warningLevel: 9999));
        var diagnostics = compilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error)
            .Where(static diagnostic => diagnostic.Id != "CS1701")
            .ToArray();
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Client_codecs_register_on_first_client_use_and_round_trip_afterward()
    {
        var result = Generate(
            SingleItemDocument("200"),
            "Demo.LazyRegistration",
            "LazyClient");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var assembly = CompileAndLoad(result.Sources.Single().Source);
        var itemType = assembly.GetType("Demo.LazyRegistration.Item")!;
        var clientType = assembly.GetType("Demo.LazyRegistration.LazyClient")!;

        Assert.Null(TryGetCodec(itemType));
        using var http = Client(new StubHandler(HttpStatusCode.OK, "{\"value\":\"ready\"}"));
        var client = Activator.CreateInstance(clientType, http)!;
        Assert.NotNull(TryGetCodec(itemType));

        var task = Assert.IsAssignableFrom<Task>(
            clientType.GetMethod("GetItem")!.Invoke(client, [CancellationToken.None]));
        await task;
        var item = task.GetType().GetProperty("Result")!.GetValue(task)!;
        Assert.Equal("ready", itemType.GetProperty("Value")!.GetValue(item));
    }

    [Fact]
    public async Task Json_200_and_no_body_204_are_handled_per_status()
    {
        var result = Generate(
            """
            {
              "openapi": "3.1.0",
              "info": { "title": "Status API" },
              "paths": {
                "/item": {
                  "get": {
                    "operationId": "get-item",
                    "responses": {
                      "200": {
                        "description": "ok",
                        "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Item" } } }
                      },
                      "204": { "description": "no content" }
                    }
                  }
                }
              },
              "components": {
                "schemas": {
                  "Item": {
                    "type": "object",
                    "properties": { "value": { "type": "string" } },
                    "required": ["value"]
                  }
                }
              }
            }
            """,
            "Demo.StatusResponses",
            "StatusClient");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var source = result.Sources.Single().Source;
        Assert.Contains("if ((int)response.StatusCode == 200)", source, StringComparison.Ordinal);
        var assembly = CompileAndLoad(source);
        var clientType = assembly.GetType("Demo.StatusResponses.StatusClient")!;
        var method = clientType.GetMethod("GetItem")!;

        using var okHttp = Client(new StubHandler(HttpStatusCode.OK, "{\"value\":\"ok\"}"));
        var okClient = Activator.CreateInstance(clientType, okHttp)!;
        var okTask = Assert.IsAssignableFrom<Task>(method.Invoke(okClient, [CancellationToken.None]));
        await okTask;
        Assert.NotNull(okTask.GetType().GetProperty("Result")!.GetValue(okTask));

        using var noContentHttp = Client(new StubHandler(HttpStatusCode.NoContent));
        var noContentClient = Activator.CreateInstance(clientType, noContentHttp)!;
        var noContentTask = Assert.IsAssignableFrom<Task>(
            method.Invoke(noContentClient, [CancellationToken.None]));
        await noContentTask;
        Assert.Null(noContentTask.GetType().GetProperty("Result")!.GetValue(noContentTask));
    }

    [Fact]
    public void Incompatible_success_response_schemas_report_diagnostic_and_skip_operation()
    {
        var result = Generate(
            """
            {
              "openapi": "3.1.0",
              "info": { "title": "Incompatible API" },
              "paths": {
                "/item": {
                  "get": {
                    "operationId": "get-item",
                    "responses": {
                      "200": {
                        "description": "ok",
                        "content": { "application/json": { "schema": { "$ref": "#/components/schemas/First" } } }
                      },
                      "201": {
                        "description": "created",
                        "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Second" } } }
                      }
                    }
                  }
                }
              },
              "components": {
                "schemas": {
                  "First": {
                    "type": "object",
                    "properties": { "first": { "type": "string" } },
                    "required": ["first"]
                  },
                  "Second": {
                    "type": "object",
                    "properties": { "second": { "type": "integer" } },
                    "required": ["second"]
                  }
                }
              }
            }
            """,
            "Demo.IncompatibleResponses",
            "IncompatibleClient");

        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Id == "MIYA022");
        Assert.Contains("incompatible JSON schemas", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.DoesNotContain(" GetItem(", result.Sources.Single().Source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dot_segments_in_string_path_parameters_are_rejected_before_sending()
    {
        var result = Generate(
            """
            {
              "openapi": "3.1.0",
              "info": { "title": "Path API" },
              "paths": {
                "/items/{id}": {
                  "get": {
                    "operationId": "get-item",
                    "parameters": [
                      { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } }
                    ],
                    "responses": { "204": { "description": "no content" } }
                  }
                }
              }
            }
            """,
            "Demo.SafePaths",
            "SafePathClient");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var assembly = CompileAndLoad(result.Sources.Single().Source);
        var clientType = assembly.GetType("Demo.SafePaths.SafePathClient")!;
        var handler = new StubHandler(HttpStatusCode.NoContent);
        using var http = new HttpClient(handler);
        var client = Activator.CreateInstance(clientType, http)!;

        var task = Assert.IsAssignableFrom<Task>(
            clientType.GetMethod("GetItem")!.Invoke(client, ["..", CancellationToken.None]));
        await Assert.ThrowsAsync<ArgumentException>(async () => await task);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public void Required_nullable_scalar_parameter_reports_diagnostic_and_skips_operation()
    {
        var result = Generate(
            """
            {
              "openapi": "3.1.0",
              "info": { "title": "Nullable Parameter API" },
              "paths": {
                "/items": {
                  "get": {
                    "operationId": "get-item",
                    "parameters": [
                      {
                        "name": "count",
                        "in": "query",
                        "required": true,
                        "schema": { "type": ["integer", "null"], "format": "int32" }
                      }
                    ],
                    "responses": { "204": { "description": "no content" } }
                  }
                }
              }
            }
            """,
            "Demo.NullableParameter",
            "NullableParameterClient");

        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Id == "MIYA022");
        Assert.Contains("required scalar parameters cannot use a nullable schema", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.DoesNotContain(" GetItem(", result.Sources.Single().Source, StringComparison.Ordinal);
    }

    private static GenerationResult Generate(string document, string targetNamespace, string clientName) =>
        OpenApiClientGenerator.Generate(
            new OpenApiImportInput(
                "api/client.json",
                document,
                targetNamespace,
                JsonNaming.CamelCase,
                clientName),
            CancellationToken.None);

    private static string SingleItemDocument(string status) => """
        {
          "openapi": "3.1.0",
          "info": { "title": "Lazy API" },
          "paths": {
            "/item": {
              "get": {
                "operationId": "get-item",
                "responses": {
                  "STATUS": {
                    "description": "ok",
                    "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Item" } } }
                  }
                }
              }
            }
          },
          "components": {
            "schemas": {
              "Item": {
                "type": "object",
                "properties": { "value": { "type": "string" } },
                "required": ["value"]
              }
            }
          }
        }
        """.Replace("STATUS", status, StringComparison.Ordinal);

    private static Assembly CompileAndLoad(string source)
    {
        var compilation = CSharpCompilation.Create(
            "OpenApiClientExecution_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), "client.g.cs")],
            JsonOnlyReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable,
                optimizationLevel: OptimizationLevel.Release,
                warningLevel: 9999));
        return GeneratorTestHelper.EmitAndLoad(compilation);
    }

    private static object? TryGetCodec(Type type) => typeof(Miya.Json.Json)
        .GetMethod(nameof(Miya.Json.Json.TryGetCodec), BindingFlags.Public | BindingFlags.Static)!
        .MakeGenericMethod(type)
        .Invoke(null, null);

    private static HttpClient Client(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("https://example.test"),
    };

    private static string RepresentativeDocument() => """
        {
          "openapi": "3.1.0",
          "info": { "title": "Pet Store" },
          "paths": {
            "/items/{id}": {
              "get": {
                "operationId": "get-item",
                "parameters": [
                  { "name": "id", "in": "path", "required": true, "schema": { "type": "integer", "format": "int32" } },
                  { "name": "q", "in": "query", "required": true, "schema": { "type": "string" } },
                  { "name": "note", "in": "query", "required": false, "schema": { "type": "string" } },
                  { "name": "X-Trace-Id", "in": "header", "required": true, "schema": { "type": "string" } }
                ],
                "responses": {
                  "404": { "description": "missing" },
                  "200": {
                    "description": "ok",
                    "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Item" } } }
                  }
                }
              }
            },
            "/items": {
              "post": {
                "operationId": "create-item",
                "requestBody": {
                  "required": true,
                  "content": {
                    "application/json": {
                      "schema": {
                        "type": "object",
                        "properties": {
                          "name": { "type": "string" },
                          "label": { "type": "string" }
                        },
                        "required": ["name"]
                      }
                    }
                  }
                },
                "responses": {
                  "201": {
                    "description": "created",
                    "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Item" } } }
                  }
                }
              }
            }
          },
          "components": {
            "schemas": {
              "Item": {
                "type": "object",
                "properties": {
                  "id": { "type": "integer", "format": "int32" },
                  "name": { "type": "string" },
                  "unicode": { "type": "string" }
                },
                "required": ["id", "name", "unicode"]
              }
            }
          }
        }
        """;

    private static ImmutableArray<MetadataReference> JsonOnlyReferences()
    {
        var runtime = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return runtime
            .Append(typeof(Miya.Json.Json).Assembly.Location)
            .Append(typeof(HttpClient).Assembly.Location)
            .Distinct(StringComparer.Ordinal)
            .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToImmutableArray();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string? _body;

        internal StubHandler(HttpStatusCode status, string? body = null)
        {
            _status = status;
            _body = body;
        }

        internal int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            var response = new HttpResponseMessage(_status);
            if (_body is not null)
            {
                response.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(_body));
            }

            return Task.FromResult(response);
        }
    }
}
