using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Miya.Generators.Core;

public static class MiyaGeneratorCore
{
    public static GenerationResult Generate(
        Compilation compilation,
        GeneratorSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (compilation is null)
        {
            throw new ArgumentNullException(nameof(compilation));
        }

        if (settings is null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        var analyses = ImmutableArray.CreateBuilder<InvocationAnalysis>();
        foreach (var syntaxTree in compilation.SyntaxTrees.OrderBy(static tree => tree.FilePath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = syntaxTree.GetRoot(cancellationToken);
            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var analysis = InvocationAnalyzer.Analyze(
                    semanticModel,
                    invocation,
                    settings.EmitInterceptors,
                    cancellationToken);
                if (analysis is not null)
                {
                    analyses.Add(analysis);
                }
            }
        }

        return GenerateFromAnalyses(analyses.ToImmutable(), settings);
    }

    internal static GenerationResult GenerateFromAnalyses(
        ImmutableArray<InvocationAnalysis> analyses,
        GeneratorSettings settings)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        foreach (var analysis in analyses)
        {
            if (analysis.Diagnostic is not null)
            {
                diagnostics.Add(analysis.Diagnostic);
            }
        }

        AddDuplicateRouteDiagnostics(analyses, diagnostics);

        var models = new Dictionary<ITypeSymbol, JsonTypeModel>(SymbolEqualityComparer.Default);
        foreach (var analysis in analyses)
        {
            if (analysis.JsonType is null)
            {
                continue;
            }

            if (!JsonTypeGraphBuilder.TryBuild(analysis.JsonType, out var graph, out var error))
            {
                diagnostics.Add(Diagnostic.Create(
                    DiagnosticCatalog.UnsupportedJsonType,
                    analysis.Syntax.GetLocation(),
                    analysis.JsonType.ToDisplayString(),
                    error));
                continue;
            }

            if (!ValidateJsonNames(graph!, settings.Naming, out error))
            {
                diagnostics.Add(Diagnostic.Create(
                    DiagnosticCatalog.UnsupportedJsonType,
                    analysis.Syntax.GetLocation(),
                    analysis.JsonType.ToDisplayString(),
                    error));
                continue;
            }

            foreach (var model in graph!.Models)
            {
                models[model.Type] = model;
            }
        }

        var orderedModels = models.Values
            .OrderBy(static model => TypeNames.Key(model.Type), StringComparer.Ordinal)
            .ToImmutableArray();
        var routes = analyses
            .Where(static analysis => analysis.Route is not null)
            .Select(static analysis => analysis.Route!)
            .ToImmutableArray();

        var sources = ImmutableArray.CreateBuilder<GeneratedSource>();
        if (orderedModels.Length != 0)
        {
            var jsonEmitter = new JsonSourceEmitter(orderedModels, settings);
            sources.Add(new GeneratedSource("Miya.JsonCodecs.g.cs", jsonEmitter.EmitCodecs()));
            sources.Add(new GeneratedSource("Miya.JsonRegistration.g.cs", jsonEmitter.EmitRegistration()));
        }

        if (routes.Length != 0)
        {
            sources.Add(new GeneratedSource(
                "Miya.RouteTemplates.g.cs",
                RouteAndInterceptorEmitter.EmitRouteTemplates(routes)));
        }

        if (settings.EmitInterceptors && HasInterceptor(analyses, models))
        {
            sources.Add(new GeneratedSource(
                "Miya.Interceptors.g.cs",
                RouteAndInterceptorEmitter.EmitInterceptors(analyses, orderedModels, routes)));
        }

        return new GenerationResult(sources.ToImmutable(), diagnostics.ToImmutable());
    }

    private static bool HasInterceptor(
        ImmutableArray<InvocationAnalysis> analyses,
        Dictionary<ITypeSymbol, JsonTypeModel> models)
    {
        foreach (var analysis in analyses)
        {
            if (analysis.Route?.InterceptAttribute is not null)
            {
                return true;
            }

            if (analysis.InterceptJson && analysis.JsonInterceptAttribute is not null
                && analysis.JsonType is not null && models.ContainsKey(analysis.JsonType))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ValidateJsonNames(
        JsonTypeGraph graph,
        MiyaJsonNaming naming,
        out string? error)
    {
        foreach (var model in graph.Models)
        {
            if (model.Kind != JsonTypeKind.Object)
            {
                continue;
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in model.Properties)
            {
                var name = GeneratedNaming.JsonPropertyName(property.Property.Name, naming);
                if (!names.Add(name))
                {
                    error = "the naming policy maps more than one property to '" + name + "'";
                    return false;
                }
            }
        }

        error = null;
        return true;
    }

    private static void AddDuplicateRouteDiagnostics(
        ImmutableArray<InvocationAnalysis> analyses,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var previous = new List<RouteCall>();
        foreach (var analysis in analyses
                     .Where(static item => item.Route is not null)
                     .OrderBy(static item => item.Syntax.SyntaxTree.FilePath, StringComparer.Ordinal)
                     .ThenBy(static item => item.Syntax.SpanStart))
        {
            var route = analysis.Route!;
            if (!(route.ReceiverSymbol is ILocalSymbol local) || route.Method == "<dynamic>")
            {
                continue;
            }

            var duplicate = previous.FirstOrDefault(candidate =>
                candidate.ReceiverSymbol is ILocalSymbol
                && SymbolEqualityComparer.Default.Equals(candidate.ReceiverSymbol, route.ReceiverSymbol)
                && string.Equals(candidate.Method, route.Method, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.Pattern, route.Pattern, StringComparison.Ordinal));
            if (duplicate is not null)
            {
                diagnostics.Add(Diagnostic.Create(
                    DiagnosticCatalog.DuplicateRoute,
                    analysis.Syntax.GetLocation(),
                    route.Method,
                    route.Pattern,
                    local.Name));
            }
            else
            {
                previous.Add(route);
            }
        }
    }
}
