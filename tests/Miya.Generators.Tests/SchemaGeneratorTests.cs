using Microsoft.CodeAnalysis;

namespace Miya.Generators.Tests;

public sealed class SchemaGeneratorTests
{
    [Fact]
    public void Schema_generates_registered_binder_and_body_codec()
    {
        const string source = """
            using Miya;
            using Miya.Schema;

            internal sealed record Input(int Id, string Name, int Age, int Limit);

            internal static class Routes
            {
                internal static void Map()
                {
                    var app = new App();
                    var schema = Schemas.For<Input>()
                        .Route(input => input.Id, rules => rules.Positive())
                        .Body(input => input.Name, rules => rules.NotEmpty().Length(1, 80).MinLength(1).MaxLength(80).Pattern("^[A-Z]").Must(value => value != "Admin", "is reserved"))
                        .Body(input => input.Age, rules => rules.Min(0).Max(120).Range(0, 120).NonNegative())
                        .Query(input => input.Limit, rules => rules.Default(20));
                    app.Post("/people/:Id", schema, static (context, input) => context.Json(input));
                }
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));
        var generated = run.SourcesWithPrefix("Miya.SchemaBinder.");

        Assert.DoesNotContain(AllDiagnostics(run), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("IInputBinder<global::Input>", generated, StringComparison.Ordinal);
        Assert.Contains("context.Param(\"Id\")", generated, StringComparison.Ordinal);
        Assert.Contains("context.Req.Json<BodyValues>()", generated, StringComparison.Ordinal);
        Assert.Contains("must be between 0 and 120", generated, StringComparison.Ordinal);
        Assert.Contains("Regex.IsMatch", generated, StringComparison.Ordinal);
        Assert.Contains("is reserved", generated, StringComparison.Ordinal);
        Assert.Contains("value3 = 20", generated, StringComparison.Ordinal);
        Assert.Contains("BinderRegistry<global::Input>.Register", generated, StringComparison.Ordinal);
        Assert.Contains("Json.Register<", generated, StringComparison.Ordinal);
        Assert.Contains("{\\\"errors\\\":[", generated, StringComparison.Ordinal);
        Assert.Contains("context.Bytes(buffer.WrittenMemory, \"application/json\")", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void Miya006_reports_Param_name_missing_from_direct_handler_route()
    {
        const string source = """
            using Miya;
            internal static class Routes
            {
                internal static void Map()
                {
                    var app = new App();
                    app.Get("/items/:id", context => context.Text(context.Param("other")));
                }
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));

        Assert.Contains(AllDiagnostics(run), diagnostic => diagnostic.Id == "MIYA006");
    }

    [Fact]
    public void Miya010_reports_explicit_route_field_missing_from_pattern()
    {
        const string source = """
            using Miya;
            using Miya.Schema;
            internal sealed record Input(int Id);
            internal static class Routes
            {
                internal static void Map()
                {
                    var app = new App();
                    var schema = Schemas.For<Input>().Route(input => input.Id);
                    app.Get("/items", schema, static (context, input) => context.Text(input.Id.ToString()));
                }
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));

        Assert.Contains(AllDiagnostics(run), diagnostic => diagnostic.Id == "MIYA010");
    }

    [Fact]
    public void Miya011_reports_pattern_parameter_missing_from_input()
    {
        const string source = """
            using Miya;
            using Miya.Schema;
            internal sealed record Input(int Id);
            internal static class Routes
            {
                internal static void Map()
                {
                    var app = new App();
                    var schema = Schemas.For<Input>();
                    app.Get("/items/:Missing", schema, static (context, input) => context.Text(input.Id.ToString()));
                }
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));

        Assert.Contains(AllDiagnostics(run), diagnostic => diagnostic.Id == "MIYA011");
    }

    [Fact]
    public void Miya012_reports_non_scalar_route_field()
    {
        const string source = """
            using System.Collections.Generic;
            using Miya;
            using Miya.Schema;
            internal sealed record Input(List<int> Id);
            internal static class Routes
            {
                internal static void Map()
                {
                    var app = new App();
                    var schema = Schemas.For<Input>();
                    app.Get("/items/:Id", schema, static (context, input) => context.Text(input.Id.Count.ToString()));
                }
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));

        Assert.Contains(AllDiagnostics(run), diagnostic => diagnostic.Id == "MIYA012");
    }

    [Fact]
    public void Miya013_reports_computed_field_selector()
    {
        const string source = """
            using Miya;
            using Miya.Schema;
            internal sealed record Input(int Id);
            internal static class Routes
            {
                internal static void Map()
                {
                    var app = new App();
                    var schema = Schemas.For<Input>().Query(input => input.Id + 1);
                    app.Get("/items", schema, static (context, input) => context.Text(input.Id.ToString()));
                }
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));

        Assert.Contains(AllDiagnostics(run), diagnostic => diagnostic.Id == "MIYA013");
    }

    [Fact]
    public void Miya014_reports_rule_used_with_wrong_field_type()
    {
        const string source = """
            using Miya;
            using Miya.Schema;
            internal sealed record Input(string Name);
            internal static class Routes
            {
                internal static void Map()
                {
                    var app = new App();
                    var schema = Schemas.For<Input>().Query(input => input.Name, rules => rules.Positive());
                    app.Get("/items", schema, static (context, input) => context.Text(input.Name));
                }
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));

        Assert.Contains(AllDiagnostics(run), diagnostic => diagnostic.Id == "MIYA014");
        Assert.DoesNotContain(
            run.Compilation.GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    private static IEnumerable<Diagnostic> AllDiagnostics(GeneratorRun run) =>
        run.DriverDiagnostics.Concat(run.Result.Diagnostics).Concat(run.Compilation.GetDiagnostics());
}
