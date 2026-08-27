using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Miya.Generators.Core;

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
        SchemaEndpointCall? schemaEndpoint = null)
    {
        Syntax = syntax;
        JsonType = jsonType;
        InterceptJson = interceptJson;
        JsonTargetMethod = jsonTargetMethod;
        JsonInterceptAttribute = jsonInterceptAttribute;
        Route = route;
        Diagnostic = diagnostic;
        SchemaDefinition = schemaDefinition;
        SchemaEndpoint = schemaEndpoint;
    }

    internal InvocationExpressionSyntax Syntax { get; }

    internal ITypeSymbol? JsonType { get; }

    internal bool InterceptJson { get; }

    internal IMethodSymbol? JsonTargetMethod { get; }

    internal string? JsonInterceptAttribute { get; }

    internal RouteCall? Route { get; }

    internal Diagnostic? Diagnostic { get; }

    internal SchemaDefinition? SchemaDefinition { get; }

    internal SchemaEndpointCall? SchemaEndpoint { get; }
}

internal enum SchemaFieldSource
{
    Automatic,
    Route,
    Query,
    Body,
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
        Diagnostic? diagnostic,
        Location location)
    {
        InputType = inputType;
        Fields = fields;
        Diagnostic = diagnostic;
        Location = location;
    }

    internal ITypeSymbol InputType { get; }

    internal ImmutableArray<SchemaFieldDeclaration> Fields { get; }

    internal Diagnostic? Diagnostic { get; }

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
