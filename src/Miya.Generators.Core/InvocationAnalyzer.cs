using System;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Miya.Generators.Core;

internal static class InvocationAnalyzer
{
    internal static InvocationAnalysis? Analyze(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        bool includeInterceptLocation,
        CancellationToken cancellationToken)
    {
        var symbol = semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol;
        if (symbol is null)
        {
            return null;
        }

        if (TryGetJsonType(symbol, out var jsonType, out var interceptJson))
        {
            if (!IsClosed(jsonType!))
            {
                return null;
            }

            if (jsonType!.IsAnonymousType)
            {
                return new InvocationAnalysis(
                    invocation,
                    null,
                    false,
                    null,
                    null,
                    null,
                    Diagnostic.Create(
                        DiagnosticCatalog.AnonymousJsonType,
                        invocation.GetLocation(),
                        jsonType.ToDisplayString()));
            }

            string? interceptAttribute = null;
            if (interceptJson && includeInterceptLocation)
            {
                var location = semanticModel.GetInterceptableLocation(invocation, cancellationToken);
                if (location is not null)
                {
                    interceptAttribute = location.GetInterceptsLocationAttributeSyntax();
                }
            }

            return new InvocationAnalysis(
                invocation,
                jsonType,
                interceptJson,
                symbol,
                interceptAttribute,
                null,
                null);
        }

        if (!TryGetRouteCall(
                semanticModel,
                invocation,
                symbol,
                includeInterceptLocation,
                cancellationToken,
                out var route,
                out var routeDiagnostic))
        {
            return routeDiagnostic is null
                ? null
                : new InvocationAnalysis(invocation, null, false, null, null, null, routeDiagnostic);
        }

        return new InvocationAnalysis(invocation, null, false, null, null, route, routeDiagnostic);
    }

    private static bool TryGetJsonType(
        IMethodSymbol method,
        out ITypeSymbol? jsonType,
        out bool interceptJson)
    {
        jsonType = null;
        interceptJson = false;
        if (method.TypeArguments.Length != 1 || method.TypeArguments[0].TypeKind == TypeKind.Error)
        {
            return false;
        }

        var containingType = method.OriginalDefinition.ContainingType;
        var containingName = GetMetadataName(containingType);
        if (method.Name == "Json" && containingName == "Miya.Context" && method.Parameters.Length == 1)
        {
            jsonType = method.TypeArguments[0];
            interceptJson = true;
            return true;
        }

        if (method.Name == "Json" && containingName == "Miya.Request" && method.Parameters.Length == 0)
        {
            jsonType = method.TypeArguments[0];
            return true;
        }

        if (containingName == "Miya.Json.MiyaJson"
            && (method.Name == "Include" || method.Name == "Serialize" || method.Name == "Deserialize"))
        {
            jsonType = method.TypeArguments[0];
            return true;
        }

        return false;
    }

    private static bool TryGetRouteCall(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        bool includeInterceptLocation,
        CancellationToken cancellationToken,
        out RouteCall? route,
        out Diagnostic? diagnostic)
    {
        route = null;
        diagnostic = null;
        var originalContainingType = method.OriginalDefinition.ContainingType;
        if (GetMetadataName(originalContainingType.OriginalDefinition) != "Miya.App`1")
        {
            return false;
        }

        var patternArgumentIndex = method.Name == "On" ? 1 : 0;
        if (!IsRouteMethod(method.Name) || invocation.ArgumentList.Arguments.Count <= patternArgumentIndex)
        {
            return false;
        }

        var patternExpression = invocation.ArgumentList.Arguments[patternArgumentIndex].Expression;
        if (!(patternExpression is LiteralExpressionSyntax literal)
            || !literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            return false;
        }

        var pattern = literal.Token.ValueText;
        if (!RoutePatternParser.TryParse(pattern, out var template, out var error))
        {
            diagnostic = Diagnostic.Create(
                DiagnosticCatalog.InvalidRoute,
                patternExpression.GetLocation(),
                pattern,
                error);
            return false;
        }

        var routeMethod = GetRouteMethod(invocation, method);
        var receiverSymbol = GetReceiverSymbol(semanticModel, invocation, cancellationToken);
        string? interceptAttribute = null;
        if (includeInterceptLocation)
        {
            var interceptableLocation = semanticModel.GetInterceptableLocation(invocation, cancellationToken);
            if (interceptableLocation is not null)
            {
                interceptAttribute = interceptableLocation.GetInterceptsLocationAttributeSyntax();
            }
        }

        route = new RouteCall(
            pattern,
            routeMethod,
            method,
            template!,
            receiverSymbol,
            interceptAttribute);
        return true;
    }

    private static ISymbol? GetReceiverSymbol(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        CancellationToken cancellationToken)
    {
        var memberAccess = invocation.Expression as MemberAccessExpressionSyntax;
        if (memberAccess is null)
        {
            return null;
        }

        return semanticModel.GetSymbolInfo(memberAccess.Expression, cancellationToken).Symbol;
    }

    private static string GetRouteMethod(InvocationExpressionSyntax invocation, IMethodSymbol method)
    {
        if (method.Name == "All")
        {
            return "*";
        }

        if (method.Name != "On")
        {
            return method.Name.ToUpperInvariant();
        }

        if (invocation.ArgumentList.Arguments.Count > 0)
        {
            var expression = invocation.ArgumentList.Arguments[0].Expression;
            if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                return literal.Token.ValueText.ToUpperInvariant();
            }
        }

        return "<dynamic>";
    }

    private static bool IsRouteMethod(string name)
    {
        switch (name)
        {
            case "Get":
            case "Post":
            case "Put":
            case "Delete":
            case "Patch":
            case "Head":
            case "Options":
            case "All":
            case "On":
                return true;
            default:
                return false;
        }
    }

    private static bool IsClosed(ITypeSymbol type)
    {
        if (type is ITypeParameterSymbol)
        {
            return false;
        }

        if (type is IArrayTypeSymbol array)
        {
            return IsClosed(array.ElementType);
        }

        if (type is INamedTypeSymbol named)
        {
            foreach (var argument in named.TypeArguments)
            {
                if (!IsClosed(argument))
                {
                    return false;
                }
            }
        }

        return true;
    }

    internal static string GetMetadataName(INamedTypeSymbol type)
    {
        var name = type.MetadataName;
        var containing = type.ContainingType;
        while (containing is not null)
        {
            name = containing.MetadataName + "+" + name;
            containing = containing.ContainingType;
        }

        if (!type.ContainingNamespace.IsGlobalNamespace)
        {
            name = type.ContainingNamespace.ToDisplayString() + "." + name;
        }

        return name;
    }
}
