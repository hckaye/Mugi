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
        Diagnostic? diagnostic)
    {
        Syntax = syntax;
        JsonType = jsonType;
        InterceptJson = interceptJson;
        JsonTargetMethod = jsonTargetMethod;
        JsonInterceptAttribute = jsonInterceptAttribute;
        Route = route;
        Diagnostic = diagnostic;
    }

    internal InvocationExpressionSyntax Syntax { get; }

    internal ITypeSymbol? JsonType { get; }

    internal bool InterceptJson { get; }

    internal IMethodSymbol? JsonTargetMethod { get; }

    internal string? JsonInterceptAttribute { get; }

    internal RouteCall? Route { get; }

    internal Diagnostic? Diagnostic { get; }
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
