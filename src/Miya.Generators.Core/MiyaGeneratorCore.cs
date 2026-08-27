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

        var orderedModels = BuildJsonModels(analyses, settings, diagnostics);
        var models = new Dictionary<ITypeSymbol, JsonTypeModel>(SymbolEqualityComparer.Default);
        foreach (var model in orderedModels)
        {
            models[model.Type] = model;
        }
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
                RouteAndInterceptorEmitter.EmitInterceptors(analyses, orderedModels)));
        }

        return new GenerationResult(sources.ToImmutable(), diagnostics.ToImmutable());
    }

    internal static ImmutableArray<GeneratedSource> GenerateIncrementalJsonSources(
        ImmutableArray<InvocationAnalysis> analyses,
        GeneratorSettings settings)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var models = BuildJsonModels(analyses, settings, diagnostics);
        var sources = ImmutableArray.CreateBuilder<GeneratedSource>(models.Length);
        foreach (var model in models)
        {
            var emitter = new JsonSourceEmitter(ImmutableArray.Create(model), settings);
            var key = TypeNames.CodecName(model.Type);
            sources.Add(new GeneratedSource(
                "Miya.JsonCodec." + key + ".g.cs",
                emitter.EmitCodecAndRegistrationSource(model)));
        }

        return sources.ToImmutable();
    }

    internal static ImmutableArray<GeneratedSource> GenerateIncrementalRouteSources(
        ImmutableArray<InvocationAnalysis> analyses)
    {
        var sources = ImmutableArray.CreateBuilder<GeneratedSource>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in analyses
                     .Where(static analysis => analysis.Route is not null)
                     .Select(static analysis => analysis.Route!))
        {
            if (!seen.Add(route.Pattern))
            {
                continue;
            }

            var key = RouteAndInterceptorEmitter.RouteFieldName(route.Pattern);
            sources.Add(new GeneratedSource(
                "Miya.RouteTemplate." + key + ".g.cs",
                RouteAndInterceptorEmitter.EmitRouteTemplate(route)));
        }

        return sources.ToImmutable();
    }

    internal static GeneratedSource? GenerateIncrementalInterceptorSource(
        InvocationAnalysis analysis,
        GeneratorSettings settings)
    {
        if (analysis.Route?.InterceptAttribute is not null)
        {
            var key = RouteAndInterceptorEmitter.InterceptorKey(analysis);
            return new GeneratedSource(
                "Miya.Interceptor." + key + ".g.cs",
                RouteAndInterceptorEmitter.EmitInterceptor(analysis, model: null));
        }

        if (!analysis.InterceptJson || analysis.JsonInterceptAttribute is null || analysis.JsonType is null)
        {
            return null;
        }

        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var models = BuildJsonModels(ImmutableArray.Create(analysis), settings, diagnostics);
        var model = models.FirstOrDefault(candidate =>
            SymbolEqualityComparer.Default.Equals(candidate.Type, analysis.JsonType));
        if (model is null)
        {
            return null;
        }

        var interceptorKey = RouteAndInterceptorEmitter.InterceptorKey(analysis);
        return new GeneratedSource(
            "Miya.Interceptor." + interceptorKey + ".g.cs",
            RouteAndInterceptorEmitter.EmitInterceptor(analysis, model));
    }

    internal static ImmutableArray<Diagnostic> GenerateCallSiteDiagnostics(
        InvocationAnalysis analysis,
        GeneratorSettings settings)
    {
        var result = GenerateFromAnalyses(
            ImmutableArray.Create(analysis),
            new GeneratorSettings(settings.Naming, emitInterceptors: false));
        return result.Diagnostics;
    }

    internal static ImmutableArray<Diagnostic> GenerateDuplicateRouteDiagnostics(
        ImmutableArray<InvocationAnalysis> analyses)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        AddDuplicateRouteDiagnostics(analyses, diagnostics);
        return diagnostics.ToImmutable();
    }

    private static ImmutableArray<JsonTypeModel> BuildJsonModels(
        ImmutableArray<InvocationAnalysis> analyses,
        GeneratorSettings settings,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
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

        return models.Values
            .OrderBy(static model => TypeNames.Key(model.Type), StringComparer.Ordinal)
            .ToImmutableArray();
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
        var previous = new List<InvocationAnalysis>();
        foreach (var analysis in analyses
                     .Where(static item => item.Route is not null)
                     .OrderBy(static item => item.Syntax.SyntaxTree.FilePath, StringComparer.Ordinal)
                     .ThenBy(static item => item.Syntax.SpanStart))
        {
            var route = analysis.Route!;
            var block = GetDirectContainingBlock(analysis.Syntax);
            if (!(route.ReceiverSymbol is ILocalSymbol local) || route.Method == "<dynamic>" || block is null)
            {
                continue;
            }

            var duplicate = previous.FirstOrDefault(candidate =>
                candidate.Route is not null
                && IsSameBlock(GetDirectContainingBlock(candidate.Syntax), block)
                && candidate.Route.ReceiverSymbol is ILocalSymbol
                && SymbolEqualityComparer.Default.Equals(candidate.Route.ReceiverSymbol, route.ReceiverSymbol)
                && string.Equals(candidate.Route.Method, route.Method, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.Route.Pattern, route.Pattern, StringComparison.Ordinal));
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
                previous.Add(analysis);
            }
        }
    }

    private static Microsoft.CodeAnalysis.CSharp.Syntax.BlockSyntax? GetDirectContainingBlock(
        Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax invocation)
    {
        var statement = invocation.FirstAncestorOrSelf<Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax>();
        return statement?.Parent as Microsoft.CodeAnalysis.CSharp.Syntax.BlockSyntax;
    }

    private static bool IsSameBlock(
        Microsoft.CodeAnalysis.CSharp.Syntax.BlockSyntax? left,
        Microsoft.CodeAnalysis.CSharp.Syntax.BlockSyntax right)
    {
        return left is not null
            && ReferenceEquals(left.SyntaxTree, right.SyntaxTree)
            && left.Span == right.Span;
    }
}
