using System.Reflection;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Miya.Generators.Tests;

public sealed class GeneratorExecutionTests
{
    [Fact]
    public void Null_conditional_json_call_generates_and_registers_its_codec()
    {
        const string source = """
            using System.Threading.Tasks;
            using Miya;
            using Miya.Json;

            public sealed record Payload(int Id);
            public static class Runner
            {
                public static ValueTask? Write(Context? context) =>
                    context?.Json(new Payload(42));

                public static bool Run() => Json.GetCodec<Payload>() is not null;
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));
        AssertNoErrors(run);
        Assert.Contains(
            "IJsonCodec<global::Payload>",
            run.SourcesWithPrefix("Miya.JsonCodec."),
            StringComparison.Ordinal);
        var assembly = GeneratorTestHelper.EmitAndLoad(run.Compilation);

        Assert.True(Assert.IsType<bool>(
            assembly.GetType("Runner")!.GetMethod("Run")!.Invoke(null, null)));
    }

    [Fact]
    public void Metadata_records_generate_compilable_round_trip_codecs()
    {
        const string contractsSource = """
            namespace Contracts;

            public sealed record Simple(string Id, string Name);
            public sealed record Extended(string Id)
            {
                public string Note { get; init; } = string.Empty;
            }
            public readonly record struct Point(int X, int Y);
            public static class Container
            {
                public sealed record Nested(string Value);
            }
            public sealed record Envelope(
                Simple Simple,
                Extended Extended,
                Point Point,
                Container.Nested Nested);
            """;
        var contracts = EmitReference(GeneratorTestHelper.CreateCompilation(contractsSource));
        const string consumerSource = """
            using System.Buffers;
            using Contracts;
            using Miya.Json;

            public static class Runner
            {
                public static string Run()
                {
                    Json.Include<Envelope>();
                    var value = new Envelope(
                        new Simple("1", "Ada"),
                        new Extended("2") { Note = "extra" },
                        new Point(3, 4),
                        new Container.Nested("inside"));
                    var buffer = new ArrayBufferWriter<byte>();
                    Json.Serialize(buffer, value);
                    var copy = Json.Deserialize<Envelope>(buffer.WrittenSpan)!;
                    return copy.Simple.Name + ":" + copy.Extended.Note + ":" +
                        copy.Point.X + ":" + copy.Nested.Value;
                }
            }
            """;
        var compilation = GeneratorTestHelper.CreateCompilation(consumerSource)
            .AddReferences(contracts.Reference);

        var run = GeneratorTestHelper.Run(compilation);
        AssertNoErrors(run);
        var assembly = GeneratorTestHelper.EmitAndLoad(run.Compilation);

        var result = Assert.IsType<string>(assembly.GetType("Runner")!.GetMethod("Run")!.Invoke(null, null));
        Assert.Equal("Ada:extra:3:inside", result);
        GC.KeepAlive(contracts.Assembly);
    }

    [Fact]
    public void Metadata_record_uses_positional_constructor_instead_of_longer_secondary_constructor()
    {
        const string contractsSource = """
            namespace Contracts;

            public sealed record MetadataItem(string Id)
            {
                public static int SecondaryConstructorCalls { get; private set; }

                public string Note { get; init; } = string.Empty;

                public MetadataItem(string Id, string Note) : this(Id)
                {
                    SecondaryConstructorCalls++;
                    this.Note = Note;
                }
            }
            """;
        var contracts = EmitReference(GeneratorTestHelper.CreateCompilation(contractsSource));
        const string consumerSource = """
            using Contracts;
            using Miya.Json;

            public static class Runner
            {
                public static string Run()
                {
                    Json.Include<MetadataItem>();
                    var value = Json.Deserialize<MetadataItem>("{\"id\":\"7\",\"note\":\"kept\"}"u8)!;
                    return value.Id + ":" + value.Note + ":" + MetadataItem.SecondaryConstructorCalls;
                }
            }
            """;
        var compilation = GeneratorTestHelper.CreateCompilation(consumerSource)
            .AddReferences(contracts.Reference);

        var run = GeneratorTestHelper.Run(compilation);
        AssertNoErrors(run);
        var assembly = GeneratorTestHelper.EmitAndLoad(run.Compilation);

        Assert.Equal(
            "7:kept:0",
            assembly.GetType("Runner")!.GetMethod("Run")!.Invoke(null, null));
        GC.KeepAlive(contracts.Assembly);
    }

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
    public void Record_constructor_parameters_enforce_presence_and_honor_defaults()
    {
        const string source = """
            using Miya.Json;

            public sealed record User(string Id, string Name);
            public sealed record WithDefault(int Count = 42, string Label = "default");
            public sealed record NullableValue(string? Value);
            public sealed record Outer(User Inner);
            public static class Runner
            {
                public static string Defaults()
                {
                    Json.Include<WithDefault>();
                    var value = Json.Deserialize<WithDefault>("{}"u8)!;
                    return value.Count + ":" + value.Label;
                }

                public static bool ExplicitNull()
                {
                    Json.Include<NullableValue>();
                    return Json.Deserialize<NullableValue>("{\"value\":null}"u8)!.Value is null;
                }

                public static void Missing() => Json.Deserialize<User>("{}"u8);
                public static void Nested() => Json.Deserialize<Outer>("{\"inner\":{}}"u8);
                public static void Array() => Json.Deserialize<User[]>("[{}]"u8);
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));
        AssertNoErrors(run);
        var assembly = GeneratorTestHelper.EmitAndLoad(run.Compilation);
        var runner = assembly.GetType("Runner")!;

        Assert.Equal("42:default", runner.GetMethod("Defaults")!.Invoke(null, null));
        Assert.True(Assert.IsType<bool>(runner.GetMethod("ExplicitNull")!.Invoke(null, null)));
        AssertMissingField(runner, "Missing", "id");
        AssertMissingField(runner, "Nested", "id");
        AssertMissingField(runner, "Array", "id");
    }

    [Fact]
    public void Pascal_case_record_errors_name_the_pascal_case_field()
    {
        const string source = """
            using Miya.Json;
            public sealed record User(string Id, string Name);
            public static class Runner
            {
                public static void Run() => Json.Deserialize<User>("{\"Name\":\"Ada\"}"u8);
            }
            """;

        var run = GeneratorTestHelper.Run(
            GeneratorTestHelper.CreateCompilation(source),
            naming: "PascalCase");
        AssertNoErrors(run);
        var assembly = GeneratorTestHelper.EmitAndLoad(run.Compilation);

        AssertMissingField(assembly.GetType("Runner")!, "Run", "Id");
    }

    [Fact]
    public async Task Missing_record_constructor_field_returns_400_through_an_app()
    {
        const string source = """
            using System;
            using System.Net.Http;
            using System.Text;
            using System.Threading.Tasks;
            using Miya;

            public sealed record User(string Id, string Name);
            public static class Runner
            {
                public static async Task<int> Run()
                {
                    var app = new App();
                    app.Post("/users", async context =>
                    {
                        var user = await context.Req.Json<User>();
                        await context.Json(user);
                    });
                    await using var server = await app.StartAsync(new AppOptions
                    {
                        Port = 0,
                        ShutdownTimeout = TimeSpan.FromSeconds(2),
                    });
                    using var client = new HttpClient { BaseAddress = new Uri(server.Addresses[0]) };
                    using var content = new StringContent("{}", Encoding.UTF8, "application/json");
                    using var response = await client.PostAsync("/users", content);
                    return (int)response.StatusCode;
                }
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));
        AssertNoErrors(run);
        var assembly = GeneratorTestHelper.EmitAndLoad(run.Compilation);
        var task = Assert.IsAssignableFrom<Task<int>>(
            assembly.GetType("Runner")!.GetMethod("Run")!.Invoke(null, null));

        Assert.Equal(400, await task);
    }

    [Fact]
    public async Task Pascal_case_schema_errors_use_pascal_case_field_names_at_runtime()
    {
        const string source = """
            using System;
            using System.Net.Http;
            using System.Threading.Tasks;
            using Miya;
            using Miya.Schema;

            public sealed record Input(int UserId);
            public static class Runner
            {
                public static async Task<string> Run()
                {
                    var app = new App();
                    var schema = Schemas.For<Input>().Query(input => input.UserId);
                    app.Get("/items", schema, static (context, input) => context.Text(input.UserId.ToString()));
                    await using var server = await app.StartAsync(new AppOptions
                    {
                        Port = 0,
                        ShutdownTimeout = TimeSpan.FromSeconds(2),
                    });
                    using var client = new HttpClient { BaseAddress = new Uri(server.Addresses[0]) };
                    using var response = await client.GetAsync("/items?UserId=invalid");
                    return await response.Content.ReadAsStringAsync();
                }
            }
            """;

        var run = GeneratorTestHelper.Run(
            GeneratorTestHelper.CreateCompilation(source),
            naming: "PascalCase");
        AssertNoErrors(run);
        var assembly = GeneratorTestHelper.EmitAndLoad(run.Compilation);
        var task = Assert.IsAssignableFrom<Task<string>>(
            assembly.GetType("Runner")!.GetMethod("Run")!.Invoke(null, null));
        using var body = JsonDocument.Parse(await task);

        var error = Assert.Single(body.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Equal("UserId", error.GetProperty("field").GetString());
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

    private static void AssertMissingField(Type runner, string method, string field)
    {
        var exception = Assert.Throws<TargetInvocationException>(
            () => runner.GetMethod(method)!.Invoke(null, null));
        var jsonException = Assert.IsType<Miya.Json.JsonException>(exception.InnerException);
        Assert.True(jsonException.IsInputError);
        Assert.Contains("'" + field + "'", jsonException.Message, StringComparison.Ordinal);
    }

    private static (MetadataReference Reference, Assembly Assembly) EmitReference(
        CSharpCompilation compilation)
    {
        using var pe = new MemoryStream();
        var result = compilation.Emit(pe);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var image = pe.ToArray();
        using var loadStream = new MemoryStream(image, writable: false);
        var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(
            loadStream);
        return (MetadataReference.CreateFromImage(image), assembly);
    }
}
