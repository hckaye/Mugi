using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mugi.Generators.Core;

internal sealed class InvocationAnalysis
{
    internal InvocationAnalysis(
        InvocationExpressionSyntax syntax,
        ITypeSymbol? jsonType,
        bool interceptJson,
        IMethodSymbol? jsonTargetMethod,
        string? jsonInterceptAttribute,
        RouteCall? route,
        Diagnostic? diagnostic,
        SchemaDefinition? schemaDefinition = null,
        SchemaPartDefinition? schemaPartDefinition = null,
        SchemaEndpointCall? schemaEndpoint = null,
        ImmutableArray<Diagnostic> diagnostics = default)
    {
        Syntax = syntax;
        JsonType = jsonType;
        InterceptJson = interceptJson;
        JsonTargetMethod = jsonTargetMethod;
        JsonInterceptAttribute = jsonInterceptAttribute;
        Route = route;
        Diagnostics = diagnostics.IsDefault
            ? diagnostic is null
                ? ImmutableArray<Diagnostic>.Empty
                : ImmutableArray.Create(diagnostic)
            : diagnostics;
        SchemaDefinition = schemaDefinition;
        SchemaPartDefinition = schemaPartDefinition;
        SchemaEndpoint = schemaEndpoint;
    }

    internal InvocationExpressionSyntax Syntax { get; }

    internal ITypeSymbol? JsonType { get; }

    internal bool InterceptJson { get; }

    internal IMethodSymbol? JsonTargetMethod { get; }

    internal string? JsonInterceptAttribute { get; }

    internal RouteCall? Route { get; }

    internal ImmutableArray<Diagnostic> Diagnostics { get; }

    internal SchemaDefinition? SchemaDefinition { get; }

    internal SchemaPartDefinition? SchemaPartDefinition { get; }

    internal SchemaEndpointCall? SchemaEndpoint { get; }
}

internal enum SchemaFieldSource
{
    Automatic,
    Route,
    Query,
    Body,
    Form,
    Header,
}

internal enum SchemaRuleKind
{
    Optional,
    Default,
    Must,
    Min,
    Max,
    Range,
    Positive,
    NonNegative,
    NotEmpty,
    Length,
    MinLength,
    MaxLength,
    Pattern,
}

internal sealed class SchemaDefinition
{
    internal SchemaDefinition(
        ITypeSymbol inputType,
        ImmutableArray<SchemaFieldDeclaration> fields,
        ImmutableArray<SchemaPartUse> parts,
        ImmutableArray<Diagnostic> diagnostics,
        Location location)
    {
        InputType = inputType;
        Fields = fields;
        Parts = parts;
        Diagnostics = diagnostics;
        Location = location;
    }

    internal ITypeSymbol InputType { get; }

    internal ImmutableArray<SchemaFieldDeclaration> Fields { get; }

    internal ImmutableArray<SchemaPartUse> Parts { get; }

    internal ImmutableArray<Diagnostic> Diagnostics { get; }

    internal Location Location { get; }
}

internal sealed class SchemaPartDefinition
{
    internal SchemaPartDefinition(
        ITypeSymbol partType,
        ImmutableArray<SchemaFieldDeclaration> fields,
        ImmutableArray<Diagnostic> diagnostics,
        Location location)
    {
        PartType = partType;
        Fields = fields;
        Diagnostics = diagnostics;
        Location = location;
    }

    internal ITypeSymbol PartType { get; }

    internal ImmutableArray<SchemaFieldDeclaration> Fields { get; }

    internal ImmutableArray<Diagnostic> Diagnostics { get; }

    internal Location Location { get; }
}

internal sealed class SchemaPartUse
{
    internal SchemaPartUse(ITypeSymbol partType, Location location)
    {
        PartType = partType;
        Location = location;
    }

    internal ITypeSymbol PartType { get; }

    internal Location Location { get; }
}

internal sealed class SchemaFieldDeclaration
{
    internal SchemaFieldDeclaration(
        IPropertySymbol property,
        SchemaFieldSource source,
        string? headerName,
        ImmutableArray<SchemaRuleDeclaration> rules,
        Location location)
    {
        Property = property;
        Source = source;
        HeaderName = headerName;
        Rules = rules;
        Location = location;
    }

    internal IPropertySymbol Property { get; }

    internal SchemaFieldSource Source { get; }

    internal string? HeaderName { get; }

    internal ImmutableArray<SchemaRuleDeclaration> Rules { get; }

    internal Location Location { get; }
}

internal sealed class SchemaRuleDeclaration
{
    internal SchemaRuleDeclaration(
        SchemaRuleKind kind,
        ImmutableArray<object?> values,
        string? predicate,
        string? message,
        Location location)
    {
        Kind = kind;
        Values = values;
        Predicate = predicate;
        Message = message;
        Location = location;
    }

    internal SchemaRuleKind Kind { get; }

    internal ImmutableArray<object?> Values { get; }

    internal string? Predicate { get; }

    internal string? Message { get; }

    internal Location Location { get; }
}

internal sealed class SchemaEndpointCall
{
    internal SchemaEndpointCall(
        string pattern,
        string method,
        ITypeSymbol inputType,
        RoutePatternSpec template,
        Location location)
    {
        Pattern = pattern;
        Method = method;
        InputType = inputType;
        Template = template;
        Location = location;
    }

    internal string Pattern { get; }

    internal string Method { get; }

    internal ITypeSymbol InputType { get; }

    internal RoutePatternSpec Template { get; }

    internal Location Location { get; }
}

internal sealed class RouteCall
{
    internal RouteCall(
        string pattern,
        string method,
        IMethodSymbol targetMethod,
        RoutePatternSpec template,
        ISymbol? receiverSymbol,
        string? interceptAttribute)
    {
        Pattern = pattern;
        Method = method;
        TargetMethod = targetMethod;
        Template = template;
        ReceiverSymbol = receiverSymbol;
        InterceptAttribute = interceptAttribute;
    }

    internal string Pattern { get; }

    internal string Method { get; }

    internal IMethodSymbol TargetMethod { get; }

    internal RoutePatternSpec Template { get; }

    internal ISymbol? ReceiverSymbol { get; }

    internal string? InterceptAttribute { get; }
}

internal sealed class RoutePatternSpec
{
    internal RoutePatternSpec(
        ImmutableArray<RouteSegmentSpec> segments,
        ImmutableArray<string> parameterNames)
    {
        Segments = segments;
        ParameterNames = parameterNames;
    }

    internal ImmutableArray<RouteSegmentSpec> Segments { get; }

    internal ImmutableArray<string> ParameterNames { get; }
}

internal sealed class RouteSegmentSpec
{
    internal RouteSegmentSpec(byte kind, string value, int parameterIndex)
    {
        Kind = kind;
        Value = value;
        ParameterIndex = parameterIndex;
    }

    internal byte Kind { get; }

    internal string Value { get; }

    internal int ParameterIndex { get; }
}
