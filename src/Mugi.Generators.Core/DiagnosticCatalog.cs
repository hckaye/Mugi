using Microsoft.CodeAnalysis;

namespace Mugi.Generators.Core;

internal static class DiagnosticCatalog
{
    private const string Category = "Mugi.Generators";

    internal static readonly DiagnosticDescriptor AnonymousJsonType = new(
        "MUGI001",
        "Anonymous JSON types are not supported",
        "The anonymous type '{0}' cannot have a Json codec generated",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor InvalidRoute = new(
        "MUGI002",
        "The route pattern is invalid",
        "Route pattern '{0}' is invalid: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor DuplicateRoute = new(
        "MUGI003",
        "The route is registered more than once",
        "Route '{0} {1}' is already registered on local variable '{2}'",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor UnsupportedJsonType = new(
        "MUGI004",
        "The JSON type is not supported",
        "A Json codec cannot be generated for '{0}': {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor UnknownRouteParameterAccess = new(
        "MUGI006",
        "The route parameter is not declared",
        "Route pattern '{0}' does not declare a parameter named '{1}'",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor SchemaRouteFieldMissing = new(
        "MUGI010",
        "The schema route field is not declared by the route",
        "Schema field '{0}' is mapped from the route, but route pattern '{1}' has no ':{0}' parameter",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor SchemaRouteParameterMissing = new(
        "MUGI011",
        "The route parameter is not mapped by the input schema",
        "Route parameter '{0}' in pattern '{1}' has no route-mapped field on '{2}'",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor UnsupportedSchemaFieldType = new(
        "MUGI012",
        "The schema field type is not supported",
        "Schema field '{0}' cannot be read from {1}: {2}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor InvalidSchemaDefinition = new(
        "MUGI013",
        "The schema definition is invalid",
        "The schema for '{0}' is invalid: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor InvalidSchemaRule = new(
        "MUGI014",
        "The schema validation rule is invalid",
        "Rule '{0}' cannot be used for field '{1}': {2}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor AmbiguousSchemaBinding = new(
        "MUGI015",
        "The input type has more than one binding shape",
        "Input type '{0}' is used with more than one route or schema binding shape; use a separate input record for each endpoint",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor FormBodyConflict = new(
        "MUGI016",
        "Form and JSON body mappings cannot be combined",
        "Schema for '{0}' maps fields from both form data and the JSON body",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor DuplicateSchemaPart = new(
        "MUGI017",
        "The schema part has more than one definition",
        "Schema part type '{0}' has more than one definition; declare each part type only once",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor UndeclaredSchemaPart = new(
        "MUGI018",
        "The schema part is not declared in this compilation",
        "Schema part '{0}' has no definition; parts must be declared in the same compilation",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor ExplicitSchemaPartMember = new(
        "MUGI019",
        "Schema part members must be implemented implicitly",
        "Schema part member '{0}' on '{1}' is implemented explicitly; implement the member implicitly",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor InvalidOpenApiDocument = new(
        "MUGI020",
        "The OpenAPI document is invalid",
        "OpenAPI document '{0}' is invalid: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor UnsupportedOpenApiSchema = new(
        "MUGI021",
        "The OpenAPI schema structure is not supported",
        "OpenAPI schema '{0}' is skipped: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor UnrepresentableOpenApiItem = new(
        "MUGI022",
        "The OpenAPI item cannot be represented by Mugi",
        "OpenAPI item '{0}' is skipped: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor OpenApiNameCollision = new(
        "MUGI023",
        "OpenAPI names produce the same C# name",
        "OpenAPI name '{0}' collides with generated C# name '{1}'",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor AmbiguousSchemaPartMember = new(
        "MUGI024",
        "The schema part member is ambiguous",
        "Schema part member '{0}' on '{1}' is contributed by more than one part",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor InvalidSchemaRuleDeclaration = new(
        "MUGI025",
        "The schema rule declaration is invalid",
        "Rule declarations must be an inline lambda or a static method containing a single rule chain",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor InaccessibleSchemaRuleMember = new(
        "MUGI026",
        "A schema rule member is inaccessible to generated code",
        "Member '{0}' must be internal or public because generated code references it",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
