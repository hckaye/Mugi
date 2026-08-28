using System;
using System.Collections.Generic;

namespace Mugi.Generators.Core;

internal sealed class OpenApiImportInput
{
    internal OpenApiImportInput(
        string path,
        string content,
        string targetNamespace,
        JsonNaming naming,
        string? clientName = null,
        bool serverImport = false)
    {
        Path = path;
        Content = content;
        TargetNamespace = targetNamespace;
        Naming = naming;
        ClientName = clientName;
        ServerImport = serverImport;
    }

    internal string Path { get; }

    internal string Content { get; }

    internal string TargetNamespace { get; }

    internal JsonNaming Naming { get; }

    internal string? ClientName { get; }

    internal bool ServerImport { get; }
}

internal sealed class OpenApiImportInputComparer : IEqualityComparer<OpenApiImportInput>
{
    internal static readonly OpenApiImportInputComparer Instance = new();

    public bool Equals(OpenApiImportInput? x, OpenApiImportInput? y)
    {
        return ReferenceEquals(x, y)
            || (x is not null && y is not null
                && string.Equals(x.Path, y.Path, StringComparison.Ordinal)
                && string.Equals(x.Content, y.Content, StringComparison.Ordinal)
                && string.Equals(x.TargetNamespace, y.TargetNamespace, StringComparison.Ordinal)
                && x.Naming == y.Naming
                && string.Equals(x.ClientName, y.ClientName, StringComparison.Ordinal)
                && x.ServerImport == y.ServerImport);
    }

    public int GetHashCode(OpenApiImportInput obj)
    {
        unchecked
        {
            var result = StringComparer.Ordinal.GetHashCode(obj.Path);
            result = (result * 397) ^ StringComparer.Ordinal.GetHashCode(obj.Content);
            result = (result * 397) ^ StringComparer.Ordinal.GetHashCode(obj.TargetNamespace);
            result = (result * 397) ^ (int)obj.Naming;
            result = (result * 397) ^ (obj.ClientName is null
                ? 0
                : StringComparer.Ordinal.GetHashCode(obj.ClientName));
            return (result * 397) ^ (obj.ServerImport ? 1 : 0);
        }
    }
}

internal enum OpenApiImportTypeKind
{
    String,
    Int32,
    Int64,
    Single,
    Double,
    Decimal,
    Boolean,
    Enum,
    Object,
    Array,
}

internal sealed class OpenApiImportType
{
    internal OpenApiImportType(
        OpenApiImportTypeKind kind,
        string? name = null,
        OpenApiImportType? elementType = null,
        bool nullable = false)
    {
        Kind = kind;
        Name = name;
        ElementType = elementType;
        Nullable = nullable;
    }

    internal OpenApiImportTypeKind Kind { get; }

    internal string? Name { get; }

    internal OpenApiImportType? ElementType { get; }

    internal bool Nullable { get; }

    internal string Render(bool required)
    {
        string result;
        switch (Kind)
        {
            case OpenApiImportTypeKind.String:
                result = "string";
                break;
            case OpenApiImportTypeKind.Int32:
                result = "int";
                break;
            case OpenApiImportTypeKind.Int64:
                result = "long";
                break;
            case OpenApiImportTypeKind.Single:
                result = "float";
                break;
            case OpenApiImportTypeKind.Double:
                result = "double";
                break;
            case OpenApiImportTypeKind.Decimal:
                result = "decimal";
                break;
            case OpenApiImportTypeKind.Boolean:
                result = "bool";
                break;
            case OpenApiImportTypeKind.Enum:
            case OpenApiImportTypeKind.Object:
                result = Name!;
                break;
            case OpenApiImportTypeKind.Array:
                result = ElementType!.Render(required: true) + "[]";
                break;
            default:
                throw new InvalidOperationException("Unknown imported OpenAPI type.");
        }

        return Nullable || !required ? result + "?" : result;
    }

    internal IEnumerable<string> NamedDependencies()
    {
        if (Kind is OpenApiImportTypeKind.Enum or OpenApiImportTypeKind.Object)
        {
            yield return Name!;
        }
        else if (Kind == OpenApiImportTypeKind.Array)
        {
            foreach (var dependency in ElementType!.NamedDependencies())
            {
                yield return dependency;
            }
        }
    }
}

internal sealed class OpenApiImportProperty
{
    internal OpenApiImportProperty(
        string openApiName,
        string identifier,
        OpenApiImportType type,
        bool required,
        string source,
        string? headerName,
        IReadOnlyList<string> rules)
    {
        OpenApiName = openApiName;
        Identifier = identifier;
        Type = type;
        Required = required;
        Source = source;
        HeaderName = headerName;
        Rules = rules;
    }

    internal string OpenApiName { get; }

    internal string Identifier { get; }

    internal OpenApiImportType Type { get; }

    internal bool Required { get; }

    internal string Source { get; }

    internal string? HeaderName { get; }

    internal IReadOnlyList<string> Rules { get; }
}

internal abstract class OpenApiImportDeclaration
{
    protected OpenApiImportDeclaration(string name, HashSet<string> dependencies)
    {
        Name = name;
        Dependencies = dependencies;
    }

    internal string Name { get; }

    internal HashSet<string> Dependencies { get; }
}

internal sealed class OpenApiImportRecord : OpenApiImportDeclaration
{
    internal OpenApiImportRecord(
        string name,
        IReadOnlyList<OpenApiImportProperty> properties,
        HashSet<string> dependencies)
        : base(name, dependencies)
    {
        Properties = properties;
    }

    internal IReadOnlyList<OpenApiImportProperty> Properties { get; }
}

internal sealed class OpenApiImportEnum : OpenApiImportDeclaration
{
    internal OpenApiImportEnum(
        string name,
        IReadOnlyList<KeyValuePair<string, string>> members)
        : base(name, new HashSet<string>(StringComparer.Ordinal))
    {
        Members = members;
    }

    internal IReadOnlyList<KeyValuePair<string, string>> Members { get; }
}

internal sealed class OpenApiImportOperation
{
    internal OpenApiImportOperation(
        string name,
        string inputName,
        IReadOnlyList<OpenApiImportProperty> fields,
        HashSet<string> dependencies)
    {
        Name = name;
        InputName = inputName;
        Fields = fields;
        Dependencies = dependencies;
    }

    internal string Name { get; }

    internal string InputName { get; }

    internal IReadOnlyList<OpenApiImportProperty> Fields { get; }

    internal HashSet<string> Dependencies { get; }
}

internal sealed class OpenApiClientOperation
{
    internal OpenApiClientOperation(
        string name,
        string method,
        string path,
        IReadOnlyList<OpenApiImportProperty> parameters,
        OpenApiImportType? bodyType,
        bool bodyRequired,
        OpenApiImportType? responseType,
        IReadOnlyList<string> jsonResponseStatuses,
        IReadOnlyList<string> noBodyResponseStatuses,
        HashSet<string> dependencies)
    {
        Name = name;
        Method = method;
        Path = path;
        Parameters = parameters;
        BodyType = bodyType;
        BodyRequired = bodyRequired;
        ResponseType = responseType;
        JsonResponseStatuses = jsonResponseStatuses;
        NoBodyResponseStatuses = noBodyResponseStatuses;
        Dependencies = dependencies;
    }

    internal string Name { get; }

    internal string Method { get; }

    internal string Path { get; }

    internal IReadOnlyList<OpenApiImportProperty> Parameters { get; }

    internal OpenApiImportType? BodyType { get; }

    internal bool BodyRequired { get; }

    internal OpenApiImportType? ResponseType { get; }

    internal IReadOnlyList<string> JsonResponseStatuses { get; }

    internal IReadOnlyList<string> NoBodyResponseStatuses { get; }

    internal HashSet<string> Dependencies { get; }
}

internal sealed class OpenApiImportDocument
{
    internal OpenApiImportDocument(
        string targetNamespace,
        string? title,
        IReadOnlyList<OpenApiImportDeclaration> declarations,
        IReadOnlyList<OpenApiImportOperation> operations,
        IReadOnlyList<KeyValuePair<string, string>> paths,
        IReadOnlyList<OpenApiClientOperation> clientOperations,
        IReadOnlyCollection<string> componentTypeNames,
        bool reuseComponentDeclarations)
    {
        TargetNamespace = targetNamespace;
        Title = title;
        Declarations = declarations;
        Operations = operations;
        Paths = paths;
        ClientOperations = clientOperations;
        ComponentTypeNames = componentTypeNames;
        ReuseComponentDeclarations = reuseComponentDeclarations;
    }

    internal string TargetNamespace { get; }

    internal string? Title { get; }

    internal IReadOnlyList<OpenApiImportDeclaration> Declarations { get; }

    internal IReadOnlyList<OpenApiImportOperation> Operations { get; }

    internal IReadOnlyList<KeyValuePair<string, string>> Paths { get; }

    internal IReadOnlyList<OpenApiClientOperation> ClientOperations { get; }

    internal IReadOnlyCollection<string> ComponentTypeNames { get; }

    internal bool ReuseComponentDeclarations { get; }
}

internal sealed class OpenApiDocumentBuildResult
{
    internal OpenApiDocumentBuildResult(
        OpenApiImportDocument? document,
        string? source,
        System.Collections.Immutable.ImmutableArray<Microsoft.CodeAnalysis.Diagnostic> diagnostics)
    {
        Document = document;
        Source = source;
        Diagnostics = diagnostics;
    }

    internal OpenApiImportDocument? Document { get; }

    internal string? Source { get; }

    internal System.Collections.Immutable.ImmutableArray<Microsoft.CodeAnalysis.Diagnostic> Diagnostics { get; }
}

internal enum OpenApiGenerationMode
{
    Import,
    Client,
}
