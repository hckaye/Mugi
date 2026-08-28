using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Mugi.Generators.Core;

internal sealed class OpenApiJsonCodecModel
{
    internal OpenApiJsonCodecModel(
        string typeName,
        string nonNullableTypeName,
        string codecName,
        JsonTypeKind kind,
        bool isReferenceType)
    {
        TypeName = typeName;
        NonNullableTypeName = nonNullableTypeName;
        CodecName = codecName;
        Kind = kind;
        IsReferenceType = isReferenceType;
        Properties = Array.Empty<OpenApiJsonCodecProperty>();
    }

    internal string TypeName { get; }

    internal string NonNullableTypeName { get; }

    internal string CodecName { get; }

    internal JsonTypeKind Kind { get; }

    internal bool IsReferenceType { get; }

    internal OpenApiJsonCodecModel? ElementType { get; set; }

    internal string? ElementTypeName { get; set; }

    internal bool ElementIsNonNullableReference { get; set; }

    internal OpenApiJsonCodecModel? EnumUnderlyingType { get; set; }

    internal IReadOnlyList<OpenApiJsonCodecProperty> Properties { get; set; }

    internal IReadOnlyList<KeyValuePair<string, string>>? EnumMembers { get; set; }

    internal bool IsNullableValue => Kind == JsonTypeKind.Nullable;

    internal string ValueTypeName => IsReferenceType ? NonNullableTypeName + "?" : TypeName;
}

internal sealed class OpenApiJsonCodecProperty
{
    internal OpenApiJsonCodecProperty(
        string identifier,
        string jsonName,
        string typeName,
        OpenApiJsonCodecModel type,
        bool required,
        bool isPrimary,
        bool isNonNullableReference)
    {
        Identifier = identifier;
        JsonName = jsonName;
        TypeName = typeName;
        Type = type;
        Required = required;
        IsPrimary = isPrimary;
        IsNonNullableReference = isNonNullableReference;
    }

    internal string Identifier { get; }

    internal string JsonName { get; }

    internal string TypeName { get; }

    internal OpenApiJsonCodecModel Type { get; }

    internal bool Required { get; }

    internal bool IsPrimary { get; }

    internal bool IsNonNullableReference { get; }
}

internal sealed class OpenApiJsonCodecModelBuilder
{
    private readonly string _targetNamespace;
    private readonly string _codecPrefix;
    private readonly Dictionary<string, OpenApiJsonCodecModel> _models =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _populatedDeclarations = new(StringComparer.Ordinal);

    internal OpenApiJsonCodecModelBuilder(
        string targetNamespace,
        string codecPrefix)
    {
        _targetNamespace = targetNamespace;
        _codecPrefix = codecPrefix;
    }

    internal ImmutableArray<OpenApiJsonCodecModel> Build(OpenApiImportDocument document)
    {
        foreach (var declaration in document.Declarations)
        {
            Predeclare(declaration);
        }

        foreach (var declaration in document.Declarations)
        {
            AddDeclaration(declaration);
        }

        foreach (var operation in document.ClientOperations)
        {
            if (operation.BodyType is not null)
            {
                _ = AddType(operation.BodyType);
            }

            if (operation.ResponseType is not null)
            {
                _ = AddType(operation.ResponseType);
            }

            foreach (var parameter in operation.Parameters)
            {
                _ = AddType(parameter.Type);
            }
        }

        return _models.Values
            .OrderBy(static model => model.CodecName, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private OpenApiJsonCodecModel AddDeclaration(OpenApiImportDeclaration declaration)
    {
        var key = "named:" + declaration.Name;
        if (_populatedDeclarations.Contains(declaration.Name))
        {
            return _models[key];
        }

        var model = _models[key];

        if (declaration is OpenApiImportEnum enumDeclaration)
        {
            model.EnumMembers = enumDeclaration.Members;
            _populatedDeclarations.Add(declaration.Name);
            return model;
        }

        var record = (OpenApiImportRecord)declaration;
        var properties = new List<OpenApiJsonCodecProperty>(record.Properties.Count);
        foreach (var property in record.Properties)
        {
            var propertyType = AddType(CodecType(property.Type, property.Required));
            properties.Add(new OpenApiJsonCodecProperty(
                property.Identifier,
                property.OpenApiName,
                Render(property.Type, property.Required),
                propertyType,
                property.Required,
                isPrimary: true,
                property.Required && IsNonNullableReference(property.Type)));
        }

        model.Properties = properties;
        _populatedDeclarations.Add(declaration.Name);
        return model;
    }

    private void Predeclare(OpenApiImportDeclaration declaration)
    {
        var key = "named:" + declaration.Name;
        if (_models.ContainsKey(key))
        {
            return;
        }

        var typeName = Qualified(declaration.Name);
        _models.Add(
            key,
            new OpenApiJsonCodecModel(
                typeName,
                typeName,
                CodecName(key),
                declaration is OpenApiImportEnum ? JsonTypeKind.Enum : JsonTypeKind.Object,
                isReferenceType: declaration is OpenApiImportRecord));
    }

    private OpenApiJsonCodecModel AddType(OpenApiImportType type)
    {
        if (type.Nullable && IsReferenceType(type.Kind))
        {
            return AddType(new OpenApiImportType(
                type.Kind,
                type.Name,
                type.ElementType,
                nullable: false));
        }

        if (type.Kind is OpenApiImportTypeKind.Enum or OpenApiImportTypeKind.Object
            && (!type.Nullable || type.Kind == OpenApiImportTypeKind.Object)
            && _models.TryGetValue("named:" + type.Name, out var named))
        {
            return named;
        }

        var key = Key(type);
        if (_models.TryGetValue(key, out var existing))
        {
            return existing;
        }

        if (type.Nullable && IsNullableValue(type.Kind))
        {
            var elementType = AddType(new OpenApiImportType(
                type.Kind,
                type.Name,
                type.ElementType,
                nullable: false));
            var nullable = new OpenApiJsonCodecModel(
                Render(type, required: true),
                Render(type, required: true),
                CodecName(key),
                JsonTypeKind.Nullable,
                isReferenceType: false)
            {
                ElementType = elementType,
            };
            _models.Add(key, nullable);
            return nullable;
        }

        var nonNullable = type.Nullable
            ? new OpenApiImportType(type.Kind, type.Name, type.ElementType, nullable: false)
            : type;
        var model = new OpenApiJsonCodecModel(
            Render(nonNullable, required: true),
            Render(nonNullable, required: true),
            CodecName(key),
            ToJsonKind(nonNullable.Kind),
            IsReferenceType(nonNullable.Kind));
        _models.Add(key, model);

        switch (nonNullable.Kind)
        {
            case OpenApiImportTypeKind.Array:
                model.ElementType = AddType(nonNullable.ElementType!);
                model.ElementTypeName = Render(nonNullable.ElementType!, required: true);
                model.ElementIsNonNullableReference = IsNonNullableReference(nonNullable.ElementType!);
                break;
            case OpenApiImportTypeKind.Enum:
            case OpenApiImportTypeKind.Object:
                if (_models.TryGetValue("named:" + nonNullable.Name, out var declaration))
                {
                    return declaration;
                }

                break;
        }

        return model;
    }

    private static OpenApiImportType CodecType(OpenApiImportType type, bool required)
    {
        if (required || type.Nullable || IsReferenceType(type.Kind))
        {
            return type;
        }

        return new OpenApiImportType(
            type.Kind,
            type.Name,
            type.ElementType,
            nullable: true);
    }

    private string CodecName(string key) =>
        GeneratedNaming.StableIdentifier(_codecPrefix + "_", "Codec_" + key);

    private string Qualified(string name) => "global::" + _targetNamespace + "." + name;

    private string Render(OpenApiImportType type, bool required)
    {
        string result;
        switch (type.Kind)
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
                result = Qualified(type.Name!);
                break;
            case OpenApiImportTypeKind.Array:
                result = Render(type.ElementType!, required: true) + "[]";
                break;
            default:
                throw new InvalidOperationException("Unknown imported OpenAPI type.");
        }

        if (type.Nullable || !required)
        {
            if (!result.EndsWith("?", StringComparison.Ordinal))
            {
                result += "?";
            }
        }

        return result;
    }

    private static string Key(OpenApiImportType type)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append((int)type.Kind);
        builder.Append(':');
        builder.Append(type.Name);
        builder.Append(':');
        builder.Append(type.Nullable ? 'N' : 'R');
        if (type.ElementType is not null)
        {
            builder.Append('[');
            builder.Append(Key(type.ElementType));
            builder.Append(']');
        }

        return builder.ToString();
    }

    private static JsonTypeKind ToJsonKind(OpenApiImportTypeKind kind) => kind switch
    {
        OpenApiImportTypeKind.String => JsonTypeKind.String,
        OpenApiImportTypeKind.Int32 => JsonTypeKind.Int32,
        OpenApiImportTypeKind.Int64 => JsonTypeKind.Int64,
        OpenApiImportTypeKind.Single => JsonTypeKind.Single,
        OpenApiImportTypeKind.Double => JsonTypeKind.Double,
        OpenApiImportTypeKind.Decimal => JsonTypeKind.Decimal,
        OpenApiImportTypeKind.Boolean => JsonTypeKind.Boolean,
        OpenApiImportTypeKind.Enum => JsonTypeKind.Enum,
        OpenApiImportTypeKind.Object => JsonTypeKind.Object,
        OpenApiImportTypeKind.Array => JsonTypeKind.Array,
        _ => throw new InvalidOperationException("Unknown imported OpenAPI type."),
    };

    private static bool IsNullableValue(OpenApiImportTypeKind kind) => kind is
        OpenApiImportTypeKind.Int32
        or OpenApiImportTypeKind.Int64
        or OpenApiImportTypeKind.Single
        or OpenApiImportTypeKind.Double
        or OpenApiImportTypeKind.Decimal
        or OpenApiImportTypeKind.Boolean
        or OpenApiImportTypeKind.Enum;

    private static bool IsReferenceType(OpenApiImportTypeKind kind) => kind is
        OpenApiImportTypeKind.String
        or OpenApiImportTypeKind.Object
        or OpenApiImportTypeKind.Array;

    private static bool IsNonNullableReference(OpenApiImportType type) =>
        IsReferenceType(type.Kind) && !type.Nullable;
}
