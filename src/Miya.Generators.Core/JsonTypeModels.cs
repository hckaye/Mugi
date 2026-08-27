using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Miya.Generators.Core;

internal enum JsonTypeKind
{
    Boolean,
    Byte,
    SByte,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Single,
    Double,
    Decimal,
    Char,
    String,
    Guid,
    DateTime,
    DateTimeOffset,
    Enum,
    Nullable,
    Array,
    List,
    Dictionary,
    Object,
}

internal sealed class JsonTypeModel
{
    internal JsonTypeModel(ITypeSymbol type, JsonTypeKind kind)
    {
        Type = type;
        Kind = kind;
        Properties = ImmutableArray<JsonPropertyModel>.Empty;
        PrimaryProperties = ImmutableArray<JsonPropertyModel>.Empty;
    }

    internal ITypeSymbol Type { get; }

    internal JsonTypeKind Kind { get; }

    internal ITypeSymbol? ElementType { get; set; }

    internal ITypeSymbol? DictionaryValueType { get; set; }

    internal ITypeSymbol? EnumUnderlyingType { get; set; }

    internal ImmutableArray<JsonPropertyModel> Properties { get; set; }

    internal ImmutableArray<JsonPropertyModel> PrimaryProperties { get; set; }
}

internal sealed class JsonPropertyModel
{
    internal JsonPropertyModel(IPropertySymbol property, bool isPrimary)
    {
        Property = property;
        IsPrimary = isPrimary;
    }

    internal IPropertySymbol Property { get; }

    internal bool IsPrimary { get; }
}

internal sealed class JsonTypeGraph
{
    internal JsonTypeGraph(ImmutableArray<JsonTypeModel> models)
    {
        Models = models;
    }

    internal ImmutableArray<JsonTypeModel> Models { get; }
}

internal static class JsonTypeGraphBuilder
{
    internal static bool TryBuild(ITypeSymbol root, out JsonTypeGraph? graph, out string? error)
    {
        var models = new Dictionary<ITypeSymbol, JsonTypeModel>(SymbolEqualityComparer.Default);
        if (!TryAdd(root, models, out error))
        {
            graph = null;
            return false;
        }

        var ordered = models.Values
            .OrderBy(static model => TypeNames.Key(model.Type), StringComparer.Ordinal)
            .ToImmutableArray();
        graph = new JsonTypeGraph(ordered);
        return true;
    }

    private static bool TryAdd(
        ITypeSymbol type,
        Dictionary<ITypeSymbol, JsonTypeModel> models,
        out string? error)
    {
        error = null;
        if (type.TypeKind == TypeKind.Error)
        {
            error = "the type could not be resolved";
            return false;
        }

        if (type is ITypeParameterSymbol || ContainsTypeParameter(type))
        {
            error = "the type must be closed and cannot contain type parameters";
            return false;
        }

        if (type.IsAnonymousType)
        {
            error = "anonymous types are not supported";
            return false;
        }

        if (models.ContainsKey(type))
        {
            return true;
        }

        if (!TryClassify(type, out var kind, out var elementType, out var dictionaryValueType, out error))
        {
            return false;
        }

        var model = new JsonTypeModel(type, kind)
        {
            ElementType = elementType,
            DictionaryValueType = dictionaryValueType,
        };
        models.Add(type, model);

        if (kind == JsonTypeKind.Enum)
        {
            model.EnumUnderlyingType = ((INamedTypeSymbol)type).EnumUnderlyingType;
            return TryAdd(model.EnumUnderlyingType!, models, out error);
        }

        if (kind == JsonTypeKind.Nullable || kind == JsonTypeKind.Array || kind == JsonTypeKind.List)
        {
            return TryAdd(elementType!, models, out error);
        }

        if (kind == JsonTypeKind.Dictionary)
        {
            return TryAdd(dictionaryValueType!, models, out error);
        }

        if (kind != JsonTypeKind.Object)
        {
            return true;
        }

        var namedType = (INamedTypeSymbol)type;
        if (!TryBuildObject(namedType, out var properties, out var primaryProperties, out error))
        {
            return false;
        }

        model.Properties = properties;
        model.PrimaryProperties = primaryProperties;
        foreach (var property in properties)
        {
            if (!TryAdd(property.Property.Type, models, out error))
            {
                error = "property '" + property.Property.Name + "' uses an unsupported type: " + error;
                return false;
            }
        }

        return true;
    }

    private static bool TryClassify(
        ITypeSymbol type,
        out JsonTypeKind kind,
        out ITypeSymbol? elementType,
        out ITypeSymbol? dictionaryValueType,
        out string? error)
    {
        elementType = null;
        dictionaryValueType = null;
        error = null;
        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean:
                kind = JsonTypeKind.Boolean;
                return true;
            case SpecialType.System_Byte:
                kind = JsonTypeKind.Byte;
                return true;
            case SpecialType.System_SByte:
                kind = JsonTypeKind.SByte;
                return true;
            case SpecialType.System_Int16:
                kind = JsonTypeKind.Int16;
                return true;
            case SpecialType.System_UInt16:
                kind = JsonTypeKind.UInt16;
                return true;
            case SpecialType.System_Int32:
                kind = JsonTypeKind.Int32;
                return true;
            case SpecialType.System_UInt32:
                kind = JsonTypeKind.UInt32;
                return true;
            case SpecialType.System_Int64:
                kind = JsonTypeKind.Int64;
                return true;
            case SpecialType.System_UInt64:
                kind = JsonTypeKind.UInt64;
                return true;
            case SpecialType.System_Single:
                kind = JsonTypeKind.Single;
                return true;
            case SpecialType.System_Double:
                kind = JsonTypeKind.Double;
                return true;
            case SpecialType.System_Decimal:
                kind = JsonTypeKind.Decimal;
                return true;
            case SpecialType.System_Char:
                kind = JsonTypeKind.Char;
                return true;
            case SpecialType.System_String:
                kind = JsonTypeKind.String;
                return true;
            case SpecialType.System_Object:
                kind = default;
                error = "System.Object is not supported";
                return false;
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            kind = JsonTypeKind.Enum;
            return true;
        }

        if (type is IArrayTypeSymbol array)
        {
            if (array.Rank != 1 || !array.IsSZArray)
            {
                kind = default;
                error = "only single-dimensional zero-based arrays are supported";
                return false;
            }

            kind = JsonTypeKind.Array;
            elementType = array.ElementType;
            return true;
        }

        if (!(type is INamedTypeSymbol named))
        {
            kind = default;
            error = "only named types and one-dimensional arrays are supported";
            return false;
        }

        var metadataName = InvocationAnalyzer.GetMetadataName(named.OriginalDefinition);
        if (metadataName == "System.Guid")
        {
            kind = JsonTypeKind.Guid;
            return true;
        }

        if (metadataName == "System.DateTime")
        {
            kind = JsonTypeKind.DateTime;
            return true;
        }

        if (metadataName == "System.DateTimeOffset")
        {
            kind = JsonTypeKind.DateTimeOffset;
            return true;
        }

        if (metadataName == "System.Nullable`1")
        {
            kind = JsonTypeKind.Nullable;
            elementType = named.TypeArguments[0];
            return true;
        }

        if (metadataName == "System.Collections.Generic.List`1")
        {
            kind = JsonTypeKind.List;
            elementType = named.TypeArguments[0];
            return true;
        }

        if (metadataName == "System.Collections.Generic.Dictionary`2")
        {
            if (named.TypeArguments[0].SpecialType != SpecialType.System_String)
            {
                kind = default;
                error = "Dictionary keys must be strings";
                return false;
            }

            kind = JsonTypeKind.Dictionary;
            dictionaryValueType = named.TypeArguments[1];
            return true;
        }

        if (type.TypeKind == TypeKind.Interface)
        {
            kind = default;
            error = "interfaces and polymorphic contracts are not supported";
            return false;
        }

        if (type.TypeKind != TypeKind.Class && type.TypeKind != TypeKind.Struct)
        {
            kind = default;
            error = "the type kind is not supported";
            return false;
        }

        if (named.IsRefLikeType)
        {
            kind = default;
            error = "ref-like types are not supported";
            return false;
        }

        kind = JsonTypeKind.Object;
        return true;
    }

    private static bool TryBuildObject(
        INamedTypeSymbol type,
        out ImmutableArray<JsonPropertyModel> properties,
        out ImmutableArray<JsonPropertyModel> primaryProperties,
        out string? error)
    {
        properties = ImmutableArray<JsonPropertyModel>.Empty;
        primaryProperties = ImmutableArray<JsonPropertyModel>.Empty;
        error = null;
        if (!IsAccessible(type))
        {
            error = "the type and all containing types must be public or internal";
            return false;
        }

        if (type.IsAbstract)
        {
            error = "abstract types and polymorphic contracts are not supported";
            return false;
        }

        if (type.BaseType is not null && type.BaseType.SpecialType != SpecialType.System_Object
            && type.TypeKind == TypeKind.Class)
        {
            error = "class inheritance and polymorphic contracts are not supported";
            return false;
        }

        var primaryNames = new List<string>();
        if (type.IsRecord)
        {
            var recordSyntax = type.DeclaringSyntaxReferences
                .Select(static reference => reference.GetSyntax())
                .OfType<RecordDeclarationSyntax>()
                .FirstOrDefault(static syntax => syntax.ParameterList is not null);
            if (recordSyntax is null)
            {
                error = "records must declare a primary constructor";
                return false;
            }

            foreach (var parameter in recordSyntax.ParameterList!.Parameters)
            {
                primaryNames.Add(parameter.Identifier.ValueText);
            }
        }
        else if (type.TypeKind == TypeKind.Class && !HasAccessibleParameterlessConstructor(type))
        {
            error = "POCO classes must have a public or internal parameterless constructor";
            return false;
        }

        var candidates = type.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(static property => !property.IsStatic && !property.IsIndexer)
            .Where(static property => IsAccessibleMember(property.DeclaredAccessibility))
            .OrderBy(static property => SourceOrder(property))
            .ThenBy(static property => property.Name, StringComparer.Ordinal)
            .ToList();

        var primaryBuilder = ImmutableArray.CreateBuilder<JsonPropertyModel>();
        foreach (var primaryName in primaryNames)
        {
            var property = candidates.FirstOrDefault(candidate => candidate.Name == primaryName);
            if (property is null || property.GetMethod is null || !IsAccessibleMember(property.GetMethod.DeclaredAccessibility))
            {
                error = "primary constructor parameter '" + primaryName + "' has no accessible property";
                return false;
            }

            primaryBuilder.Add(new JsonPropertyModel(property, true));
        }

        var propertyBuilder = ImmutableArray.CreateBuilder<JsonPropertyModel>();
        foreach (var primary in primaryBuilder)
        {
            propertyBuilder.Add(primary);
        }

        foreach (var property in candidates)
        {
            if (primaryNames.Contains(property.Name))
            {
                continue;
            }

            if (property.GetMethod is null || !IsAccessibleMember(property.GetMethod.DeclaredAccessibility)
                || property.SetMethod is null || !IsAccessibleMember(property.SetMethod.DeclaredAccessibility))
            {
                error = "property '" + property.Name + "' must have accessible get and set/init accessors";
                return false;
            }

            propertyBuilder.Add(new JsonPropertyModel(property, false));
        }

        properties = propertyBuilder.ToImmutable();
        primaryProperties = primaryBuilder.ToImmutable();
        return true;
    }

    private static bool ContainsTypeParameter(ITypeSymbol type)
    {
        if (type is ITypeParameterSymbol)
        {
            return true;
        }

        if (type is IArrayTypeSymbol array)
        {
            return ContainsTypeParameter(array.ElementType);
        }

        if (type is INamedTypeSymbol named)
        {
            return named.TypeArguments.Any(ContainsTypeParameter);
        }

        return false;
    }

    private static bool HasAccessibleParameterlessConstructor(INamedTypeSymbol type)
    {
        return type.InstanceConstructors.Any(static constructor =>
            constructor.Parameters.Length == 0
            && IsAccessibleMember(constructor.DeclaredAccessibility));
    }

    private static bool IsAccessible(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.ContainingType)
        {
            if (!IsAccessibleMember(current.DeclaredAccessibility))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAccessibleMember(Accessibility accessibility)
    {
        return accessibility == Accessibility.Public
            || accessibility == Accessibility.Internal
            || accessibility == Accessibility.ProtectedOrInternal;
    }

    private static int SourceOrder(ISymbol symbol)
    {
        foreach (var location in symbol.Locations)
        {
            if (location.IsInSource)
            {
                return location.SourceSpan.Start;
            }
        }

        return int.MaxValue;
    }
}

internal static class TypeNames
{
    private static readonly SymbolDisplayFormat DisplayFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions:
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    internal static string Display(ITypeSymbol type) => type.ToDisplayString(DisplayFormat);

    internal static string NonNullableDisplay(ITypeSymbol type) =>
        type.IsReferenceType
            ? type.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(DisplayFormat)
            : Display(type);

    internal static string Key(ITypeSymbol type) => type.ToDisplayString(DisplayFormat);

    internal static string CodecName(ITypeSymbol type)
    {
        var normalized = type.IsReferenceType && type.NullableAnnotation == NullableAnnotation.Annotated
            ? type.WithNullableAnnotation(NullableAnnotation.NotAnnotated)
            : type;
        return GeneratedNaming.StableIdentifier("Codec_", Key(normalized));
    }
}
