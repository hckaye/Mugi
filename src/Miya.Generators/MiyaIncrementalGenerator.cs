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
        context.RegisterPostInitializationOutput(static productionContext =>
            productionContext.AddSource(
                "Miya.InterceptsLocationAttribute.g.cs",
                SourceText.From(
                    RouteAndInterceptorEmitter.EmitInterceptsLocationAttribute(),
                    System.Text.Encoding.UTF8)));

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
        var jsonSources = jsonCodecInputs
            .SelectMany(static (pair, _) =>
            {
                var jsonAnalyses = pair.Left
                    .Select(static candidate => candidate.Analysis)
                    .ToImmutableArray();
                return MiyaGeneratorCore.GenerateIncrementalJsonSources(jsonAnalyses, pair.Right);
            })
            .WithComparer(GeneratedSourceComparer.Instance)
            .WithTrackingName("MiyaJsonSources");
        context.RegisterSourceOutput(
            jsonSources,
            static (productionContext, source) => AddSource(productionContext, source));

        var routes = analyses
            .Where(static analysis => analysis.Route is not null)
            .WithTrackingName("MiyaRoutes");
        var routeSources = routes.Collect()
            .SelectMany(static (routeAnalyses, _) =>
                MiyaGeneratorCore.GenerateIncrementalRouteSources(routeAnalyses))
            .WithComparer(GeneratedSourceComparer.Instance)
            .WithTrackingName("MiyaRouteSources");
        context.RegisterSourceOutput(
            routeSources,
            static (productionContext, source) => AddSource(productionContext, source));

        var interceptorSources = analyses.Combine(settings)
            .Select(static (pair, _) =>
                MiyaGeneratorCore.GenerateIncrementalInterceptorSource(pair.Left, pair.Right))
            .Where(static source => source is not null)
            .Select(static (source, _) => source!)
            .WithComparer(GeneratedSourceComparer.Instance)
            .WithTrackingName("MiyaInterceptorSources");
        context.RegisterSourceOutput(
            interceptorSources,
            static (productionContext, source) => AddSource(productionContext, source));

        var callSiteDiagnostics = analyses.Combine(settings)
            .Select(static (pair, _) =>
                MiyaGeneratorCore.GenerateCallSiteDiagnostics(pair.Left, pair.Right))
            .WithTrackingName("MiyaCallSiteDiagnostics");
        context.RegisterSourceOutput(
            callSiteDiagnostics,
            static (productionContext, diagnostics) =>
            {
                foreach (var diagnostic in diagnostics)
                {
                    productionContext.ReportDiagnostic(diagnostic);
                }
            });

        var duplicateRouteDiagnostics = routes.Collect()
            .Select(static (routeAnalyses, _) =>
                MiyaGeneratorCore.GenerateDuplicateRouteDiagnostics(routeAnalyses))
            .WithTrackingName("MiyaDuplicateRouteDiagnostics");
        context.RegisterSourceOutput(
            duplicateRouteDiagnostics,
            static (productionContext, pair) =>
            {
                foreach (var diagnostic in pair)
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

    private static void AddSource(SourceProductionContext context, GeneratedSource source)
    {
        context.AddSource(source.HintName, SourceText.From(source.Source, System.Text.Encoding.UTF8));
    }
}
