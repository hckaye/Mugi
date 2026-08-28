using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Miya.Generators.Core;

namespace Miya.Generators.Tests;

public sealed class IncrementalGeneratorTests
{
    [Fact]
    public void Every_recognized_invocation_name_keeps_relevant_generation_enabled()
    {
        const string source = """
            using System.Buffers;
            using Miya;
            using Miya.Json;
            using Miya.Schema;

            internal sealed record Payload(int Id);
            internal interface IInputPart { int Id { get; } }
            internal sealed record Input(int Id) : IInputPart;
            internal static class Routes
            {
                internal static void Register()
                {
                    var app = new App();
                    var sub = new App();
                    var part = Schemas.Part<IInputPart>().Query(input => input.Id);
                    var schema = Schemas.For<Input>().Use(part);
                    app.Get("/get/:id", c => c.Json(new Payload(int.Parse(c.Param("id")))));
                    app.Post("/post", static c => c.Text("post"));
                    app.Put("/put", static c => c.Text("put"));
                    app.Delete("/delete", static c => c.Text("delete"));
                    app.Patch("/patch", static c => c.Text("patch"));
                    app.Head("/head", static c => c.Text("head"));
                    app.Options("/options", static c => c.Text("options"));
                    app.All("/all", static c => c.Text("all"));
                    app.On("TRACE", "/on", static c => c.Text("on"));
                    app.Use(static async (c, next) => await next(c));
                    app.Route("/sub", sub);
                    app.Get("/schema", schema, static (c, input) => c.Json(input));

                    Json.Include<Payload>();
                    var buffer = new ArrayBufferWriter<byte>();
                    Json.Serialize(buffer, new Payload(1));
                    _ = Json.Deserialize<Payload>(buffer.WrittenSpan);
                }
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));
        var generated = string.Join(
            Environment.NewLine,
            run.Result.Results.SelectMany(static result => result.GeneratedSources)
                .Select(static result => result.SourceText.ToString()));

        Assert.DoesNotContain(
            run.Compilation.GetDiagnostics(),
            static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("IJsonCodec<global::Payload>", generated, StringComparison.Ordinal);
        Assert.Contains("RouteTemplate.Precompiled", generated, StringComparison.Ordinal);
        Assert.Contains("IInputBinder<global::Input>", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void Unrelated_edit_reuses_json_type_pipeline_output()
    {
        const string source = """
            using Miya.Json;
            internal sealed record Payload(int Id);
            internal static class Marker { internal static void Include() => Json.Include<Payload>(); }
            """;
        var compilation = GeneratorTestHelper.CreateCompilation(source);
        var first = GeneratorTestHelper.Run(compilation, trackSteps: true);
        var unrelated = CSharpSyntaxTree.ParseText(
            "internal static class Unrelated { internal const int Value = 2; }",
            GeneratorTestHelper.ParseOptions,
            "Unrelated.cs");
        var updated = compilation.AddSyntaxTrees(unrelated);

        var driver = first.Driver.RunGeneratorsAndUpdateCompilation(updated, out _, out _);
        var result = driver.GetRunResult().Results.Single();
        var jsonTypeSteps = result.TrackedSteps["MiyaJsonTypes"];
        var jsonSourceSteps = result.TrackedSteps["MiyaJsonSources"];

        Assert.NotEmpty(jsonTypeSteps);
        Assert.All(
            jsonTypeSteps.SelectMany(static step => step.Outputs),
            static output => Assert.True(
                output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged));
        Assert.All(
            jsonSourceSteps.SelectMany(static step => step.Outputs),
            static output => Assert.True(
                output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged));
        Assert.Equal(
            first.SourcesWithPrefix("Miya.JsonCodec."),
            string.Join(
                Environment.NewLine,
                driver.GetRunResult().Results.Single().GeneratedSources
                    .Where(static sourceResult => sourceResult.HintName.StartsWith("Miya.JsonCodec.", StringComparison.Ordinal))
                    .OrderBy(static sourceResult => sourceResult.HintName, StringComparer.Ordinal)
                    .Select(static sourceResult => sourceResult.SourceText.ToString())));
    }

    [Fact]
    public void Editing_one_type_keeps_other_type_outputs_cached_or_unchanged()
    {
        const string firstType = """
            using Miya.Json;
            internal sealed record First(int Id);
            internal static class FirstMarker { internal static void Include() => Json.Include<First>(); }
            """;
        const string changedFirstType = """
            using Miya.Json;
            internal sealed record First(int Id, string Name);
            internal static class FirstMarker { internal static void Include() => Json.Include<First>(); }
            """;
        const string secondType = """
            using Miya.Json;
            internal sealed record Second(int Id);
            internal static class SecondMarker { internal static void Include() => Json.Include<Second>(); }
            """;
        var compilation = GeneratorTestHelper.CreateCompilation(firstType, "First.cs");
        var secondTree = CSharpSyntaxTree.ParseText(
            secondType,
            GeneratorTestHelper.ParseOptions,
            "Second.cs");
        compilation = compilation.AddSyntaxTrees(secondTree);
        var first = GeneratorTestHelper.Run(compilation, trackSteps: true);
        var changedTree = CSharpSyntaxTree.ParseText(
            changedFirstType,
            GeneratorTestHelper.ParseOptions,
            "First.cs");
        var updated = compilation.ReplaceSyntaxTree(
            compilation.SyntaxTrees.Single(static tree => tree.FilePath == "First.cs"),
            changedTree);

        var driver = first.Driver.RunGeneratorsAndUpdateCompilation(updated, out _, out _);
        var result = driver.GetRunResult().Results.Single();

        AssertTrackedSourceUnchanged(result, "MiyaJsonSources", "Second");
    }

    [Fact]
    public void Editing_one_route_keeps_other_route_and_interceptor_outputs_cached_or_unchanged()
    {
        const string firstRoute = """
            using Miya;
            internal static class FirstRoutes
            {
                internal static void Register(App app) => app.Get("/a", c => c.Text("a"));
            }
            """;
        const string changedFirstRoute = """
            using Miya;
            internal static class FirstRoutes
            {
                internal static void Register(App app) => app.Get("/c", c => c.Text("a"));
            }
            """;
        const string secondRoute = """
            using Miya;
            internal static class SecondRoutes
            {
                internal static void Register(App app) => app.Get("/b", c => c.Text("b"));
            }
            """;
        var compilation = GeneratorTestHelper.CreateCompilation(firstRoute, "FirstRoutes.cs");
        compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            secondRoute,
            GeneratorTestHelper.ParseOptions,
            "SecondRoutes.cs"));
        var first = GeneratorTestHelper.Run(compilation, trackSteps: true);
        var changedTree = CSharpSyntaxTree.ParseText(
            changedFirstRoute,
            GeneratorTestHelper.ParseOptions,
            "FirstRoutes.cs");
        var updated = compilation.ReplaceSyntaxTree(
            compilation.SyntaxTrees.Single(static tree => tree.FilePath == "FirstRoutes.cs"),
            changedTree);

        var driver = first.Driver.RunGeneratorsAndUpdateCompilation(updated, out _, out _);
        var result = driver.GetRunResult().Results.Single();

        AssertTrackedSourceUnchanged(result, "MiyaRouteSources", "_002F_b");
        AssertTrackedSourceUnchanged(result, "MiyaInterceptorSources", "SecondRoutes");
    }

    [Fact]
    public void Editing_a_part_updates_consumers_and_keeps_unrelated_schemas_unchanged()
    {
        const string firstPart = """
            using Miya.Schema;
            internal interface IPaging { int Page { get; } }
            internal static class Parts
            {
                internal static readonly SchemaPart<IPaging> Paging = Schemas.Part<IPaging>()
                    .Query(input => input.Page, rules => rules.Default(1));
            }
            """;
        const string changedPart = """
            using Miya.Schema;
            internal interface IPaging { int Page { get; } }
            internal static class Parts
            {
                internal static readonly SchemaPart<IPaging> Paging = Schemas.Part<IPaging>()
                    .Query(input => input.Page, rules => rules.Default(2));
            }
            """;
        const string consumer = """
            using Miya;
            using Miya.Schema;
            internal sealed record ConsumerInput(int Page) : IPaging;
            internal static class ConsumerRoutes
            {
                internal static void Map()
                {
                    var app = new App();
                    var schema = Schemas.For<ConsumerInput>().Use(Parts.Paging);
                    app.Get("/consumer", schema, static (context, input) => context.Json(input));
                }
            }
            """;
        const string unrelated = """
            using Miya;
            using Miya.Schema;
            internal sealed record UnrelatedInput(int Value);
            internal static class UnrelatedRoutes
            {
                internal static void Map()
                {
                    var app = new App();
                    var schema = Schemas.For<UnrelatedInput>().Query(input => input.Value);
                    app.Get("/unrelated", schema, static (context, input) => context.Json(input));
                }
            }
            """;
        var compilation = GeneratorTestHelper.CreateCompilation(firstPart, "Part.cs")
            .AddSyntaxTrees(
                CSharpSyntaxTree.ParseText(
                    consumer,
                    GeneratorTestHelper.ParseOptions,
                    "Consumer.cs"),
                CSharpSyntaxTree.ParseText(
                    unrelated,
                    GeneratorTestHelper.ParseOptions,
                    "Unrelated.cs"));
        var first = GeneratorTestHelper.Run(compilation, trackSteps: true);
        var changedTree = CSharpSyntaxTree.ParseText(
            changedPart,
            GeneratorTestHelper.ParseOptions,
            "Part.cs");
        var updated = compilation.ReplaceSyntaxTree(
            compilation.SyntaxTrees.Single(static tree => tree.FilePath == "Part.cs"),
            changedTree);

        var driver = first.Driver.RunGeneratorsAndUpdateCompilation(updated, out _, out _);
        var result = driver.GetRunResult().Results.Single();
        var consumerOutputs = result.TrackedSteps["MiyaSchemaSources"]
            .SelectMany(static step => step.Outputs)
            .Where(output => output.Value is GeneratedSource source
                && source.HintName.Contains("ConsumerInput", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(consumerOutputs);
        Assert.All(
            consumerOutputs,
            static output => Assert.DoesNotContain(
                output.Reason,
                new[] { IncrementalStepRunReason.Cached, IncrementalStepRunReason.Unchanged }));
        AssertTrackedSourceUnchanged(result, "MiyaSchemaSources", "UnrelatedInput");
    }

    [Fact]
    public void Editing_a_shared_rule_method_regenerates_its_consumer()
    {
        const string firstRules = """
            using Miya.Schema;
            internal static class SharedRules
            {
                internal static void Apply(Rule<int> rule) => rule.Range(1, 10);
            }
            """;
        const string changedRules = """
            using Miya.Schema;
            internal static class SharedRules
            {
                internal static void Apply(Rule<int> rule) => rule.Range(1, 20);
            }
            """;
        const string consumer = """
            using Miya;
            using Miya.Schema;
            internal sealed record ConsumerInput(int Value);
            internal static class ConsumerRoutes
            {
                internal static void Map()
                {
                    var app = new App();
                    var schema = Schemas.For<ConsumerInput>()
                        .Query(input => input.Value, SharedRules.Apply);
                    app.Get("/consumer", schema, static (context, input) => context.Json(input));
                }
            }
            """;
        var compilation = GeneratorTestHelper.CreateCompilation(firstRules, "Rules.cs")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(
                consumer,
                GeneratorTestHelper.ParseOptions,
                "Consumer.cs"));
        var first = GeneratorTestHelper.Run(compilation, trackSteps: true);
        var changedTree = CSharpSyntaxTree.ParseText(
            changedRules,
            GeneratorTestHelper.ParseOptions,
            "Rules.cs");
        var updated = compilation.ReplaceSyntaxTree(
            compilation.SyntaxTrees.Single(static tree => tree.FilePath == "Rules.cs"),
            changedTree);

        var driver = first.Driver.RunGeneratorsAndUpdateCompilation(updated, out _, out _);
        var result = driver.GetRunResult().Results.Single();
        var consumerOutputs = result.TrackedSteps["MiyaSchemaSources"]
            .SelectMany(static step => step.Outputs)
            .Where(output => output.Value is GeneratedSource source
                && source.HintName.Contains("ConsumerInput", StringComparison.Ordinal))
            .ToArray();
        var generated = string.Join(
            Environment.NewLine,
            result.GeneratedSources
                .Where(static source => source.HintName.StartsWith(
                    "Miya.SchemaBinder.",
                    StringComparison.Ordinal))
                .Select(static source => source.SourceText.ToString()));

        Assert.NotEmpty(consumerOutputs);
        Assert.All(
            consumerOutputs,
            static output => Assert.DoesNotContain(
                output.Reason,
                new[] { IncrementalStepRunReason.Cached, IncrementalStepRunReason.Unchanged }));
        Assert.Contains("must be between 1 and 20", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("must be between 1 and 10", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void Unrelated_source_edit_reuses_openapi_import_output()
    {
        const string path = "api/openapi.json";
        const string openApi = """
            {
              "openapi": "3.1.0",
              "paths": {
                "/health": {
                  "get": { "operationId": "health" }
                }
              }
            }
            """;
        var additionalText = GeneratorTestHelper.AdditionalText(path, openApi);
        var metadata = OpenApiMetadata(path);
        var compilation = GeneratorTestHelper.CreateCompilation("internal static class Application { }");
        var first = GeneratorTestHelper.Run(
            compilation,
            trackSteps: true,
            additionalTexts: [additionalText],
            additionalFileMetadata: metadata);
        var updated = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            "internal static class Unrelated { internal const int Value = 2; }",
            GeneratorTestHelper.ParseOptions,
            "Unrelated.cs"));

        var driver = first.Driver.RunGeneratorsAndUpdateCompilation(updated, out _, out _);
        var result = driver.GetRunResult().Results.Single();

        Assert.All(
            result.TrackedSteps["MiyaOpenApiInputs"].SelectMany(static step => step.Outputs),
            static output => Assert.True(
                output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged));
        Assert.All(
            result.TrackedSteps["MiyaOpenApiSources"].SelectMany(static step => step.Outputs),
            static output => Assert.True(
                output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged));
    }

    [Fact]
    public void Openapi_content_edit_updates_openapi_import_output()
    {
        const string path = "api/openapi.json";
        const string firstOpenApi = """
            { "openapi": "3.1.0", "paths": { "/first": { "get": { "operationId": "first" } } } }
            """;
        const string changedOpenApi = """
            { "openapi": "3.1.0", "paths": { "/second": { "get": { "operationId": "second" } } } }
            """;
        var firstText = GeneratorTestHelper.AdditionalText(path, firstOpenApi);
        var changedText = GeneratorTestHelper.AdditionalText(path, changedOpenApi);
        var compilation = GeneratorTestHelper.CreateCompilation("internal static class Application { }");
        var first = GeneratorTestHelper.Run(
            compilation,
            trackSteps: true,
            additionalTexts: [firstText],
            additionalFileMetadata: OpenApiMetadata(path));

        var driver = first.Driver
            .ReplaceAdditionalText(firstText, changedText)
            .RunGeneratorsAndUpdateCompilation(compilation, out _, out _);
        var generated = string.Join(
            Environment.NewLine,
            driver.GetRunResult().Results.Single().GeneratedSources
                .Where(static source => source.HintName.StartsWith("Miya.OpenApi.", StringComparison.Ordinal))
                .Select(static source => source.SourceText.ToString()));

        Assert.Contains("public const string Second = \"/second\";", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("public const string First = \"/first\";", generated, StringComparison.Ordinal);
    }

    private static void AssertTrackedSourceUnchanged(
        GeneratorRunResult result,
        string stepName,
        string hintNamePart)
    {
        var outputs = result.TrackedSteps[stepName]
            .SelectMany(static step => step.Outputs)
            .Where(output => output.Value is GeneratedSource source
                && source.HintName.Contains(hintNamePart, StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(outputs);
        Assert.All(
            outputs,
            static output => Assert.True(
                output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged));
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> OpenApiMetadata(
        string path) =>
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            [path] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MiyaOpenApi"] = "true",
                ["MiyaOpenApiNamespace"] = "MyApp.Api",
            },
        };
}
