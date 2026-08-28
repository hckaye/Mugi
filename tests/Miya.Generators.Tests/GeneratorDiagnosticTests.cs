namespace Miya.Generators.Tests;

public sealed class GeneratorDiagnosticTests
{
    [Theory]
    [InlineData("MIYA001", "var context = new Context(); context.Json(new { Value = 1 });")]
    [InlineData("MIYA002", "var app = new App(); app.Get(\"missing-slash\", c => c.Text(\"x\"));")]
    [InlineData("MIYA004", "var context = new Context(); context.Json<object>(new object());")]
    public void Invalid_calls_report_expected_diagnostic(string expectedId, string body)
    {
        var source = $$"""
            using Miya;
            internal static class Calls
            {
                internal static void Run()
                {
                    {{body}}
                }
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));

        Assert.Contains(run.Result.Diagnostics, diagnostic => diagnostic.Id == expectedId);
    }

    [Fact]
    public void Sequential_duplicate_on_same_local_reports_warning()
    {
        const string source = """
            using Miya;
            internal static class Calls
            {
                internal static void Run()
                {
                    var app = new App();
                    app.Get("/same", c => c.Text("first"));
                    app.Get("/same", c => c.Text("second"));
                }
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));

        Assert.Contains(run.Result.Diagnostics, diagnostic => diagnostic.Id == "MIYA003");
    }

    [Fact]
    public void Explicit_codec_overloads_do_not_request_generated_object_codecs()
    {
        const string source = """
            using System.Buffers;
            using Miya.Json;
            internal sealed class ObjectCodec : IJsonCodec<object>
            {
                internal static readonly ObjectCodec Instance = new();
                public void Write(ref JsonWriter writer, object? value) => writer.WriteNull();
                public object? Read(ref JsonReader reader) { reader.TryReadNull(); return null; }
            }
            internal static class Calls
            {
                internal static void Run()
                {
                    var buffer = new ArrayBufferWriter<byte>();
                    Json.Serialize<object>(buffer, new object(), ObjectCodec.Instance);
                    Json.Deserialize<object>("null"u8, ObjectCodec.Instance);
                }
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));

        Assert.DoesNotContain(run.Result.Diagnostics, diagnostic => diagnostic.Id == "MIYA004");
        Assert.DoesNotContain(
            run.Compilation.GetDiagnostics(),
            diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }

    [Fact]
    public void Source_record_without_primary_constructor_remains_unsupported()
    {
        const string source = """
            using Miya.Json;
            internal sealed record Payload
            {
                public int Id { get; init; }
            }
            internal static class Calls
            {
                internal static void Run() => Json.Include<Payload>();
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));

        var diagnostic = Assert.Single(run.Result.Diagnostics, diagnostic => diagnostic.Id == "MIYA004");
        Assert.Contains("records must declare a primary constructor", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void Reordered_named_route_arguments_are_mapped_to_their_parameters()
    {
        const string source = """
            using Miya;
            internal static class Calls
            {
                internal static void Run()
                {
                    var app = new App();
                    app.Get(handler: c => c.Text("get"), pattern: "/get/:id");
                    app.On(handler: c => c.Text("on"), pattern: "/on/:id", method: "GET");
                }
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));

        Assert.DoesNotContain(run.Result.Diagnostics, diagnostic => diagnostic.Id == "MIYA002");
        Assert.Contains("/get/:id", run.SourcesWithPrefix("Miya.RouteTemplate."), StringComparison.Ordinal);
        Assert.Contains("/on/:id", run.SourcesWithPrefix("Miya.RouteTemplate."), StringComparison.Ordinal);
        Assert.DoesNotContain(
            run.Compilation.GetDiagnostics(),
            diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }

    [Fact]
    public void Mutually_exclusive_route_registrations_do_not_report_duplicates()
    {
        const string source = """
            using Miya;
            internal static class Calls
            {
                internal static void Run(bool first)
                {
                    var app = new App();
                    if (first)
                    {
                        app.Get("/same", c => c.Text("first"));
                    }
                    else
                    {
                        app.Get("/same", c => c.Text("second"));
                    }
                }
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));

        Assert.DoesNotContain(run.Result.Diagnostics, diagnostic => diagnostic.Id == "MIYA003");
    }
}
