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
}
