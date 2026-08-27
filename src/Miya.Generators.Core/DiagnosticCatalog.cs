using Microsoft.CodeAnalysis;

namespace Miya.Generators.Core;

internal static class DiagnosticCatalog
{
    private const string Category = "Miya.Generators";

    internal static readonly DiagnosticDescriptor AnonymousJsonType = new(
        "MIYA001",
        "Anonymous JSON types are not supported",
        "The anonymous type '{0}' cannot have a MiyaJson codec generated",
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
        "A MiyaJson codec cannot be generated for '{0}': {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
