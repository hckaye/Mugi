using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Miya.Generators.Core;

namespace Miya.Generators.Tests;

public sealed class IncrementalGeneratorTests
{
    [Fact]
    public void Unrelated_edit_reuses_json_type_pipeline_output()
    {
        const string source = """
            using Miya.Json;
            internal sealed record Payload(int Id);
            internal static class Marker { internal static void Include() => MiyaJson.Include<Payload>(); }
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
            internal static class FirstMarker { internal static void Include() => MiyaJson.Include<First>(); }
            """;
        const string changedFirstType = """
            using Miya.Json;
            internal sealed record First(int Id, string Name);
            internal static class FirstMarker { internal static void Include() => MiyaJson.Include<First>(); }
            """;
        const string secondType = """
            using Miya.Json;
            internal sealed record Second(int Id);
            internal static class SecondMarker { internal static void Include() => MiyaJson.Include<Second>(); }
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
}
