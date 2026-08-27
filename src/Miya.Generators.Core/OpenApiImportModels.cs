using System;
using System.Collections.Generic;

namespace Miya.Generators.Core;

internal sealed class OpenApiImportInput
{
    internal OpenApiImportInput(
        string path,
        string content,
        string targetNamespace,
        JsonNaming naming)
    {
        Path = path;
        Content = content;
        TargetNamespace = targetNamespace;
        Naming = naming;
    }

    internal string Path { get; }

    internal string Content { get; }

    internal string TargetNamespace { get; }

    internal JsonNaming Naming { get; }
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
                && x.Naming == y.Naming);
    }

    public int GetHashCode(OpenApiImportInput obj)
    {
        unchecked
        {
            var result = StringComparer.Ordinal.GetHashCode(obj.Path);
            result = (result * 397) ^ StringComparer.Ordinal.GetHashCode(obj.Content);
            result = (result * 397) ^ StringComparer.Ordinal.GetHashCode(obj.TargetNamespace);
            return (result * 397) ^ (int)obj.Naming;
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
