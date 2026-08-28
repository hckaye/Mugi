using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Miya.Generators.Tests;

public sealed class SchemaRuleMethodGeneratorTests
{
    [Fact]
    public void Shared_rule_method_body_is_transplanted_into_the_generated_binder()
    {
        const string source = """
            using Miya;
            using Miya.Schema;

            internal sealed record Foo(string Name, int Count);
            internal sealed record Input(Foo Value);

            internal static class Checks
            {
                internal static bool NameOk(Foo value) => value.Name.Length != 0;
                internal static bool CountOk(Foo value) => value.Count >= 0;
            }

            internal static class Routes
            {
                internal static void CommonRules(Rule<Foo> rule) =>
                    rule.Must(Checks.NameOk, "name must not be empty")
                        .Must(Checks.CountOk, "count must be non-negative");

                internal static void Map()
                {
                    var app = new App();
                    var schema = Schemas.For<Input>().Body(input => input.Value, CommonRules);
                    app.Post("/items", schema, static (context, input) => context.Json(input));
                }
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));
        var generated = run.SourcesWithPrefix("Miya.SchemaBinder.");

        AssertNoErrors(run);
        Assert.Contains("global::Checks.NameOk", generated, StringComparison.Ordinal);
        Assert.Contains("global::Checks.CountOk", generated, StringComparison.Ordinal);
        Assert.Contains("name must not be empty", generated, StringComparison.Ordinal);
        Assert.Contains("count must be non-negative", generated, StringComparison.Ordinal);
        Assert.True(
            generated.IndexOf("global::Checks.NameOk", StringComparison.Ordinal)
                < generated.IndexOf("global::Checks.CountOk", StringComparison.Ordinal));
    }

    [Fact]
    public void Explicit_delegate_and_forwarding_lambda_resolve_a_shared_rule_method()
    {
        const string source = """
            using System;
            using Miya;
            using Miya.Schema;

            internal sealed record ExplicitInput(int Value);
            internal sealed record ForwardedInput(int Value);

            internal static class Routes
            {
                internal static void CommonRules(Rule<int> rule) => rule.Range(1, 10);

                internal static void Map()
                {
                    var app = new App();
                    var explicitSchema = Schemas.For<ExplicitInput>()
                        .Query(input => input.Value, new Action<Rule<int>>(CommonRules));
                    var forwardedSchema = Schemas.For<ForwardedInput>()
                        .Query(input => input.Value, rule => CommonRules(rule));
                    app.Get("/explicit", explicitSchema, static (context, input) => context.Json(input));
                    app.Get("/forwarded", forwardedSchema, static (context, input) => context.Json(input));
                }
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));
        var generated = run.SourcesWithPrefix("Miya.SchemaBinder.");

        AssertNoErrors(run);
        Assert.Equal(2, Count(generated, "must be between 1 and 10"));
    }

    [Fact]
    public void Method_group_rules_are_parsed_for_every_schema_source()
    {
        const string source = """
            using Miya.Schema;

            internal sealed record Input(int Route, int Query, int Body, int Form, string Header);

            internal static class SchemaDefinitions
            {
                internal static void NumberRules(Rule<int> rule) => rule.NonNegative();
                internal static void TextRules(Rule<string> rule) => rule.NotEmpty();

                internal static void Build()
                {
                    _ = Schemas.For<Input>()
                        .Route(input => input.Route, NumberRules)
                        .Query(input => input.Query, NumberRules)
                        .Body(input => input.Body, NumberRules)
                        .Form(input => input.Form, NumberRules)
                        .Header(input => input.Header, "X-Value", TextRules);
                }
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));

        AssertNoErrors(run);
    }

    [Fact]
    public void Multi_statement_rule_method_reports_Miya025()
    {
        const string source = """
            using Miya.Schema;
            internal sealed record Input(string Value);
            internal static class SchemaDefinitions
            {
                internal static void CommonRules(Rule<string> rule)
                {
                    rule.NotEmpty();
                    rule.MaxLength(10);
                }

                internal static void Build() =>
                    _ = Schemas.For<Input>().Query(input => input.Value, CommonRules);
            }
            """;

        AssertInvalidRuleDeclaration(source);
    }

    [Fact]
    public void Instance_rule_method_reports_Miya025()
    {
        const string source = """
            using Miya.Schema;
            internal sealed record Input(string Value);
            internal sealed class RuleSet
            {
                internal void CommonRules(Rule<string> rule) => rule.NotEmpty();
            }

            internal static class SchemaDefinitions
            {
                internal static void Build()
                {
                    var rules = new RuleSet();
                    _ = Schemas.For<Input>().Query(input => input.Value, rules.CommonRules);
                }
            }
            """;

        AssertInvalidRuleDeclaration(source);
    }

    [Fact]
    public void Cross_assembly_rule_method_reports_Miya025()
    {
        const string externalSource = """
            using Miya.Schema;
            namespace External;
            public static class SharedRules
            {
                public static void Apply(Rule<string> rule) => rule.NotEmpty();
            }
            """;
        var externalCompilation = CSharpCompilation.Create(
            "ExternalRules",
            [CSharpSyntaxTree.ParseText(externalSource, GeneratorTestHelper.ParseOptions, "External.cs")],
            GeneratorTestHelper.References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        var emit = externalCompilation.Emit(image);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));

        const string source = """
            using Miya.Schema;
            internal sealed record Input(string Value);
            internal static class SchemaDefinitions
            {
                internal static void Build() =>
                    _ = Schemas.For<Input>().Query(input => input.Value, External.SharedRules.Apply);
            }
            """;
        var compilation = GeneratorTestHelper.CreateCompilation(source)
            .AddReferences(MetadataReference.CreateFromImage(image.ToArray()));
        var run = GeneratorTestHelper.Run(compilation);

        AssertDiagnostic(run, "MIYA025", RuleDeclarationMessage);
    }

    [Fact]
    public void Rule_chain_not_rooted_at_the_parameter_reports_Miya025()
    {
        const string source = """
            using Miya.Schema;
            internal sealed record Input(string Value);
            internal static class SchemaDefinitions
            {
                internal static Rule<string> OtherRule() => throw new System.NotSupportedException();
                internal static void CommonRules(Rule<string> rule) => OtherRule().NotEmpty();
                internal static void Build() =>
                    _ = Schemas.For<Input>().Query(input => input.Value, CommonRules);
            }
            """;

        AssertInvalidRuleDeclaration(source);
    }

    [Fact]
    public void Conditional_inline_rules_report_Miya025_even_with_another_schema_error()
    {
        const string source = """
            using Miya.Schema;
            internal sealed record Input(string Value);
            internal static class SchemaDefinitions
            {
                internal static void Build() =>
                    _ = Schemas.For<Input>().Header(
                        input => input.Value,
                        "",
                        rule =>
                        {
                            if (true)
                            {
                                rule.NotEmpty();
                            }
                        });
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));

        Assert.Contains(run.Result.Diagnostics, diagnostic => diagnostic.Id == "MIYA013");
        AssertDiagnostic(run, "MIYA025", RuleDeclarationMessage);
    }

    [Fact]
    public void Private_predicate_reports_Miya026_without_generated_access_error()
    {
        const string source = """
            using Miya;
            using Miya.Schema;
            internal sealed record Input(string Value);
            internal static class Routes
            {
                private static bool IsValid(string value) => value.Length != 0;
                internal static void CommonRules(Rule<string> rule) =>
                    rule.Must(IsValid, "is invalid");

                internal static void Map()
                {
                    var app = new App();
                    var schema = Schemas.For<Input>().Query(input => input.Value, CommonRules);
                    app.Get("/items", schema, static (context, input) => context.Text(input.Value));
                }
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));
        var diagnostic = Assert.Single(run.Result.Diagnostics, diagnostic => diagnostic.Id == "MIYA026");

        Assert.Contains("Routes.IsValid", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains(
            "must be internal or public because generated code references it",
            diagnostic.GetMessage(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(run.Compilation.GetDiagnostics(), diagnostic => diagnostic.Id == "CS0122");
    }

    private const string RuleDeclarationMessage =
        "Rule declarations must be an inline lambda or a static method containing a single rule chain";

    private static void AssertInvalidRuleDeclaration(string source)
    {
        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));
        AssertDiagnostic(run, "MIYA025", RuleDeclarationMessage);
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

    private static int Count(string text, string value) =>
        text.Split(value, StringSplitOptions.None).Length - 1;
}
