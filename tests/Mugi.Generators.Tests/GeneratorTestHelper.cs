using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Mugi.Generators;

namespace Mugi.Generators.Tests;

internal static class GeneratorTestHelper
{
    internal static readonly CSharpParseOptions ParseOptions = new CSharpParseOptions(LanguageVersion.Latest)
        .WithFeatures([new KeyValuePair<string, string>("InterceptorsNamespaces", "Mugi.Generated")]);

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
        bool trackSteps = false,
        IEnumerable<AdditionalText>? additionalTexts = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? additionalFileMetadata = null,
        string rootNamespace = "GeneratedTests")
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new IncrementalGenerator().AsSourceGenerator()],
            additionalTexts: additionalTexts?.ToImmutableArray() ?? ImmutableArray<AdditionalText>.Empty,
            parseOptions: ParseOptions,
            optionsProvider: new TestAnalyzerConfigOptionsProvider(
                naming,
                rootNamespace,
                additionalFileMetadata),
            driverOptions: new GeneratorDriverOptions(
                IncrementalGeneratorOutputKind.None,
                trackIncrementalGeneratorSteps: trackSteps));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        return new GeneratorRun(driver, (CSharpCompilation)output, diagnostics, driver.GetRunResult());
    }

    internal static AdditionalText AdditionalText(string path, string content) =>
        new TestAdditionalText(path, content);

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
                typeof(Mugi.App).Assembly.Location,
                typeof(Mugi.Json.Json).Assembly.Location,
                typeof(Mugi.Schema.Schemas).Assembly.Location,
            ])
            .Distinct(StringComparer.Ordinal);
        return paths.Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToImmutableArray();
    }

    private sealed class TestAnalyzerConfigOptionsProvider : AnalyzerConfigOptionsProvider
    {
        private readonly AnalyzerConfigOptions _global;
        private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _additionalFileMetadata;

        internal TestAnalyzerConfigOptionsProvider(
            string naming,
            string rootNamespace,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? additionalFileMetadata)
        {
            _global = new TestAnalyzerConfigOptions(naming, rootNamespace);
            _additionalFileMetadata = additionalFileMetadata
                ?? new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        }

        public override AnalyzerConfigOptions GlobalOptions => _global;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => EmptyAnalyzerConfigOptions.Instance;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) =>
            _additionalFileMetadata.TryGetValue(textFile.Path, out var metadata)
                ? new AdditionalFileAnalyzerConfigOptions(metadata)
                : EmptyAnalyzerConfigOptions.Instance;
    }

    private sealed class TestAnalyzerConfigOptions : AnalyzerConfigOptions
    {
        private readonly string _naming;
        private readonly string _rootNamespace;

        internal TestAnalyzerConfigOptions(string naming, string rootNamespace)
        {
            _naming = naming;
            _rootNamespace = rootNamespace;
        }

        public override bool TryGetValue(string key, out string value)
        {
            if (key == "build_property.MugiJsonNaming")
            {
                value = _naming;
                return true;
            }

            if (key == "build_property.RootNamespace")
            {
                value = _rootNamespace;
                return true;
            }

            value = string.Empty;
            return false;
        }
    }

    private sealed class AdditionalFileAnalyzerConfigOptions : AnalyzerConfigOptions
    {
        private readonly IReadOnlyDictionary<string, string> _metadata;

        internal AdditionalFileAnalyzerConfigOptions(IReadOnlyDictionary<string, string> metadata)
        {
            _metadata = metadata;
        }

        public override bool TryGetValue(string key, out string value)
        {
            const string prefix = "build_metadata.AdditionalFiles.";
            if (key.StartsWith(prefix, StringComparison.Ordinal)
                && _metadata.TryGetValue(key.Substring(prefix.Length), out var configuredValue))
            {
                value = configuredValue;
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

    private sealed class TestAdditionalText : AdditionalText
    {
        private readonly Microsoft.CodeAnalysis.Text.SourceText _text;

        internal TestAdditionalText(string path, string content)
        {
            Path = path;
            _text = Microsoft.CodeAnalysis.Text.SourceText.From(content);
        }

        public override string Path { get; }

        public override Microsoft.CodeAnalysis.Text.SourceText GetText(
            CancellationToken cancellationToken = default) => _text;
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
