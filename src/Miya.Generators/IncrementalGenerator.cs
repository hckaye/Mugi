using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Miya.Generators.Core;

namespace Miya.Generators;

[Generator(LanguageNames.CSharp)]
public sealed class IncrementalGenerator : IIncrementalGenerator
{
    private static readonly HashSet<string> CandidateInvocationNames = new(StringComparer.Ordinal)
    {
        "All",
        "Delete",
        "Deserialize",
        "For",
        "Form",
        "Get",
        "Head",
        "Include",
        "Json",
        "On",
        "Options",
        "Param",
        "Part",
        "Patch",
        "Post",
        "Put",
        "Route",
        "Serialize",
        "Use",
    };

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

        var openApiDefaults = context.AnalyzerConfigOptionsProvider
            .Select(static (options, _) => ReadOpenApiDefaults(options.GlobalOptions));
        var openApiFiles = context.AdditionalTextsProvider
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select(static (pair, cancellationToken) => ReadOpenApiFile(
                pair.Left,
                pair.Right.GetOptions(pair.Left),
                cancellationToken))
            .Where(static file => file is not null)
            .Select(static (file, _) => file!)
            .WithTrackingName("MiyaOpenApiFiles");
        var openApiImportFiles = openApiFiles
            .Where(static file => file.ServerImport)
            .Combine(openApiDefaults)
            .Select(static (pair, _) => new OpenApiImportInput(
                pair.Left.Path,
                pair.Left.Content,
                pair.Left.TargetNamespace ?? pair.Right.RootNamespace,
                pair.Right.Naming,
                serverImport: pair.Left.ServerImport))
            .WithComparer(OpenApiImportInputComparer.Instance)
            .WithTrackingName("MiyaOpenApiInputs");
        var openApiGeneration = openApiImportFiles
            .Select(static (file, cancellationToken) =>
                OpenApiImportGenerator.Generate(file, cancellationToken))
            .WithTrackingName("MiyaOpenApiGeneration");
        var openApiSources = openApiGeneration
            .SelectMany(static (result, _) => result.Sources)
            .WithComparer(GeneratedSourceComparer.Instance)
            .WithTrackingName("MiyaOpenApiSources");
        context.RegisterSourceOutput(
            openApiSources,
            static (productionContext, source) => AddSource(productionContext, source));
        context.RegisterSourceOutput(
            openApiGeneration,
            static (productionContext, result) =>
            {
                foreach (var diagnostic in result.Diagnostics)
                {
                    productionContext.ReportDiagnostic(diagnostic);
                }
            });

        var openApiClientFiles = context.AdditionalTextsProvider
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select(static (pair, cancellationToken) => ReadOpenApiFile(
                pair.Left,
                pair.Right.GetOptions(pair.Left),
                cancellationToken))
            .Where(static file => file is not null && file.ClientGeneration)
            .Select(static (file, _) => file!)
            .Combine(openApiDefaults)
            .Select(static (pair, _) => new OpenApiImportInput(
                pair.Left.Path,
                pair.Left.Content,
                pair.Left.TargetNamespace ?? pair.Right.RootNamespace,
                pair.Right.Naming,
                pair.Left.ClientName,
                serverImport: pair.Left.ServerImport))
            .WithComparer(OpenApiImportInputComparer.Instance)
            .WithTrackingName("MiyaOpenApiClientInputs");
        var openApiClientGeneration = openApiClientFiles
            .Select(static (file, cancellationToken) =>
                new OpenApiClientGeneration(
                    file,
                    OpenApiClientGenerator.Generate(file, cancellationToken, emitApiException: false)))
            .WithTrackingName("MiyaOpenApiClientGeneration");
        var openApiClientSources = openApiClientGeneration
            .SelectMany(static (generation, _) => generation.Result.Sources)
            .WithComparer(GeneratedSourceComparer.Instance)
            .WithTrackingName("MiyaOpenApiClientSources");
        context.RegisterSourceOutput(
            openApiClientSources,
            static (productionContext, source) => AddSource(productionContext, source));
        context.RegisterSourceOutput(
            openApiClientGeneration,
            static (productionContext, generation) =>
            {
                foreach (var diagnostic in generation.Result.Diagnostics)
                {
                    productionContext.ReportDiagnostic(diagnostic);
                }
            });
        var openApiClientExceptions = openApiClientGeneration
            .Collect()
            .SelectMany(static (files, _) =>
            {
                var namespaces = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                var sources = ImmutableArray.CreateBuilder<GeneratedSource>();
                foreach (var generation in files)
                {
                    if (!OpenApiImportGenerator.TryRenderNamespace(
                            generation.Input.TargetNamespace,
                            out var targetNamespace)
                        || !namespaces.Add(targetNamespace))
                    {
                        continue;
                    }

                    sources.Add(OpenApiClientGenerator.EmitApiExceptionSource(targetNamespace));
                }

                return sources.ToImmutable();
            })
            .WithComparer(GeneratedSourceComparer.Instance)
            .WithTrackingName("MiyaOpenApiClientExceptions");
        context.RegisterSourceOutput(
            openApiClientExceptions,
            static (productionContext, source) => AddSource(productionContext, source));

        var analyses = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => IsCandidateInvocation(node),
                static (syntaxContext, cancellationToken) => InvocationAnalyzer.Analyze(
                    syntaxContext.SemanticModel,
                    (InvocationExpressionSyntax)syntaxContext.Node,
                    includeInterceptLocation: true,
                    cancellationToken))
            .Where(static analysis => analysis is not null)
            .Select(static (analysis, _) => analysis!)
            .WithTrackingName("MiyaCallSites");

        var jsonTypes = analyses
            .Where(static analysis => analysis.JsonType is not null || analysis.SchemaDefinition is not null)
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
                return GeneratorCore.GenerateIncrementalJsonSources(jsonAnalyses, pair.Right);
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
                GeneratorCore.GenerateIncrementalRouteSources(routeAnalyses))
            .WithComparer(GeneratedSourceComparer.Instance)
            .WithTrackingName("MiyaRouteSources");
        context.RegisterSourceOutput(
            routeSources,
            static (productionContext, source) => AddSource(productionContext, source));

        var schemaGeneration = analyses.Collect().Combine(settings)
            .Select(static (pair, _) =>
                GeneratorCore.GenerateIncrementalSchemaSources(pair.Left, pair.Right))
            .WithTrackingName("MiyaSchemaGeneration");
        var schemaSources = schemaGeneration
            .SelectMany(static (result, _) => result.Sources)
            .WithComparer(GeneratedSourceComparer.Instance)
            .WithTrackingName("MiyaSchemaSources");
        context.RegisterSourceOutput(
            schemaSources,
            static (productionContext, source) => AddSource(productionContext, source));
        context.RegisterSourceOutput(
            schemaGeneration,
            static (productionContext, result) =>
            {
                foreach (var diagnostic in result.Diagnostics)
                {
                    productionContext.ReportDiagnostic(diagnostic);
                }
            });

        var interceptorSources = analyses.Combine(settings)
            .Select(static (pair, _) =>
                GeneratorCore.GenerateIncrementalInterceptorSource(pair.Left, pair.Right))
            .Where(static source => source is not null)
            .Select(static (source, _) => source!)
            .WithComparer(GeneratedSourceComparer.Instance)
            .WithTrackingName("MiyaInterceptorSources");
        context.RegisterSourceOutput(
            interceptorSources,
            static (productionContext, source) => AddSource(productionContext, source));

        var callSiteDiagnostics = analyses.Combine(settings)
            .Select(static (pair, _) =>
                GeneratorCore.GenerateCallSiteDiagnostics(pair.Left, pair.Right))
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
                GeneratorCore.GenerateDuplicateRouteDiagnostics(routeAnalyses))
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

    private static bool IsCandidateInvocation(SyntaxNode node)
    {
        if (node is not InvocationExpressionSyntax invocation)
        {
            return false;
        }

        SimpleNameSyntax? name = invocation.Expression switch
        {
            IdentifierNameSyntax identifier => identifier,
            GenericNameSyntax generic => generic,
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name,
            MemberBindingExpressionSyntax memberBinding => memberBinding.Name,
            _ => null,
        };
        return name is not null && CandidateInvocationNames.Contains(name.Identifier.ValueText);
    }

    private static GeneratorSettings ReadSettings(AnalyzerConfigOptions options)
    {
        if (options.TryGetValue("build_property.MiyaJsonNaming", out var naming)
            && string.Equals(naming, "PascalCase", StringComparison.OrdinalIgnoreCase))
        {
            return new GeneratorSettings(JsonNaming.PascalCase, emitInterceptors: true);
        }

        return new GeneratorSettings(JsonNaming.CamelCase, emitInterceptors: true);
    }

    private static OpenApiDefaults ReadOpenApiDefaults(AnalyzerConfigOptions options)
    {
        var naming = JsonNaming.CamelCase;
        if (options.TryGetValue("build_property.MiyaJsonNaming", out var configuredNaming)
            && string.Equals(configuredNaming, "PascalCase", StringComparison.OrdinalIgnoreCase))
        {
            naming = JsonNaming.PascalCase;
        }

        var rootNamespace = "OpenApi";
        if (options.TryGetValue("build_property.RootNamespace", out var configuredNamespace)
            && !string.IsNullOrWhiteSpace(configuredNamespace))
        {
            rootNamespace = configuredNamespace;
        }

        return new OpenApiDefaults(rootNamespace, naming);
    }

    private static OpenApiFile? ReadOpenApiFile(
        AdditionalText text,
        AnalyzerConfigOptions options,
        System.Threading.CancellationToken cancellationToken)
    {
        if (!options.TryGetValue("build_metadata.AdditionalFiles.MiyaOpenApi", out var enabled)
            || !string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))
        {
            enabled = string.Empty;
        }

        var clientEnabled = options.TryGetValue(
                "build_metadata.AdditionalFiles.MiyaOpenApiClient",
                out var configuredClient)
            && string.Equals(configuredClient, "true", StringComparison.OrdinalIgnoreCase);
        var serverEnabled = string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase);
        if (!serverEnabled && !clientEnabled)
        {
            return null;
        }

        string? targetNamespace = null;
        if (options.TryGetValue(
                "build_metadata.AdditionalFiles.MiyaOpenApiNamespace",
                out var configuredTargetNamespace)
            && !string.IsNullOrWhiteSpace(configuredTargetNamespace))
        {
            targetNamespace = configuredTargetNamespace;
        }

        var content = text.GetText(cancellationToken)?.ToString() ?? string.Empty;
        string? clientName = null;
        if (options.TryGetValue(
                "build_metadata.AdditionalFiles.MiyaOpenApiClientName",
                out var configuredClientName)
            && !string.IsNullOrWhiteSpace(configuredClientName))
        {
            clientName = configuredClientName;
        }

        return new OpenApiFile(text.Path, content, targetNamespace, serverEnabled, clientEnabled, clientName);
    }

    private static void AddSource(SourceProductionContext context, GeneratedSource source)
    {
        context.AddSource(source.HintName, SourceText.From(source.Source, System.Text.Encoding.UTF8));
    }

    private sealed class OpenApiFile
    {
        internal OpenApiFile(
            string path,
            string content,
            string? targetNamespace,
            bool serverImport,
            bool clientGeneration,
            string? clientName)
        {
            Path = path;
            Content = content;
            TargetNamespace = targetNamespace;
            ServerImport = serverImport;
            ClientGeneration = clientGeneration;
            ClientName = clientName;
        }

        internal string Path { get; }

        internal string Content { get; }

        internal string? TargetNamespace { get; }

        internal bool ServerImport { get; }

        internal bool ClientGeneration { get; }

        internal string? ClientName { get; }
    }

    private sealed class OpenApiClientGeneration
    {
        internal OpenApiClientGeneration(OpenApiImportInput input, GenerationResult result)
        {
            Input = input;
            Result = result;
        }

        internal OpenApiImportInput Input { get; }

        internal GenerationResult Result { get; }
    }

    private sealed class OpenApiDefaults
    {
        internal OpenApiDefaults(string rootNamespace, JsonNaming naming)
        {
            RootNamespace = rootNamespace;
            Naming = naming;
        }

        internal string RootNamespace { get; }

        internal JsonNaming Naming { get; }
    }
}
