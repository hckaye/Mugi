using System.Reflection;
using System.Text.Json;
using Microsoft.CodeAnalysis;

namespace Miya.Generators.Tests;

public sealed class GeneratorExecutionTests
{
    [Fact]
    public void Generated_codecs_serialize_and_deserialize_supported_model_graph()
    {
        const string source = """
            using System;
            using System.Buffers;
            using System.Collections.Generic;
            using System.Text;
            using Miya.Json;

            public enum State : short { None, Ready = 7 }
            public sealed record Child(int Id, string? Note);
            public sealed class Node
            {
                public int Value { get; set; }
                public Node? Next { get; set; }
            }
            public sealed class Payload
            {
                public required string Name { get; init; }
                public Child? Child { get; init; }
                public List<int?> Values { get; init; } = [];
                public Dictionary<string, Child?> Children { get; init; } = [];
                public State State { get; init; }
                public Node? Root { get; init; }
            }
            public static class Runner
            {
                public static string Run()
                {
                    MiyaJson.Include<Payload>();
                    var value = new Payload
                    {
                        Name = "sample",
                        Child = new Child(3, null),
                        Values = [1, null, 5],
                        Children = new Dictionary<string, Child?> { ["first"] = new Child(9, "note") },
                        State = State.Ready,
                        Root = new Node { Value = 1, Next = new Node { Value = 2 } },
                    };
                    var buffer = new ArrayBufferWriter<byte>();
                    MiyaJson.Serialize(buffer, value);
                    var copy = MiyaJson.Deserialize<Payload>(buffer.WrittenSpan)!;
                    return Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n" +
                        copy.Name + ":" + copy.Child!.Id + ":" + copy.Values.Count + ":" +
                        copy.Children["first"]!.Note + ":" + (short)copy.State + ":" + copy.Root!.Next!.Value;
                }
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));
        AssertNoErrors(run);
        var assembly = GeneratorTestHelper.EmitAndLoad(run.Compilation);

        var result = Assert.IsType<string>(assembly.GetType("Runner")!.GetMethod("Run")!.Invoke(null, null));
        var parts = result.Split('\n');
        using var actual = JsonDocument.Parse(parts[0]);
        using var expected = JsonDocument.Parse(
            """{"name":"sample","child":{"id":3,"note":null},"values":[1,null,5],"children":{"first":{"id":9,"note":"note"}},"state":7,"root":{"value":1,"next":{"value":2,"next":null}}}""");
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement));
        Assert.Equal("sample:3:3:note:7:2", parts[1]);
    }

    [Fact]
    public void Required_property_missing_throws_miya_json_exception()
    {
        const string source = """
            using Miya.Json;
            public sealed class Payload { public required string Name { get; init; } }
            public static class Runner
            {
                public static void Run()
                {
                    MiyaJson.Include<Payload>();
                    MiyaJson.Deserialize<Payload>("{}"u8);
                }
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));
        AssertNoErrors(run);
        var assembly = GeneratorTestHelper.EmitAndLoad(run.Compilation);

        var exception = Assert.Throws<TargetInvocationException>(
            () => assembly.GetType("Runner")!.GetMethod("Run")!.Invoke(null, null));
        Assert.IsType<Miya.Json.MiyaJsonException>(exception.InnerException);
    }

    private static void AssertNoErrors(GeneratorRun run)
    {
        var errors = run.DriverDiagnostics
            .Concat(run.Result.Diagnostics)
            .Concat(run.Compilation.GetDiagnostics())
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.Empty(errors);
    }
}
