using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Miya.Generators.Tests;

public sealed class SchemaPartGeneratorTests
{
    [Fact]
    public void Merged_schema_is_generated_in_declaration_and_part_order_across_namespaces()
    {
        const string partSource = """
            using Miya.Schema;

            namespace SharedParts;

            internal interface IPaging
            {
                int Limit { get; }
                string RequestId { get; }
            }

            internal static class PagingRules
            {
                internal static bool IsAllowed(int value) => value <= 100;
            }

            internal static class Parts
            {
                internal static readonly SchemaPart<IPaging> Paging = Schemas.Part<IPaging>()
                    .Query(input => input.Limit, rules => rules
                        .Default(20)
                        .Range(1, 100)
                        .Must(value => SharedParts.PagingRules.IsAllowed(value), "is not allowed"))
                    .Header(input => input.RequestId, "X-Request-Id");
            }
            """;
        const string schemaSource = """
            using Miya;
            using Miya.Schema;

            namespace Endpoints;

            internal sealed record Input(string RequestId, string Name, int Limit) : SharedParts.IPaging;

            internal static class Routes
            {
                internal static void Map()
                {
                    var app = new App();
                    var schema = Schemas.For<Input>()
                        .Body(input => input.Name)
                        .Use(SharedParts.Parts.Paging);
                    app.Post("/items", schema, static (context, input) => context.Json(input));
                }
            }
            """;
        var compilation = GeneratorTestHelper.CreateCompilation(partSource, "Parts.cs")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(
                schemaSource,
                GeneratorTestHelper.ParseOptions,
                "Schema.cs"));

        var run = GeneratorTestHelper.Run(compilation);
        var generated = run.SourcesWithPrefix("Miya.SchemaBinder.");

        AssertNoErrors(run);
        Assert.Contains("global::System.String value0", generated, StringComparison.Ordinal);
        Assert.Contains("global::System.Int32 value1", generated, StringComparison.Ordinal);
        Assert.Contains("global::System.String value2", generated, StringComparison.Ordinal);
        Assert.True(
            generated.IndexOf("context.Query(\"Limit\")", StringComparison.Ordinal)
                < generated.IndexOf("context.Req.Header(\"X-Request-Id\")", StringComparison.Ordinal));
        Assert.Contains(
            "value => global::SharedParts.PagingRules.IsAllowed(value)",
            generated,
            StringComparison.Ordinal);
        Assert.Contains(
            "new global::Endpoints.Input(value2!, value0!, value1)",
            generated,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Concrete_schema_declaration_overrides_a_part_member()
    {
        const string source = """
            using Miya;
            using Miya.Schema;

            internal interface IPaging { int Limit { get; } }
            internal sealed record Input(int Limit) : IPaging;

            internal static class Routes
            {
                internal static void Map()
                {
                    var app = new App();
                    var part = Schemas.Part<IPaging>()
                        .Query(input => input.Limit, rules => rules.Default(20));
                    var schema = Schemas.For<Input>()
                        .Header(input => input.Limit, "X-Limit", rules => rules.Default(5))
                        .Use(part);
                    app.Get("/items", schema, static (context, input) => context.Json(input));
                }
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));
        var generated = run.SourcesWithPrefix("Miya.SchemaBinder.");

        AssertNoErrors(run);
        Assert.Contains("context.Req.Header(\"X-Limit\")", generated, StringComparison.Ordinal);
        Assert.Contains("value0 = 5", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("context.Query(\"Limit\")", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("value0 = 20", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void Use_requires_the_concrete_input_to_implement_the_part_type()
    {
        const string source = """
            using Miya.Schema;

            internal interface IPaging { int Page { get; } }
            internal sealed record Input(int Page);

            internal static class SchemasForTests
            {
                internal static void Build()
                {
                    var part = Schemas.Part<IPaging>().Query(input => input.Page);
                    _ = Schemas.For<Input>().Use(part);
                }
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));

        Assert.Contains(
            run.Compilation.GetDiagnostics(),
            static diagnostic => diagnostic.Id == "CS0311"
                && diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(
            run.Result.Diagnostics,
            static diagnostic => diagnostic.Id.StartsWith("MIYA", StringComparison.Ordinal));
    }

    [Fact]
    public void MIYA017_reports_duplicate_part_definitions()
    {
        const string source = """
            using Miya.Schema;
            internal interface IPaging { int Page { get; } }
            internal static class Parts
            {
                internal static readonly SchemaPart<IPaging> First =
                    Schemas.Part<IPaging>().Query(input => input.Page);
                internal static readonly SchemaPart<IPaging> Second =
                    Schemas.Part<IPaging>().Query(input => input.Page);
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));

        AssertDiagnostic(
            run,
            "MIYA017",
            "Schema part type 'IPaging' has more than one definition; declare each part type only once");
    }

    [Fact]
    public void MIYA018_reports_a_part_declared_in_another_assembly()
    {
        const string externalSource = """
            using Miya.Schema;
            namespace External;
            public interface IPaging { int Page { get; } }
            public static class Parts
            {
                public static SchemaPart<IPaging> Paging => throw new System.NotSupportedException();
            }
            """;
        var externalCompilation = CSharpCompilation.Create(
            "ExternalParts",
            [CSharpSyntaxTree.ParseText(externalSource, GeneratorTestHelper.ParseOptions, "External.cs")],
            GeneratorTestHelper.References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        var emit = externalCompilation.Emit(image);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        var externalReference = MetadataReference.CreateFromImage(image.ToArray());

        const string source = """
            using Miya.Schema;
            internal sealed record Input(int Page) : External.IPaging;
            internal static class SchemaForTests
            {
                internal static void Build() =>
                    _ = Schemas.For<Input>().Use(External.Parts.Paging);
            }
            """;
        var compilation = GeneratorTestHelper.CreateCompilation(source).AddReferences(externalReference);
        var run = GeneratorTestHelper.Run(compilation);

        AssertDiagnostic(
            run,
            "MIYA018",
            "Schema part 'External.IPaging' has no definition; parts must be declared in the same compilation");
    }

    [Fact]
    public void MIYA019_reports_explicit_interface_implementation()
    {
        const string source = """
            using Miya.Schema;
            internal interface IPaging { int Page { get; } }
            internal sealed record Input(int Value) : IPaging
            {
                int IPaging.Page => Value;
            }
            internal static class SchemaForTests
            {
                internal static void Build()
                {
                    var part = Schemas.Part<IPaging>().Query(input => input.Page);
                    _ = Schemas.For<Input>().Use(part);
                }
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));

        AssertDiagnostic(
            run,
            "MIYA019",
            "Schema part member 'Page' on 'Input' is implemented explicitly; implement the member implicitly");
    }

    [Fact]
    public void MIYA024_reports_a_member_contributed_by_two_parts()
    {
        const string source = """
            using Miya.Schema;
            internal interface IFirstPaging { int Page { get; } }
            internal interface ISecondPaging { int Page { get; } }
            internal sealed record Input(int Page) : IFirstPaging, ISecondPaging;
            internal static class SchemaForTests
            {
                internal static void Build()
                {
                    var first = Schemas.Part<IFirstPaging>().Query(input => input.Page);
                    var second = Schemas.Part<ISecondPaging>().Query(input => input.Page);
                    _ = Schemas.For<Input>().Use(first).Use(second);
                }
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));

        AssertDiagnostic(
            run,
            "MIYA024",
            "Schema part member 'Page' on 'Input' is contributed by more than one part");
    }

    [Fact]
    public void MIYA024_reports_the_same_part_applied_twice()
    {
        const string source = """
            using Miya.Schema;
            internal interface IPaging { int Page { get; } }
            internal sealed record Input(int Page) : IPaging;
            internal static class SchemaForTests
            {
                internal static void Build()
                {
                    var paging = Schemas.Part<IPaging>().Query(input => input.Page);
                    _ = Schemas.For<Input>().Use(paging).Use(paging);
                }
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));

        AssertDiagnostic(
            run,
            "MIYA024",
            "Schema part member 'Page' on 'Input' is contributed by more than one part");
    }

    [Fact]
    public void Existing_form_body_conflict_validation_runs_after_part_merging()
    {
        const string source = """
            using Miya;
            using Miya.Schema;
            internal interface IFormName { string Name { get; } }
            internal sealed record Input(string Name, int Value) : IFormName;
            internal static class Routes
            {
                internal static void Map()
                {
                    var app = new App();
                    var part = Schemas.Part<IFormName>().Form(input => input.Name);
                    var schema = Schemas.For<Input>()
                        .Body(input => input.Value)
                        .Use(part);
                    app.Post("/items", schema, static (context, input) => context.Json(input));
                }
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));

        AssertDiagnostic(
            run,
            "MIYA016",
            "Schema for 'Input' maps fields from both form data and the JSON body");
    }

    private static void AssertDiagnostic(GeneratorRun run, string id, string message)
    {
        var diagnostic = Assert.Single(run.Result.Diagnostics, diagnostic => diagnostic.Id == id);
        Assert.Equal(message, diagnostic.GetMessage());
    }

    private static void AssertNoErrors(GeneratorRun run)
    {
        Assert.DoesNotContain(
            run.Result.Diagnostics,
            static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(
            run.Compilation.GetDiagnostics(),
            static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }
}
