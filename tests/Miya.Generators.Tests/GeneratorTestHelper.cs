using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Miya.Generators;

namespace Miya.Generators.Tests;

internal static class GeneratorTestHelper
{
    internal static readonly CSharpParseOptions ParseOptions = new CSharpParseOptions(LanguageVersion.Latest)
        .WithFeatures([new KeyValuePair<string, string>("InterceptorsNamespaces", "Miya.Generated")]);

    internal static CSharpCompilation CreateCompilation(string source, string path = "Test.cs")
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, ParseOptions, path);
        return CSharpCompilation.Create(
            "GeneratedTests_" + Guid.NewGuid().ToString("N"),
            [syntaxTree],
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable,
                optimizationLevel: OptimizationLevel.Release,
                warningLevel: 9999));
    }

    internal static GeneratorRun Run(
        CSharpCompilation compilation,
        string naming = "camelCase",
        bool trackSteps = false)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new IncrementalGenerator().AsSourceGenerator()],
            parseOptions: ParseOptions,
            optionsProvider: new TestAnalyzerConfigOptionsProvider(naming),
            driverOptions: new GeneratorDriverOptions(
                IncrementalGeneratorOutputKind.None,
                trackIncrementalGeneratorSteps: trackSteps));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        return new GeneratorRun(driver, (CSharpCompilation)output, diagnostics, driver.GetRunResult());
    }

    internal static Assembly EmitAndLoad(CSharpCompilation compilation)
    {
        using var pe = new MemoryStream();
        var result = compilation.Emit(pe);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        pe.Position = 0;
        return AssemblyLoadContext.Default.LoadFromStream(pe);
    }

    internal static ImmutableArray<MetadataReference> References { get; } = CreateReferences();

    private static ImmutableArray<MetadataReference> CreateReferences()
    {
        var paths = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(
            [
                typeof(Miya.App).Assembly.Location,
                typeof(Miya.Json.Json).Assembly.Location,
                typeof(Miya.Schema.Schemas).Assembly.Location,
            ])
            .Distinct(StringComparer.Ordinal);
        return paths.Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToImmutableArray();
    }

    private sealed class TestAnalyzerConfigOptionsProvider : AnalyzerConfigOptionsProvider
    {
        private readonly AnalyzerConfigOptions _global;

        internal TestAnalyzerConfigOptionsProvider(string naming)
        {
            _global = new TestAnalyzerConfigOptions(naming);
        }

        public override AnalyzerConfigOptions GlobalOptions => _global;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => EmptyAnalyzerConfigOptions.Instance;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => EmptyAnalyzerConfigOptions.Instance;
    }

    private sealed class TestAnalyzerConfigOptions : AnalyzerConfigOptions
    {
        private readonly string _naming;

        internal TestAnalyzerConfigOptions(string naming)
        {
            _naming = naming;
        }

        public override bool TryGetValue(string key, out string value)
        {
            if (key == "build_property.MiyaJsonNaming")
            {
                value = _naming;
                return true;
            }

            value = string.Empty;
            return false;
        }
    }

    private sealed class EmptyAnalyzerConfigOptions : AnalyzerConfigOptions
    {
        internal static readonly EmptyAnalyzerConfigOptions Instance = new();

        public override bool TryGetValue(string key, out string value)
        {
            value = string.Empty;
            return false;
        }
    }
}

internal sealed class GeneratorRun
{
    internal GeneratorRun(
        GeneratorDriver driver,
        CSharpCompilation compilation,
        ImmutableArray<Diagnostic> driverDiagnostics,
        GeneratorDriverRunResult result)
    {
        Driver = driver;
        Compilation = compilation;
        DriverDiagnostics = driverDiagnostics;
        Result = result;
    }

    internal GeneratorDriver Driver { get; }

    internal CSharpCompilation Compilation { get; }

    internal ImmutableArray<Diagnostic> DriverDiagnostics { get; }

    internal GeneratorDriverRunResult Result { get; }

    internal string Source(string hintName) => Result.Results
        .SelectMany(static generatorResult => generatorResult.GeneratedSources)
        .Single(source => source.HintName == hintName)
        .SourceText
        .ToString();

    internal string SourcesWithPrefix(string hintPrefix) => string.Join(
        Environment.NewLine,
        Result.Results
            .SelectMany(static generatorResult => generatorResult.GeneratedSources)
            .Where(source => source.HintName.StartsWith(hintPrefix, StringComparison.Ordinal))
            .OrderBy(static source => source.HintName, StringComparer.Ordinal)
            .Select(static source => source.SourceText.ToString()));
}
