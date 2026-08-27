using Microsoft.CodeAnalysis;

namespace Miya.Generators.Core;

internal static class DiagnosticCatalog
{
    private const string Category = "Miya.Generators";

    internal static readonly DiagnosticDescriptor AnonymousJsonType = new(
        "MIYA001",
        "Anonymous JSON types are not supported",
        "The anonymous type '{0}' cannot have a Json codec generated",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor InvalidRoute = new(
        "MIYA002",
        "The route pattern is invalid",
        "Route pattern '{0}' is invalid: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor DuplicateRoute = new(
        "MIYA003",
        "The route is registered more than once",
        "Route '{0} {1}' is already registered on local variable '{2}'",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor UnsupportedJsonType = new(
        "MIYA004",
        "The JSON type is not supported",
        "A Json codec cannot be generated for '{0}': {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor UnknownRouteParameterAccess = new(
        "MIYA006",
        "The route parameter is not declared",
        "Route pattern '{0}' does not declare a parameter named '{1}'",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor SchemaRouteFieldMissing = new(
        "MIYA010",
        "The schema route field is not declared by the route",
        "Schema field '{0}' is mapped from the route, but route pattern '{1}' has no ':{0}' parameter",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor SchemaRouteParameterMissing = new(
        "MIYA011",
        "The route parameter is not mapped by the input schema",
        "Route parameter '{0}' in pattern '{1}' has no route-mapped field on '{2}'",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor UnsupportedSchemaFieldType = new(
        "MIYA012",
        "The schema field type is not supported",
        "Schema field '{0}' cannot be read from {1}: {2}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor InvalidSchemaDefinition = new(
        "MIYA013",
        "The schema definition is invalid",
        "The schema for '{0}' is invalid: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor InvalidSchemaRule = new(
        "MIYA014",
        "The schema validation rule is invalid",
        "Rule '{0}' cannot be used for field '{1}': {2}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor AmbiguousSchemaBinding = new(
        "MIYA015",
        "The input type has more than one binding shape",
        "Input type '{0}' is used with more than one route or schema binding shape; use a separate input record for each endpoint",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor InvalidOpenApiDocument = new(
        "MIYA020",
        "The OpenAPI document is invalid",
        "OpenAPI document '{0}' is invalid: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor UnsupportedOpenApiSchema = new(
        "MIYA021",
        "The OpenAPI schema structure is not supported",
        "OpenAPI schema '{0}' is skipped: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor UnrepresentableOpenApiItem = new(
        "MIYA022",
        "The OpenAPI item cannot be represented by Miya",
        "OpenAPI item '{0}' is skipped: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor OpenApiNameCollision = new(
        "MIYA023",
        "OpenAPI names produce the same C# name",
        "OpenAPI name '{0}' collides with generated C# name '{1}'",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
