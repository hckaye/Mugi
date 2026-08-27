using Microsoft.CodeAnalysis;

namespace Miya.Generators.Tests;

public sealed class GeneratorSnapshotTests
{
    [Fact]
    public void Representative_models_generate_compilable_shared_codecs()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using Miya.Json;

            internal enum State : short { None, Ready = 7 }
            internal sealed record Child(int Id, string? Note);
            internal sealed class Node
            {
                public int Value { get; set; }
                public Node? Next { get; set; }
            }
            internal sealed class Payload
            {
                public required string Name { get; init; }
                public Child? Child { get; init; }
                public List<int?> Values { get; init; } = [];
                public Dictionary<string, Child?> Children { get; init; } = [];
                public State State { get; init; }
                public Node? Root { get; init; }
                public Guid Id { get; init; }
                public DateTime Created { get; init; }
                public decimal Amount { get; init; }
            }
            internal static class Marker
            {
                internal static void Include() => MiyaJson.Include<Payload>();
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));

        Assert.Empty(run.DriverDiagnostics);
        Assert.DoesNotContain(run.Result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(run.Compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var codecs = run.SourcesWithPrefix("Miya.JsonCodec.");
        Assert.Contains("writer.EnterContainer", codecs, StringComparison.Ordinal);
        Assert.Contains("if ((++index & 4095) == 0)", codecs, StringComparison.Ordinal);
        Assert.Contains("writer.ThrowIfCancellationRequested();", codecs, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteValue(ref writer, value, 0)", codecs, StringComparison.Ordinal);
        Assert.DoesNotContain("private const int MaxDepth = 64;", codecs, StringComparison.Ordinal);
        Assert.Contains("MemoryExtensions.SequenceEqual(propertyName, \"name\"u8)", codecs, StringComparison.Ordinal);
        Assert.Contains("reader.SkipValue();", codecs, StringComparison.Ordinal);
        Assert.Contains("writer.WriteRaw(\"{\\\"name\\\":\"u8);", codecs, StringComparison.Ordinal);
        Assert.Contains("global::System.Collections.Generic.Dictionary", codecs, StringComparison.Ordinal);
        Assert.Contains("MiyaJsonGeneratedRegistration", codecs, StringComparison.Ordinal);
    }

    [Fact]
    public void Pascal_case_naming_is_applied_at_generation_time()
    {
        const string source = """
            using Miya.Json;
            internal sealed record Payload(int UserId);
            internal static class Marker { internal static void Include() => MiyaJson.Include<Payload>(); }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source), "PascalCase");

        Assert.Contains("{\\\"UserId\\\":", run.SourcesWithPrefix("Miya.JsonCodec."), StringComparison.Ordinal);
    }
}
