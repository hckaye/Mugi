using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

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

        Assert.NotEmpty(jsonTypeSteps);
        Assert.All(
            jsonTypeSteps.SelectMany(static step => step.Outputs),
            static output => Assert.True(
                output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged));
        Assert.Equal(
            first.Source("Miya.JsonCodecs.g.cs"),
            driver.GetRunResult().Results.Single().GeneratedSources
                .Single(static sourceResult => sourceResult.HintName == "Miya.JsonCodecs.g.cs")
                .SourceText.ToString());
    }
}
