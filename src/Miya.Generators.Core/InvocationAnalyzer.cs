using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

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

        var parameterDiagnostic = TryGetRouteParameterDiagnostic(
            semanticModel,
            invocation,
            symbol,
            cancellationToken);
        if (parameterDiagnostic is not null)
        {
            return new InvocationAnalysis(
                invocation,
                null,
                false,
                null,
                null,
                null,
                parameterDiagnostic);
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

        if (TryGetSchemaDefinition(
                semanticModel,
                invocation,
                symbol,
                cancellationToken,
                out var schemaDefinition,
                out var schemaPartDefinition))
        {
            return new InvocationAnalysis(
                invocation,
                null,
                false,
                null,
                null,
                null,
                null,
                schemaDefinition: schemaDefinition,
                schemaPartDefinition: schemaPartDefinition,
                diagnostics: schemaDefinition is not null
                    ? schemaDefinition.Diagnostics
                    : schemaPartDefinition!.Diagnostics);
        }

        if (TryGetSchemaEndpoint(
                semanticModel,
                invocation,
                symbol,
                cancellationToken,
                out var schemaEndpoint,
                out var schemaEndpointDiagnostic))
        {
            return new InvocationAnalysis(
                invocation,
                null,
                false,
                null,
                null,
                null,
                schemaEndpointDiagnostic,
                schemaEndpoint: schemaEndpoint);
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

    private static Diagnostic? TryGetRouteParameterDiagnostic(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        CancellationToken cancellationToken)
    {
        if (method.Name != "Param"
            || method.Parameters.Length != 1
            || GetMetadataName(method.ContainingType) != "Miya.Context"
            || !TryGetArgumentExpression(
                semanticModel,
                invocation,
                0,
                cancellationToken,
                out var nameExpression)
            || !(nameExpression is LiteralExpressionSyntax nameLiteral)
            || !nameLiteral.IsKind(SyntaxKind.StringLiteralExpression))
        {
            return null;
        }

        foreach (var candidate in invocation.Ancestors().OfType<InvocationExpressionSyntax>())
        {
            var routeMethod = semanticModel.GetSymbolInfo(candidate, cancellationToken).Symbol as IMethodSymbol;
            if (routeMethod is null || !IsAnyRouteMethod(routeMethod))
            {
                continue;
            }

            var patternParameter = routeMethod.Parameters.FirstOrDefault(static parameter => parameter.Name == "pattern");
            if (patternParameter is null
                || !(routeMethod.ReducedFrom is not null
                    ? TryGetNamedArgumentExpression(
                        semanticModel,
                        candidate,
                        "pattern",
                        cancellationToken,
                        out var patternExpression)
                    : TryGetArgumentExpression(
                        semanticModel,
                        candidate,
                        patternParameter.Ordinal,
                        cancellationToken,
                        out patternExpression))
                || !(patternExpression is LiteralExpressionSyntax patternLiteral)
                || !patternLiteral.IsKind(SyntaxKind.StringLiteralExpression)
                || !RoutePatternParser.TryParse(patternLiteral.Token.ValueText, out var template, out _))
            {
                return null;
            }

            var name = nameLiteral.Token.ValueText;
            if (!template!.ParameterNames.Contains(name, StringComparer.Ordinal))
            {
                return Diagnostic.Create(
                    DiagnosticCatalog.UnknownRouteParameterAccess,
                    nameExpression.GetLocation(),
                    patternLiteral.Token.ValueText,
                    name);
            }

            return null;
        }

        return null;
    }

    private static bool TryGetSchemaDefinition(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        CancellationToken cancellationToken,
        out SchemaDefinition? definition,
        out SchemaPartDefinition? partDefinition)
    {
        definition = null;
        partDefinition = null;
        var isPart = method.Name == "Part";
        if (method.Name is not ("For" or "Part")
            || method.TypeArguments.Length != 1
            || GetMetadataName(method.ContainingType) != "Miya.Schema.Schemas")
        {
            return false;
        }

        var inputType = method.TypeArguments[0];
        var fields = ImmutableArray.CreateBuilder<SchemaFieldDeclaration>();
        var parts = ImmutableArray.CreateBuilder<SchemaPartUse>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var current = invocation;
        while (current.Parent is MemberAccessExpressionSyntax memberAccess
               && ReferenceEquals(memberAccess.Expression, current)
               && memberAccess.Parent is InvocationExpressionSyntax next)
        {
            var builderMethod = semanticModel.GetSymbolInfo(next, cancellationToken).Symbol as IMethodSymbol;
            if (builderMethod is null)
            {
                break;
            }

            if (!isPart && TryGetSchemaPartUse(builderMethod, out var partType))
            {
                parts.Add(new SchemaPartUse(partType!, next.GetLocation()));
                current = next;
                continue;
            }

            var expectedBuilderType = isPart
                ? "Miya.Schema.SchemaPart`1"
                : "Miya.Schema.Schema`1";
            if (GetMetadataName(builderMethod.ContainingType.OriginalDefinition) != expectedBuilderType)
            {
                break;
            }

            var source = SchemaFieldSource.Automatic;
            switch (builderMethod.Name)
            {
                case "Route":
                    source = SchemaFieldSource.Route;
                    break;
                case "Query":
                    source = SchemaFieldSource.Query;
                    break;
                case "Body":
                    source = SchemaFieldSource.Body;
                    break;
                case "Form":
                    source = SchemaFieldSource.Form;
                    break;
                case "Header":
                    source = SchemaFieldSource.Header;
                    break;
                default:
                    break;
            }

            if (builderMethod.Name is not ("Route" or "Query" or "Body" or "Form" or "Header"))
            {
                break;
            }

            if (!TryGetArgumentExpression(
                    semanticModel,
                    next,
                    builderMethod.Parameters.First(static parameter => parameter.Name == "field").Ordinal,
                    cancellationToken,
                    out var selector)
                || !TryGetSelectedProperty(semanticModel, selector, cancellationToken, out var property))
            {
                diagnostics.Add(Diagnostic.Create(
                    DiagnosticCatalog.InvalidSchemaDefinition,
                    next.GetLocation(),
                    inputType.ToDisplayString(),
                    "field selectors must have the form 'value => value.Property'"));
                current = next;
                continue;
            }

            var selectedProperty = property!;
            if (!(isPart
                    ? IsPropertyDeclaredByPartType(selectedProperty, inputType)
                    : SymbolEqualityComparer.Default.Equals(selectedProperty.ContainingType, inputType)))
            {
                diagnostics.Add(Diagnostic.Create(
                    DiagnosticCatalog.InvalidSchemaDefinition,
                    selector.GetLocation(),
                    inputType.ToDisplayString(),
                    "a field selector refers to a property on another type"));
                current = next;
                continue;
            }

            if (!names.Add(selectedProperty.Name))
            {
                diagnostics.Add(Diagnostic.Create(
                    DiagnosticCatalog.InvalidSchemaDefinition,
                    selector.GetLocation(),
                    inputType.ToDisplayString(),
                    "field '" + selectedProperty.Name + "' is configured more than once"));
                current = next;
                continue;
            }

            string? headerName = null;
            if (source == SchemaFieldSource.Header)
            {
                var headerParameter = builderMethod.Parameters.First(static parameter => parameter.Name == "name");
                if (!TryGetArgumentExpression(
                        semanticModel,
                        next,
                        headerParameter.Ordinal,
                        cancellationToken,
                        out var headerExpression)
                    || !semanticModel.GetConstantValue(headerExpression, cancellationToken).HasValue
                    || !(semanticModel.GetConstantValue(headerExpression, cancellationToken).Value is string constantHeader)
                    || constantHeader.Length == 0)
                {
                    diagnostics.Add(Diagnostic.Create(
                        DiagnosticCatalog.InvalidSchemaDefinition,
                        next.GetLocation(),
                        inputType.ToDisplayString(),
                        "header names must be non-empty constant strings"));
                }
                else
                {
                    headerName = constantHeader;
                }
            }

            var rules = ParseRules(
                semanticModel,
                next,
                builderMethod,
                selectedProperty,
                cancellationToken,
                diagnostics);
            fields.Add(new SchemaFieldDeclaration(
                selectedProperty,
                source,
                headerName,
                rules,
                selector.GetLocation()));
            current = next;
        }

        if (isPart)
        {
            partDefinition = new SchemaPartDefinition(
                inputType,
                fields.ToImmutable(),
                diagnostics.ToImmutable(),
                invocation.GetLocation());
        }
        else
        {
            definition = new SchemaDefinition(
                inputType,
                fields.ToImmutable(),
                parts.ToImmutable(),
                diagnostics.ToImmutable(),
                invocation.GetLocation());
        }

        return true;
    }

    private static bool TryGetSchemaPartUse(IMethodSymbol method, out ITypeSymbol? partType)
    {
        partType = null;
        var declaredMethod = method.ReducedFrom ?? method;
        if (method.Name != "Use"
            || GetMetadataName(declaredMethod.ContainingType) != "Miya.Schema.SchemaPartExtensions"
            || method.TypeArguments.Length != 2)
        {
            return false;
        }

        partType = method.TypeArguments[1];
        return true;
    }

    private static bool IsPropertyDeclaredByPartType(IPropertySymbol property, ITypeSymbol partType)
    {
        if (property.IsStatic || !(partType is INamedTypeSymbol namedPartType))
        {
            return false;
        }

        for (var current = namedPartType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(property.ContainingType, current))
            {
                return true;
            }
        }

        foreach (var @interface in namedPartType.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(property.ContainingType, @interface))
            {
                return true;
            }
        }

        return false;
    }

    private static ImmutableArray<SchemaRuleDeclaration> ParseRules(
        SemanticModel semanticModel,
        InvocationExpressionSyntax builderInvocation,
        IMethodSymbol builderMethod,
        IPropertySymbol property,
        CancellationToken cancellationToken,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var rulesParameter = builderMethod.Parameters.FirstOrDefault(static parameter => parameter.Name == "rules");
        if (rulesParameter is null
            || !TryGetArgumentExpression(
                semanticModel,
                builderInvocation,
                rulesParameter.Ordinal,
                cancellationToken,
                out var rulesExpression))
        {
            return ImmutableArray<SchemaRuleDeclaration>.Empty;
        }

        var constant = semanticModel.GetConstantValue(rulesExpression, cancellationToken);
        if (constant.HasValue && constant.Value is null)
        {
            return ImmutableArray<SchemaRuleDeclaration>.Empty;
        }

        if (!TryResolveRuleChain(
                semanticModel,
                rulesExpression,
                cancellationToken,
                out var ruleSemanticModel,
                out var ruleChain,
                out var ruleParameter)
            || !TryCollectRuleInvocations(
                ruleSemanticModel,
                ruleChain,
                ruleParameter,
                cancellationToken,
                out var ruleInvocations))
        {
            diagnostics.Add(Diagnostic.Create(
                DiagnosticCatalog.InvalidSchemaRuleDeclaration,
                rulesExpression.GetLocation()));
            return ImmutableArray<SchemaRuleDeclaration>.Empty;
        }

        var result = ImmutableArray.CreateBuilder<SchemaRuleDeclaration>();
        foreach (var ruleInvocation in ruleInvocations)
        {
            var ruleMethod = (IMethodSymbol)ruleSemanticModel.GetSymbolInfo(
                ruleInvocation,
                cancellationToken).Symbol!;
            _ = TryGetRuleKind(ruleMethod.Name, out var kind);

            var arguments = new List<ExpressionSyntax>();
            if (ruleSemanticModel.GetOperation(ruleInvocation, cancellationToken) is IInvocationOperation operation)
            {
                arguments.AddRange(operation.Arguments
                    .OrderBy(static argument => argument.Parameter?.Ordinal ?? int.MaxValue)
                    .Select(static argument => ((ArgumentSyntax)argument.Syntax).Expression));
            }

            string? predicate = null;
            string? message = null;
            var values = ImmutableArray.CreateBuilder<object?>();
            if (kind == SchemaRuleKind.Must)
            {
                if (arguments.Count != 2
                    || !(ruleSemanticModel.GetConstantValue(arguments[1], cancellationToken).Value is string constantMessage)
                    || constantMessage.Length == 0)
                {
                    diagnostics.Add(InvalidRule(
                        ruleInvocation,
                        ruleMethod.Name,
                        property.Name,
                        "Must requires a predicate and a non-empty constant message"));
                    continue;
                }

                if (arguments[0] is LambdaExpressionSyntax predicateLambda)
                {
                    var flow = ruleSemanticModel.AnalyzeDataFlow(predicateLambda);
                    if (flow is null || !flow.Succeeded || flow.CapturedInside.Length != 0)
                    {
                        diagnostics.Add(InvalidRule(
                            ruleInvocation,
                            ruleMethod.Name,
                            property.Name,
                            "Must predicates cannot capture local state"));
                        continue;
                    }

                    var inaccessibleMember = FindInaccessibleGeneratedMember(
                        predicateLambda,
                        ruleSemanticModel,
                        cancellationToken);
                    if (inaccessibleMember is not null)
                    {
                        diagnostics.Add(InaccessibleRuleMember(arguments[0], inaccessibleMember));
                        continue;
                    }

                    predicate = QualifyPredicate(predicateLambda, ruleSemanticModel);
                }
                else if (ruleSemanticModel.GetSymbolInfo(arguments[0], cancellationToken).Symbol is IMethodSymbol predicateMethod
                         && predicateMethod.IsStatic)
                {
                    if (!IsAccessibleFromGeneratedCode(predicateMethod, ruleSemanticModel.Compilation))
                    {
                        diagnostics.Add(InaccessibleRuleMember(arguments[0], predicateMethod));
                        continue;
                    }

                    predicate = TypeNames.Display(predicateMethod.ContainingType) + "." +
                        GeneratedNaming.Identifier(predicateMethod.Name);
                }
                else
                {
                    diagnostics.Add(InvalidRule(
                        ruleInvocation,
                        ruleMethod.Name,
                        property.Name,
                        "Must predicates must be non-capturing lambdas or static method groups"));
                    continue;
                }

                message = constantMessage;
            }
            else
            {
                var constantsValid = true;
                foreach (var argument in arguments)
                {
                    var argumentConstant = ruleSemanticModel.GetConstantValue(argument, cancellationToken);
                    if (!argumentConstant.HasValue)
                    {
                        constantsValid = false;
                        break;
                    }

                    values.Add(argumentConstant.Value);
                }

                if (!constantsValid)
                {
                    diagnostics.Add(InvalidRule(
                        ruleInvocation,
                        ruleMethod.Name,
                        property.Name,
                        "rule arguments must be compile-time constants"));
                    continue;
                }
            }

            result.Add(new SchemaRuleDeclaration(
                kind,
                values.ToImmutable(),
                predicate,
                message,
                ruleInvocation.GetLocation()));
        }

        return result.ToImmutable();

        Diagnostic InvalidRule(
            InvocationExpressionSyntax syntax,
            string ruleName,
            string fieldName,
            string reason) => Diagnostic.Create(
                DiagnosticCatalog.InvalidSchemaRule,
                syntax.GetLocation(),
                ruleName,
                fieldName,
                reason);

        Diagnostic InaccessibleRuleMember(ExpressionSyntax syntax, ISymbol member) => Diagnostic.Create(
            DiagnosticCatalog.InaccessibleSchemaRuleMember,
            syntax.GetLocation(),
            member.ToDisplayString());
    }

    private static bool TryResolveRuleChain(
        SemanticModel semanticModel,
        ExpressionSyntax rulesExpression,
        CancellationToken cancellationToken,
        out SemanticModel ruleSemanticModel,
        out ExpressionSyntax ruleChain,
        out IParameterSymbol ruleParameter)
    {
        rulesExpression = UnwrapParentheses(rulesExpression);
        if (rulesExpression is LambdaExpressionSyntax lambda)
        {
            if (!TryGetLambdaParameter(
                    semanticModel,
                    lambda,
                    cancellationToken,
                    out var lambdaParameter)
                || !TryGetSingleExpression(lambda, out var lambdaExpression))
            {
                ruleSemanticModel = null!;
                ruleChain = null!;
                ruleParameter = null!;
                return false;
            }

            if (TryGetForwardedRuleMethod(
                    semanticModel,
                    lambdaExpression,
                    lambdaParameter!,
                    cancellationToken,
                    out var forwardedMethod))
            {
                return TryGetRuleMethodChain(
                    semanticModel,
                    forwardedMethod!,
                    cancellationToken,
                    out ruleSemanticModel,
                    out ruleChain,
                    out ruleParameter);
            }

            ruleSemanticModel = semanticModel;
            ruleChain = lambdaExpression;
            ruleParameter = lambdaParameter!;
            return true;
        }

        if (rulesExpression is BaseObjectCreationExpressionSyntax creation)
        {
            var argumentList = creation.ArgumentList;
            if (argumentList is null || argumentList.Arguments.Count != 1)
            {
                ruleSemanticModel = null!;
                ruleChain = null!;
                ruleParameter = null!;
                return false;
            }

            rulesExpression = UnwrapParentheses(argumentList.Arguments[0].Expression);
        }

        var method = semanticModel.GetSymbolInfo(rulesExpression, cancellationToken).Symbol as IMethodSymbol;
        return TryGetRuleMethodChain(
            semanticModel,
            method,
            cancellationToken,
            out ruleSemanticModel,
            out ruleChain,
            out ruleParameter);
    }

    private static bool TryGetLambdaParameter(
        SemanticModel semanticModel,
        LambdaExpressionSyntax lambda,
        CancellationToken cancellationToken,
        out IParameterSymbol? parameter)
    {
        ParameterSyntax? parameterSyntax = lambda switch
        {
            SimpleLambdaExpressionSyntax simple => simple.Parameter,
            ParenthesizedLambdaExpressionSyntax parenthesized
                when parenthesized.ParameterList.Parameters.Count == 1 =>
                parenthesized.ParameterList.Parameters[0],
            _ => null,
        };
        parameter = parameterSyntax is null
            ? null
            : semanticModel.GetDeclaredSymbol(parameterSyntax, cancellationToken);
        return parameter is not null;
    }

    private static bool TryGetSingleExpression(
        LambdaExpressionSyntax lambda,
        out ExpressionSyntax expression)
    {
        if (lambda.Body is ExpressionSyntax expressionBody)
        {
            expression = expressionBody;
            return true;
        }

        if (lambda.Body is BlockSyntax { Statements.Count: 1 } block
            && block.Statements[0] is ExpressionStatementSyntax expressionStatement)
        {
            expression = expressionStatement.Expression;
            return true;
        }

        expression = null!;
        return false;
    }

    private static bool TryGetForwardedRuleMethod(
        SemanticModel semanticModel,
        ExpressionSyntax expression,
        IParameterSymbol parameter,
        CancellationToken cancellationToken,
        out IMethodSymbol? method)
    {
        method = null;
        expression = UnwrapParentheses(expression);
        if (!(expression is InvocationExpressionSyntax invocation)
            || invocation.ArgumentList.Arguments.Count != 1
            || !(semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is IMethodSymbol target)
            || (GetMetadataName(target.ContainingType.OriginalDefinition) == "Miya.Schema.Rule`1"
                && TryGetRuleKind(target.Name, out _))
            || !TryGetReferencedParameter(
                semanticModel,
                invocation.ArgumentList.Arguments[0].Expression,
                cancellationToken,
                out var referencedParameter)
            || !SymbolEqualityComparer.Default.Equals(parameter, referencedParameter))
        {
            return false;
        }

        method = target;
        return true;
    }

    private static bool TryGetRuleMethodChain(
        SemanticModel semanticModel,
        IMethodSymbol? method,
        CancellationToken cancellationToken,
        out SemanticModel ruleSemanticModel,
        out ExpressionSyntax ruleChain,
        out IParameterSymbol ruleParameter)
    {
        ruleSemanticModel = null!;
        ruleChain = null!;
        ruleParameter = null!;
        if (method is null
            || !method.IsStatic
            || method.Parameters.Length != 1
            || !SymbolEqualityComparer.Default.Equals(
                method.ContainingAssembly,
                semanticModel.Compilation.Assembly))
        {
            return false;
        }

        foreach (var syntaxReference in method.DeclaringSyntaxReferences)
        {
            if (!(syntaxReference.GetSyntax(cancellationToken) is MethodDeclarationSyntax declaration)
                || declaration.ParameterList.Parameters.Count != 1)
            {
                continue;
            }

            ExpressionSyntax? expression = declaration.ExpressionBody?.Expression;
            if (expression is null
                && declaration.Body is BlockSyntax { Statements.Count: 1 } body
                && body.Statements[0] is ExpressionStatementSyntax expressionStatement)
            {
                expression = expressionStatement.Expression;
            }

            if (expression is null)
            {
                continue;
            }

            var bodySemanticModel = semanticModel.Compilation.GetSemanticModel(declaration.SyntaxTree);
            var parameter = bodySemanticModel.GetDeclaredSymbol(
                declaration.ParameterList.Parameters[0],
                cancellationToken);
            if (parameter is null)
            {
                continue;
            }

            ruleSemanticModel = bodySemanticModel;
            ruleChain = expression;
            ruleParameter = parameter;
            return true;
        }

        return false;
    }

    private static bool TryCollectRuleInvocations(
        SemanticModel semanticModel,
        ExpressionSyntax expression,
        IParameterSymbol parameter,
        CancellationToken cancellationToken,
        out ImmutableArray<InvocationExpressionSyntax> invocations)
    {
        var result = new List<InvocationExpressionSyntax>();
        expression = UnwrapParentheses(expression);
        while (expression is InvocationExpressionSyntax invocation
               && semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is IMethodSymbol method
               && GetMetadataName(method.ContainingType.OriginalDefinition) == "Miya.Schema.Rule`1"
               && TryGetRuleKind(method.Name, out _)
               && invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            result.Add(invocation);
            var receiver = UnwrapParentheses(memberAccess.Expression);
            if (TryGetReferencedParameter(
                    semanticModel,
                    receiver,
                    cancellationToken,
                    out var referencedParameter)
                && SymbolEqualityComparer.Default.Equals(parameter, referencedParameter))
            {
                result.Reverse();
                invocations = result.ToImmutableArray();
                return true;
            }

            expression = receiver;
        }

        invocations = ImmutableArray<InvocationExpressionSyntax>.Empty;
        return false;
    }

    private static bool TryGetReferencedParameter(
        SemanticModel semanticModel,
        ExpressionSyntax expression,
        CancellationToken cancellationToken,
        out IParameterSymbol? parameter)
    {
        expression = UnwrapParentheses(expression);
        parameter = semanticModel.GetSymbolInfo(expression, cancellationToken).Symbol as IParameterSymbol;
        return parameter is not null;
    }

    private static ExpressionSyntax UnwrapParentheses(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
    }

    private static ISymbol? FindInaccessibleGeneratedMember(
        LambdaExpressionSyntax predicate,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        foreach (var name in predicate.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            var symbol = semanticModel.GetSymbolInfo(name, cancellationToken).Symbol;
            if (symbol is IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol or INamedTypeSymbol
                && symbol is not IParameterSymbol
                && !IsAccessibleFromGeneratedCode(symbol, semanticModel.Compilation))
            {
                return symbol;
            }
        }

        return null;
    }

    private static bool IsAccessibleFromGeneratedCode(ISymbol symbol, Compilation compilation)
    {
        if (symbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction })
        {
            return false;
        }

        for (var current = symbol; current is not null; current = current.ContainingType)
        {
            if (current is INamedTypeSymbol { IsFileLocal: true })
            {
                return false;
            }

            switch (current.DeclaredAccessibility)
            {
                case Accessibility.Public:
                    break;
                case Accessibility.Internal:
                    if (!SymbolEqualityComparer.Default.Equals(current.ContainingAssembly, compilation.Assembly))
                    {
                        return false;
                    }

                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    private static string QualifyPredicate(
        LambdaExpressionSyntax predicate,
        SemanticModel semanticModel)
    {
        return new PredicateQualifier(semanticModel).Visit(predicate)!.ToString();
    }

    private sealed class PredicateQualifier : CSharpSyntaxRewriter
    {
        private readonly SemanticModel _semanticModel;

        internal PredicateQualifier(SemanticModel semanticModel)
        {
            _semanticModel = semanticModel;
        }

        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var method = _semanticModel.GetSymbolInfo(node).Symbol as IMethodSymbol;
            if (method?.ReducedFrom is not IMethodSymbol extensionMethod
                || node.Expression is not MemberAccessExpressionSyntax memberAccess)
            {
                return base.VisitInvocationExpression(node);
            }

            var receiver = (ExpressionSyntax)Visit(memberAccess.Expression)!;
            var receiverArgument = SyntaxFactory.Argument(receiver);
            if (extensionMethod.Parameters[0].RefKind == RefKind.Ref)
            {
                receiverArgument = receiverArgument.WithRefOrOutKeyword(
                    SyntaxFactory.Token(SyntaxKind.RefKeyword));
            }

            var arguments = new List<ArgumentSyntax> { receiverArgument };
            arguments.AddRange(node.ArgumentList.Arguments.Select(argument =>
                (ArgumentSyntax)Visit(argument)!));
            var name = (SimpleNameSyntax)Visit(memberAccess.Name)!;
            var expression = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.ParseExpression(TypeNames.Display(extensionMethod.ContainingType)),
                name.WithoutTrivia());
            return SyntaxFactory.InvocationExpression(
                    expression,
                    SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(arguments)))
                .WithTriviaFrom(node);
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            var symbol = _semanticModel.GetSymbolInfo(node).Symbol;
            if (IsQualifiedNamePart(node))
            {
                return base.VisitIdentifierName(node);
            }

            if (symbol is INamedTypeSymbol type)
            {
                return SyntaxFactory.ParseName(TypeNames.Display(type)).WithTriviaFrom(node);
            }

            var containingType = symbol switch
            {
                IFieldSymbol { IsStatic: true } field => field.ContainingType,
                IPropertySymbol { IsStatic: true } property => property.ContainingType,
                IMethodSymbol { IsStatic: true } method => method.ContainingType,
                _ => null,
            };
            if (containingType is null)
            {
                return base.VisitIdentifierName(node);
            }

            return SyntaxFactory.ParseExpression(
                    TypeNames.Display(containingType) + "." + GeneratedNaming.Identifier(symbol!.Name))
                .WithTriviaFrom(node);
        }

        public override SyntaxNode? VisitGenericName(GenericNameSyntax node)
        {
            if (IsQualifiedNamePart(node))
            {
                return base.VisitGenericName(node);
            }

            var symbol = _semanticModel.GetSymbolInfo(node).Symbol;
            if (symbol is INamedTypeSymbol type)
            {
                return SyntaxFactory.ParseName(TypeNames.Display(type)).WithTriviaFrom(node);
            }

            if (symbol is IMethodSymbol { IsStatic: true } method)
            {
                var visited = (GenericNameSyntax)base.VisitGenericName(node)!;
                return SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.ParseExpression(TypeNames.Display(method.ContainingType)),
                        visited.WithoutTrivia())
                    .WithTriviaFrom(node);
            }

            return base.VisitGenericName(node);
        }

        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            return _semanticModel.GetSymbolInfo(node).Symbol is INamedTypeSymbol type
                ? SyntaxFactory.ParseExpression(TypeNames.Display(type)).WithTriviaFrom(node)
                : base.VisitMemberAccessExpression(node);
        }

        public override SyntaxNode? VisitQualifiedName(QualifiedNameSyntax node)
        {
            return _semanticModel.GetSymbolInfo(node).Symbol is INamedTypeSymbol type
                ? SyntaxFactory.ParseName(TypeNames.Display(type)).WithTriviaFrom(node)
                : base.VisitQualifiedName(node);
        }

        public override SyntaxNode? VisitAliasQualifiedName(AliasQualifiedNameSyntax node)
        {
            return _semanticModel.GetSymbolInfo(node).Symbol is INamedTypeSymbol type
                ? SyntaxFactory.ParseName(TypeNames.Display(type)).WithTriviaFrom(node)
                : base.VisitAliasQualifiedName(node);
        }

        private static bool IsQualifiedNamePart(SimpleNameSyntax node) => node.Parent switch
        {
            MemberAccessExpressionSyntax memberAccess => ReferenceEquals(memberAccess.Name, node),
            QualifiedNameSyntax qualified => ReferenceEquals(qualified.Right, node),
            AliasQualifiedNameSyntax aliasQualified => ReferenceEquals(aliasQualified.Name, node),
            _ => false,
        };
    }

    private static bool TryGetRuleKind(string name, out SchemaRuleKind kind)
    {
        switch (name)
        {
            case "Optional": kind = SchemaRuleKind.Optional; return true;
            case "Default": kind = SchemaRuleKind.Default; return true;
            case "Must": kind = SchemaRuleKind.Must; return true;
            case "Min": kind = SchemaRuleKind.Min; return true;
            case "Max": kind = SchemaRuleKind.Max; return true;
            case "Range": kind = SchemaRuleKind.Range; return true;
            case "Positive": kind = SchemaRuleKind.Positive; return true;
            case "NonNegative": kind = SchemaRuleKind.NonNegative; return true;
            case "NotEmpty": kind = SchemaRuleKind.NotEmpty; return true;
            case "Length": kind = SchemaRuleKind.Length; return true;
            case "MinLength": kind = SchemaRuleKind.MinLength; return true;
            case "MaxLength": kind = SchemaRuleKind.MaxLength; return true;
            case "Pattern": kind = SchemaRuleKind.Pattern; return true;
            default:
                kind = default;
                return false;
        }
    }

    private static bool TryGetSelectedProperty(
        SemanticModel semanticModel,
        ExpressionSyntax selector,
        CancellationToken cancellationToken,
        out IPropertySymbol? property)
    {
        property = null;
        if (!(selector is LambdaExpressionSyntax lambda))
        {
            return false;
        }

        ExpressionSyntax? body = lambda.Body as ExpressionSyntax;
        if (body is null && lambda.Body is BlockSyntax block
            && block.Statements.Count == 1
            && block.Statements[0] is ReturnStatementSyntax returnStatement)
        {
            body = returnStatement.Expression;
        }

        if (body is null)
        {
            return false;
        }

        property = semanticModel.GetSymbolInfo(body, cancellationToken).Symbol as IPropertySymbol;
        return property is not null;
    }

    private static bool TryGetSchemaEndpoint(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        CancellationToken cancellationToken,
        out SchemaEndpointCall? endpoint,
        out Diagnostic? diagnostic)
    {
        endpoint = null;
        diagnostic = null;
        var declaredMethod = method.ReducedFrom ?? method;
        if (GetMetadataName(declaredMethod.ContainingType) != "Miya.Schema.EndpointExtensions"
            || method.Name is not ("Get" or "Post" or "Put" or "Patch" or "Delete" or "Head" or "Options" or "On"))
        {
            return false;
        }

        var schemaParameter = method.Parameters.FirstOrDefault(static parameter => parameter.Name == "schema");
        var patternParameter = method.Parameters.FirstOrDefault(static parameter => parameter.Name == "pattern");
        if (schemaParameter is null
            || !(schemaParameter.Type is INamedTypeSymbol schemaType)
            || schemaType.TypeArguments.Length != 1)
        {
            return false;
        }

        if (patternParameter is null
            || !TryGetNamedArgumentExpression(
                semanticModel,
                invocation,
                "pattern",
                cancellationToken,
                out var patternExpression)
            || !(patternExpression is LiteralExpressionSyntax patternLiteral)
            || !patternLiteral.IsKind(SyntaxKind.StringLiteralExpression))
        {
            diagnostic = Diagnostic.Create(
                DiagnosticCatalog.InvalidSchemaDefinition,
                invocation.GetLocation(),
                schemaType.TypeArguments[0].ToDisplayString(),
                "typed endpoint route patterns must be string literals");
            return true;
        }

        var pattern = patternLiteral.Token.ValueText;
        if (!RoutePatternParser.TryParse(pattern, out var template, out var error))
        {
            diagnostic = Diagnostic.Create(
                DiagnosticCatalog.InvalidRoute,
                patternExpression.GetLocation(),
                pattern,
                error);
            return true;
        }

        var httpMethod = method.Name.ToUpperInvariant();
        if (method.Name == "On")
        {
            var methodParameter = method.Parameters.First(static parameter => parameter.Name == "method");
            if (!TryGetNamedArgumentExpression(
                    semanticModel,
                    invocation,
                    "method",
                    cancellationToken,
                    out var methodExpression)
                || !(semanticModel.GetConstantValue(methodExpression, cancellationToken).Value is string constantMethod))
            {
                diagnostic = Diagnostic.Create(
                    DiagnosticCatalog.InvalidSchemaDefinition,
                    invocation.GetLocation(),
                    schemaType.TypeArguments[0].ToDisplayString(),
                    "the HTTP method passed to On must be a constant string");
                return true;
            }

            httpMethod = constantMethod.ToUpperInvariant();
        }

        endpoint = new SchemaEndpointCall(
            pattern,
            httpMethod,
            schemaType.TypeArguments[0],
            template!,
            invocation.GetLocation());
        return true;
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
            jsonType = NormalizeTopLevelNullability(method.TypeArguments[0]);
            interceptJson = true;
            return true;
        }

        if (method.Name == "Json" && containingName == "Miya.Request" && method.Parameters.Length == 0)
        {
            jsonType = NormalizeTopLevelNullability(method.TypeArguments[0]);
            return true;
        }

        if (containingName == "Miya.Json.Json"
            && (method.Name == "Include" || method.Name == "Serialize" || method.Name == "Deserialize"))
        {
            if (method.Name != "Include" && HasExplicitCodecParameter(method))
            {
                return false;
            }

            jsonType = NormalizeTopLevelNullability(method.TypeArguments[0]);
            return true;
        }

        return false;
    }

    private static bool HasExplicitCodecParameter(IMethodSymbol method)
    {
        foreach (var parameter in method.Parameters)
        {
            if (parameter.Type is INamedTypeSymbol named
                && GetMetadataName(named.OriginalDefinition) == "Miya.Json.IJsonCodec`1")
            {
                return true;
            }
        }

        return false;
    }

    // A call site inferring T as an annotated reference type (for example User?) must share the
    // codec generated for the underlying type; codecs already accept null values.
    private static ITypeSymbol NormalizeTopLevelNullability(ITypeSymbol type) =>
        type.IsReferenceType && type.NullableAnnotation == NullableAnnotation.Annotated
            ? type.WithNullableAnnotation(NullableAnnotation.NotAnnotated)
            : type;

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

        if (!IsRouteMethod(method.Name))
        {
            return false;
        }

        var patternParameter = method.Parameters.FirstOrDefault(static parameter => parameter.Name == "pattern");
        if (patternParameter is null
            || !TryGetArgumentExpression(
                semanticModel,
                invocation,
                patternParameter.Ordinal,
                cancellationToken,
                out var patternExpression))
        {
            return false;
        }

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

        var routeMethod = GetRouteMethod(semanticModel, invocation, method, cancellationToken);
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

    private static string GetRouteMethod(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        CancellationToken cancellationToken)
    {
        if (method.Name == "All")
        {
            return "*";
        }

        if (method.Name != "On")
        {
            return method.Name.ToUpperInvariant();
        }

        var methodParameter = method.Parameters.FirstOrDefault(static parameter => parameter.Name == "method");
        if (methodParameter is not null
            && TryGetArgumentExpression(
                semanticModel,
                invocation,
                methodParameter.Ordinal,
                cancellationToken,
                out var expression))
        {
            if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                return literal.Token.ValueText.ToUpperInvariant();
            }
        }

        return "<dynamic>";
    }

    private static bool TryGetArgumentExpression(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        int parameterOrdinal,
        CancellationToken cancellationToken,
        out ExpressionSyntax expression)
    {
        if (semanticModel.GetOperation(invocation, cancellationToken) is IInvocationOperation operation)
        {
            foreach (var argument in operation.Arguments)
            {
                if (argument.Parameter?.Ordinal == parameterOrdinal
                    && argument.Syntax is ArgumentSyntax argumentSyntax)
                {
                    expression = argumentSyntax.Expression;
                    return true;
                }
            }
        }

        expression = null!;
        return false;
    }

    private static bool TryGetNamedArgumentExpression(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        string parameterName,
        CancellationToken cancellationToken,
        out ExpressionSyntax expression)
    {
        if (semanticModel.GetOperation(invocation, cancellationToken) is IInvocationOperation operation)
        {
            foreach (var argument in operation.Arguments)
            {
                if (argument.Parameter?.Name == parameterName
                    && argument.Syntax is ArgumentSyntax argumentSyntax)
                {
                    expression = argumentSyntax.Expression;
                    return true;
                }
            }
        }

        expression = null!;
        return false;
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

    private static bool IsAnyRouteMethod(IMethodSymbol method)
    {
        if (!IsRouteMethod(method.Name))
        {
            return false;
        }

        var declaredMethod = method.ReducedFrom ?? method;
        var containingName = GetMetadataName(declaredMethod.ContainingType.OriginalDefinition);
        return containingName == "Miya.App`1"
            || containingName == "Miya.Schema.EndpointExtensions";
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
