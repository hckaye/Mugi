using Microsoft.CodeAnalysis;

namespace Mugi.Generators.Tests;

public sealed class SchemaGeneratorTests
{
    [Fact]
    public void Schema_generates_registered_binder_and_body_codec()
    {
        const string source = """
            using Mugi;
            using Mugi.Schema;

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
        var generated = run.SourcesWithPrefix("Mugi.SchemaBinder.");

        Assert.DoesNotContain(AllDiagnostics(run), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("IInputBinder<global::Input>", generated, StringComparison.Ordinal);
        Assert.Contains("context.Param(\"Id\")", generated, StringComparison.Ordinal);
        Assert.Contains("context.Req.Json<BodyValues>()", generated, StringComparison.Ordinal);
        Assert.Contains("must be between 0 and 120", generated, StringComparison.Ordinal);
        Assert.Contains(
            "private static readonly global::System.Text.RegularExpressions.Regex Pattern0 = " +
            "global::Mugi.Schema.SchemaRegex.Create(\"^[A-Z]\");",
            generated,
            StringComparison.Ordinal);
        Assert.Contains("SchemaRegex.IsMatch(Pattern0, value1)", generated, StringComparison.Ordinal);
        Assert.Contains("is reserved", generated, StringComparison.Ordinal);
        Assert.Contains("value3 = 20", generated, StringComparison.Ordinal);
        Assert.Contains("BinderRegistry<global::Input>.Register", generated, StringComparison.Ordinal);
        Assert.Contains("Json.Register<", generated, StringComparison.Ordinal);
        Assert.Contains("{\\\"errors\\\":[", generated, StringComparison.Ordinal);
        Assert.Contains("context.Bytes(buffer.WrittenMemory, \"application/json\")", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void Schema_form_reads_form_data_once_and_uses_shared_text_parsers()
    {
        const string source = """
            using System;
            using Mugi;
            using Mugi.Schema;

            internal enum State { Ready }
            internal sealed record Input(string Name, int Count, Guid Id, State State);

            internal static class Routes
            {
                internal static void Map()
                {
                    var app = new App();
                    var schema = Schemas.For<Input>()
                        .Form(input => input.Name)
                        .Form(input => input.Count)
                        .Form(input => input.Id)
                        .Form(input => input.State);
                    app.Post("/form", schema, static (context, input) => context.Text(input.Name));
                }
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));
        var generated = run.SourcesWithPrefix("Mugi.SchemaBinder.");

        Assert.DoesNotContain(AllDiagnostics(run), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Equal(1, Count(generated, "context.Req.Form().ConfigureAwait(false)"));
        Assert.Contains("form.Get(\"Name\")", generated, StringComparison.Ordinal);
        Assert.Contains("form.Get(\"Count\")", generated, StringComparison.Ordinal);
        Assert.Contains("SchemaText.TryParseInteger<global::System.Int32>", generated, StringComparison.Ordinal);
        Assert.Contains("global::System.Guid.TryParse", generated, StringComparison.Ordinal);
        Assert.Contains("global::System.Enum.TryParse<global::State>", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("context.Req.Json<BodyValues>()", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void Schema_without_form_keeps_form_loading_out_of_generated_code()
    {
        const string source = """
            using Mugi;
            using Mugi.Schema;

            internal sealed record Input(int Count);
            internal static class Routes
            {
                internal static void Map()
                {
                    var app = new App();
                    var schema = Schemas.For<Input>().Query(input => input.Count);
                    app.Get("/items", schema, static (context, input) => context.Text(input.Count.ToString()));
                }
            }
            """;

        var first = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));
        var second = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));

        Assert.Equal(
            first.SourcesWithPrefix("Mugi.SchemaBinder."),
            second.SourcesWithPrefix("Mugi.SchemaBinder."));
        Assert.DoesNotContain("context.Req.Form()", first.SourcesWithPrefix("Mugi.SchemaBinder."), StringComparison.Ordinal);
    }

    [Fact]
    public void Pascal_case_naming_is_used_for_validation_error_fields()
    {
        const string source = """
            using Mugi;
            using Mugi.Schema;
            internal sealed record Input(int UserId);
            internal static class Routes
            {
                internal static void Map()
                {
                    var app = new App();
                    var schema = Schemas.For<Input>().Query(input => input.UserId);
                    app.Get("/items", schema, static (context, input) => context.Text(input.UserId.ToString()));
                }
            }
            """;

        var run = GeneratorTestHelper.Run(
            GeneratorTestHelper.CreateCompilation(source),
            naming: "PascalCase");
        var generated = run.SourcesWithPrefix("Mugi.SchemaBinder.");

        Assert.DoesNotContain(AllDiagnostics(run), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains(
            "new global::Mugi.Schema.ValidationError(\"UserId\", \"has an invalid value\")",
            generated,
            StringComparison.Ordinal);
        Assert.Contains(
            "new global::Mugi.Schema.ValidationError(\"UserId\", \"is required\")",
            generated,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Mugi006_reports_Param_name_missing_from_direct_handler_route()
    {
        const string source = """
            using Mugi;
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

        Assert.Contains(AllDiagnostics(run), diagnostic => diagnostic.Id == "MUGI006");
    }

    [Fact]
    public void Mugi010_reports_explicit_route_field_missing_from_pattern()
    {
        const string source = """
            using Mugi;
            using Mugi.Schema;
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

        Assert.Contains(AllDiagnostics(run), diagnostic => diagnostic.Id == "MUGI010");
    }

    [Fact]
    public void Mugi011_reports_pattern_parameter_missing_from_input()
    {
        const string source = """
            using Mugi;
            using Mugi.Schema;
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

        Assert.Contains(AllDiagnostics(run), diagnostic => diagnostic.Id == "MUGI011");
    }

    [Fact]
    public void Mugi012_reports_non_scalar_route_field()
    {
        const string source = """
            using System.Collections.Generic;
            using Mugi;
            using Mugi.Schema;
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

        Assert.Contains(AllDiagnostics(run), diagnostic => diagnostic.Id == "MUGI012");
    }

    [Fact]
    public void Mugi013_reports_computed_field_selector()
    {
        const string source = """
            using Mugi;
            using Mugi.Schema;
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

        Assert.Contains(AllDiagnostics(run), diagnostic => diagnostic.Id == "MUGI013");
    }

    [Fact]
    public void Mugi014_reports_rule_used_with_wrong_field_type()
    {
        const string source = """
            using Mugi;
            using Mugi.Schema;
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

        Assert.Contains(AllDiagnostics(run), diagnostic => diagnostic.Id == "MUGI014");
        Assert.DoesNotContain(
            run.Compilation.GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Mugi016_reports_form_and_body_mappings_in_one_schema()
    {
        const string source = """
            using Mugi;
            using Mugi.Schema;
            internal sealed record Input(string Name, int Age);
            internal static class Routes
            {
                internal static void Map()
                {
                    var app = new App();
                    var schema = Schemas.For<Input>()
                        .Form(input => input.Name)
                        .Body(input => input.Age);
                    app.Post("/items", schema, static (context, input) => context.Text(input.Name));
                }
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));

        Assert.Contains(AllDiagnostics(run), diagnostic => diagnostic.Id == "MUGI016");
    }

    [Fact]
    public void Form_can_be_combined_with_route_query_and_header_mappings()
    {
        const string source = """
            using Mugi;
            using Mugi.Schema;
            internal sealed record Input(int Id, string Query, string RequestId, string Token);
            internal static class Routes
            {
                internal static void Map()
                {
                    var app = new App();
                    var schema = Schemas.For<Input>()
                        .Route(input => input.Id)
                        .Query(input => input.Query)
                        .Header(input => input.RequestId, "X-Request-Id")
                        .Form(input => input.Token);
                    app.Get("/items/:Id", schema, static (context, input) => context.Text(input.Token));
                }
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));

        Assert.DoesNotContain(AllDiagnostics(run), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void FormFile_selector_reports_unsupported_schema_field_type()
    {
        const string source = """
            using Mugi;
            using Mugi.Schema;

            internal sealed record Input(FormFile Upload);
            internal static class Routes
            {
                internal static void Map()
                {
                    var app = new App();
                    var schema = Schemas.For<Input>().Form(input => input.Upload);
                    app.Post("/upload", schema, static (context, input) => context.Text(input.Upload.FileName));
                }
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));

        Assert.Contains(
            AllDiagnostics(run),
            diagnostic => diagnostic.Id == "MUGI012" && diagnostic.GetMessage().Contains("form", StringComparison.Ordinal));
    }

    private static IEnumerable<Diagnostic> AllDiagnostics(GeneratorRun run) =>
        run.DriverDiagnostics.Concat(run.Result.Diagnostics).Concat(run.Compilation.GetDiagnostics());

    private static int Count(string text, string value) =>
        text.Split(value, StringSplitOptions.None).Length - 1;
}
