using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Miya.Generators.Core;

namespace Miya.Generators;

[Generator(LanguageNames.CSharp)]
public sealed class MiyaIncrementalGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var settings = context.AnalyzerConfigOptionsProvider
            .Select(static (options, _) => ReadSettings(options.GlobalOptions));

        var analyses = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is InvocationExpressionSyntax,
                static (syntaxContext, cancellationToken) => InvocationAnalyzer.Analyze(
                    syntaxContext.SemanticModel,
                    (InvocationExpressionSyntax)syntaxContext.Node,
                    includeInterceptLocation: true,
                    cancellationToken))
            .Where(static analysis => analysis is not null)
            .Select(static (analysis, _) => analysis!)
            .WithTrackingName("MiyaCallSites");

        var jsonTypes = analyses
            .Where(static analysis => analysis.JsonType is not null)
            .Select(static (analysis, _) => new JsonInvocationCandidate(analysis))
            .WithComparer(JsonInvocationCandidateComparer.Instance)
            .WithTrackingName("MiyaJsonTypes");

        var jsonCodecInputs = jsonTypes.Collect().Combine(settings)
            .WithTrackingName("MiyaJsonCodecInputs");
        context.RegisterSourceOutput(
            jsonCodecInputs,
            static (productionContext, pair) =>
            {
                var jsonAnalyses = pair.Left.Select(static candidate => candidate.Analysis).ToImmutableArray();
                var result = MiyaGeneratorCore.GenerateFromAnalyses(
                    jsonAnalyses,
                    new GeneratorSettings(pair.Right.Naming, emitInterceptors: false));
                AddSources(productionContext, result, "Miya.Json");
            });

        var routes = analyses
            .Where(static analysis => analysis.Route is not null)
            .WithTrackingName("MiyaRoutes");
        context.RegisterSourceOutput(
            routes.Collect(),
            static (productionContext, routeAnalyses) =>
            {
                var result = MiyaGeneratorCore.GenerateFromAnalyses(
                    routeAnalyses,
                    new GeneratorSettings(emitInterceptors: false));
                AddSources(productionContext, result, "Miya.RouteTemplates");
            });

        context.RegisterSourceOutput(
            analyses.Collect().Combine(settings),
            static (productionContext, pair) =>
            {
                var result = MiyaGeneratorCore.GenerateFromAnalyses(pair.Left, pair.Right);
                AddSources(productionContext, result, "Miya.Interceptors");
                foreach (var diagnostic in result.Diagnostics)
                {
                    productionContext.ReportDiagnostic(diagnostic);
                }
            });
    }

    private static GeneratorSettings ReadSettings(AnalyzerConfigOptions options)
    {
        if (options.TryGetValue("build_property.MiyaJsonNaming", out var naming)
            && string.Equals(naming, "PascalCase", StringComparison.OrdinalIgnoreCase))
        {
            return new GeneratorSettings(MiyaJsonNaming.PascalCase, emitInterceptors: true);
        }

        return new GeneratorSettings(MiyaJsonNaming.CamelCase, emitInterceptors: true);
    }

    private static void AddSources(
        SourceProductionContext context,
        GenerationResult result,
        string hintPrefix)
    {
        foreach (var source in result.Sources)
        {
            if (source.HintName.StartsWith(hintPrefix, StringComparison.Ordinal))
            {
                context.AddSource(source.HintName, SourceText.From(source.Source, System.Text.Encoding.UTF8));
            }
        }
    }
}
