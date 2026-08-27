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
                    Json.Include<Payload>();
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
                    Json.Serialize(buffer, value);
                    var copy = Json.Deserialize<Payload>(buffer.WrittenSpan)!;
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
                    Json.Include<Payload>();
                    Json.Deserialize<Payload>("{}"u8);
                }
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));
        AssertNoErrors(run);
        var assembly = GeneratorTestHelper.EmitAndLoad(run.Compilation);

        var exception = Assert.Throws<TargetInvocationException>(
            () => assembly.GetType("Runner")!.GetMethod("Run")!.Invoke(null, null));
        var jsonException = Assert.IsType<Miya.Json.JsonException>(exception.InnerException);
        Assert.True(jsonException.IsInputError);
    }

    [Fact]
    public void Generated_reader_honors_cancellation_for_large_and_deep_inputs()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using System.Text;
            using System.Threading;
            using Miya.Json;

            public sealed class Node { public Node? Next { get; set; } }
            public static class Runner
            {
                private static JsonOptions CancelledOptions()
                {
                    var source = new CancellationTokenSource();
                    source.Cancel();
                    return new JsonOptions { CancellationToken = source.Token, MaxDepth = 512 };
                }

                public static void ReadCollection()
                {
                    var json = Encoding.UTF8.GetBytes("[" + string.Join(',', new int[10_000]) + "]");
                    Json.Deserialize<List<int>>(json, CancelledOptions());
                }

                public static void ReadDeep()
                {
                    var json = Encoding.UTF8.GetBytes(new string('[', 256) + "null" + new string(']', 256));
                    Json.Deserialize<List<List<int>>>(json, CancelledOptions());
                }
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));
        AssertNoErrors(run);
        var assembly = GeneratorTestHelper.EmitAndLoad(run.Compilation);
        var runner = assembly.GetType("Runner")!;

        var collection = Assert.Throws<TargetInvocationException>(
            () => runner.GetMethod("ReadCollection")!.Invoke(null, null));
        var deep = Assert.Throws<TargetInvocationException>(
            () => runner.GetMethod("ReadDeep")!.Invoke(null, null));
        Assert.IsType<OperationCanceledException>(collection.InnerException);
        Assert.IsType<OperationCanceledException>(deep.InnerException);
    }

    [Fact]
    public void Generated_writer_honors_cancellation_for_large_and_deep_values()
    {
        const string source = """
            using System.Buffers;
            using System.Collections.Generic;
            using System.Threading;
            using Miya.Json;

            public sealed class Node { public Node? Next { get; set; } }
            public static class Runner
            {
                private static JsonOptions CancelledOptions()
                {
                    var source = new CancellationTokenSource();
                    source.Cancel();
                    return new JsonOptions { CancellationToken = source.Token, MaxDepth = 512 };
                }

                public static void WriteCollection()
                {
                    var buffer = new ArrayBufferWriter<byte>();
                    Json.Serialize(buffer, new int[10_000], CancelledOptions());
                }

                public static void WriteDeep()
                {
                    var node = new Node();
                    var current = node;
                    for (var index = 0; index < 256; index++)
                    {
                        current.Next = new Node();
                        current = current.Next;
                    }

                    var buffer = new ArrayBufferWriter<byte>();
                    Json.Serialize(buffer, node, CancelledOptions());
                }

                public static void WriteLargeString()
                {
                    var buffer = new ArrayBufferWriter<byte>();
                    Json.Serialize(buffer, new string('x', 128 * 1024), CancelledOptions());
                }
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));
        AssertNoErrors(run);
        var assembly = GeneratorTestHelper.EmitAndLoad(run.Compilation);
        var runner = assembly.GetType("Runner")!;

        foreach (var method in new[] { "WriteCollection", "WriteDeep", "WriteLargeString" })
        {
            var exception = Assert.Throws<TargetInvocationException>(
                () => runner.GetMethod(method)!.Invoke(null, null));
            Assert.IsType<OperationCanceledException>(exception.InnerException);
        }
    }

    [Fact]
    public void Generated_writer_honors_configured_depth_and_collection_limits()
    {
        const string source = """
            using System.Buffers;
            using System.Collections.Generic;
            using Miya.Json;

            public sealed class Child { public int Id { get; set; } }
            public sealed class Payload { public Child? Child { get; set; } }
            public static class Runner
            {
                public static void WriteDepth()
                {
                    var buffer = new ArrayBufferWriter<byte>();
                    Json.Serialize(buffer, new Payload { Child = new Child { Id = 1 } },
                        new JsonOptions { MaxDepth = 1 });
                }

                public static void WriteCollection()
                {
                    var buffer = new ArrayBufferWriter<byte>();
                    Json.Serialize(buffer, new List<int> { 1, 2 },
                        new JsonOptions { MaxCollectionSize = 1 });
                }

                public static void WriteArray()
                {
                    var buffer = new ArrayBufferWriter<byte>();
                    Json.Serialize(buffer, new int[] { 1, 2 },
                        new JsonOptions { MaxCollectionSize = 1 });
                }

                public static void WriteDictionary()
                {
                    var buffer = new ArrayBufferWriter<byte>();
                    Json.Serialize(buffer, new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 },
                        new JsonOptions { MaxCollectionSize = 1 });
                }
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));
        AssertNoErrors(run);
        var assembly = GeneratorTestHelper.EmitAndLoad(run.Compilation);
        var runner = assembly.GetType("Runner")!;

        var depth = Assert.Throws<TargetInvocationException>(
            () => runner.GetMethod("WriteDepth")!.Invoke(null, null));
        var collection = Assert.Throws<TargetInvocationException>(
            () => runner.GetMethod("WriteCollection")!.Invoke(null, null));
        var array = Assert.Throws<TargetInvocationException>(
            () => runner.GetMethod("WriteArray")!.Invoke(null, null));
        var dictionary = Assert.Throws<TargetInvocationException>(
            () => runner.GetMethod("WriteDictionary")!.Invoke(null, null));
        Assert.False(Assert.IsType<Miya.Json.JsonException>(depth.InnerException).IsInputError);
        Assert.False(Assert.IsType<Miya.Json.JsonException>(collection.InnerException).IsInputError);
        Assert.False(Assert.IsType<Miya.Json.JsonException>(array.InnerException).IsInputError);
        Assert.False(Assert.IsType<Miya.Json.JsonException>(dictionary.InnerException).IsInputError);
    }

    [Fact]
    public void Generated_small_integer_overflow_is_classified_as_input_error()
    {
        const string source = """
            using Miya.Json;
            public static class Runner
            {
                public static void Run() => Json.Deserialize<byte>("256"u8);
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));
        AssertNoErrors(run);
        var assembly = GeneratorTestHelper.EmitAndLoad(run.Compilation);

        var invocation = Assert.Throws<TargetInvocationException>(
            () => assembly.GetType("Runner")!.GetMethod("Run")!.Invoke(null, null));
        var exception = Assert.IsType<Miya.Json.JsonException>(invocation.InnerException);
        Assert.True(exception.IsInputError);
        Assert.IsType<OverflowException>(exception.InnerException);
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
