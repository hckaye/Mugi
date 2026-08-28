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
    internal JsonPropertyModel(
        IPropertySymbol property,
        bool isPrimary,
        IParameterSymbol? primaryParameter = null)
    {
        Property = property;
        IsPrimary = isPrimary;
        PrimaryParameter = primaryParameter;
    }

    internal IPropertySymbol Property { get; }

    internal bool IsPrimary { get; }

    internal IParameterSymbol? PrimaryParameter { get; }

    internal bool RequiresPresence =>
        Property.IsRequired
        || (PrimaryParameter is not null && !PrimaryParameter.HasExplicitDefaultValue);
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

        var candidates = type.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(static property => !property.IsStatic && !property.IsIndexer)
            .Where(static property => IsAccessibleMember(property.DeclaredAccessibility))
            .OrderBy(static property => SourceOrder(property))
            .ThenBy(static property => property.Name, StringComparer.Ordinal)
            .ToList();

        var primaryParameters = ImmutableArray<IParameterSymbol>.Empty;
        if (type.IsRecord)
        {
            var recordDeclarations = type.DeclaringSyntaxReferences
                .Select(static reference => reference.GetSyntax())
                .OfType<RecordDeclarationSyntax>()
                .ToList();
            var recordSyntax = recordDeclarations
                .FirstOrDefault(static syntax => syntax.ParameterList is not null);
            IMethodSymbol? primaryConstructor;
            if (recordSyntax is not null)
            {
                var primaryNames = recordSyntax.ParameterList!.Parameters
                    .Select(static parameter => parameter.Identifier.ValueText)
                    .ToImmutableArray();
                primaryConstructor = FindSourceRecordConstructor(type, candidates, primaryNames);
            }
            else if (recordDeclarations.Count == 0)
            {
                primaryConstructor = FindMetadataRecordConstructor(type, candidates, out var ambiguous);
                if (ambiguous)
                {
                    error = "metadata record primary constructor is ambiguous";
                    return false;
                }
            }
            else
            {
                primaryConstructor = null;
            }

            if (primaryConstructor is null)
            {
                error = "records must declare a primary constructor";
                return false;
            }

            primaryParameters = primaryConstructor.Parameters;
        }
        else if (type.TypeKind == TypeKind.Class && !HasAccessibleParameterlessConstructor(type))
        {
            error = "POCO classes must have a public or internal parameterless constructor";
            return false;
        }

        var primaryBuilder = ImmutableArray.CreateBuilder<JsonPropertyModel>();
        foreach (var parameter in primaryParameters)
        {
            var property = candidates.FirstOrDefault(candidate =>
                candidate.Name == parameter.Name
                && SymbolEqualityComparer.Default.Equals(candidate.Type, parameter.Type));
            if (property is null || property.GetMethod is null || !IsAccessibleMember(property.GetMethod.DeclaredAccessibility))
            {
                error = "primary constructor parameter '" + parameter.Name + "' has no accessible property";
                return false;
            }

            primaryBuilder.Add(new JsonPropertyModel(property, true, parameter));
        }

        var propertyBuilder = ImmutableArray.CreateBuilder<JsonPropertyModel>();
        foreach (var primary in primaryBuilder)
        {
            propertyBuilder.Add(primary);
        }

        foreach (var property in candidates)
        {
            if (primaryParameters.Any(parameter => parameter.Name == property.Name))
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

    private static IMethodSymbol? FindSourceRecordConstructor(
        INamedTypeSymbol type,
        IReadOnlyList<IPropertySymbol> properties,
        ImmutableArray<string> primaryNames)
    {
        foreach (var constructor in type.InstanceConstructors)
        {
            if (!IsAccessibleMember(constructor.DeclaredAccessibility)
                || constructor.Parameters.Length != primaryNames.Length
                || IsRecordCopyConstructor(type, constructor)
                || !ParametersMatchProperties(constructor.Parameters, properties))
            {
                continue;
            }

            var matches = true;
            for (var index = 0; index < primaryNames.Length; index++)
            {
                if (!string.Equals(constructor.Parameters[index].Name, primaryNames[index], StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return constructor;
            }
        }

        return null;
    }

    private static IMethodSymbol? FindMetadataRecordConstructor(
        INamedTypeSymbol type,
        IReadOnlyList<IPropertySymbol> properties,
        out bool ambiguous)
    {
        ambiguous = false;
        var positionalShapes = type.GetMembers("Deconstruct")
            .OfType<IMethodSymbol>()
            .Where(static method =>
                !method.IsStatic
                && method.ReturnsVoid
                && method.Parameters.All(static parameter => parameter.RefKind == RefKind.Out))
            .ToList();
        var matches = new List<IMethodSymbol>();
        foreach (var constructor in type.InstanceConstructors)
        {
            if (constructor.DeclaredAccessibility != Accessibility.Public
                || IsRecordCopyConstructor(type, constructor)
                || !ParametersMatchPropertiesOneToOne(constructor.Parameters, properties)
                || !positionalShapes.Any(shape => ParametersMatchPositionalShape(
                    constructor.Parameters,
                    shape.Parameters)))
            {
                continue;
            }

            matches.Add(constructor);
        }

        ambiguous = matches.Count > 1;
        return matches.Count == 1 ? matches[0] : null;
    }

    private static bool ParametersMatchPropertiesOneToOne(
        ImmutableArray<IParameterSymbol> parameters,
        IReadOnlyList<IPropertySymbol> properties)
    {
        var matchedProperties = new HashSet<IPropertySymbol>(SymbolEqualityComparer.Default);
        foreach (var parameter in parameters)
        {
            var property = properties.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, parameter.Name, StringComparison.Ordinal)
                && SymbolEqualityComparer.Default.Equals(candidate.Type, parameter.Type)
                && candidate.GetMethod is not null
                && candidate.SetMethod?.IsInitOnly == true);
            if (property is null || !matchedProperties.Add(property))
            {
                return false;
            }
        }

        return matchedProperties.Count == parameters.Length;
    }

    private static bool ParametersMatchPositionalShape(
        ImmutableArray<IParameterSymbol> constructorParameters,
        ImmutableArray<IParameterSymbol> deconstructParameters)
    {
        if (constructorParameters.Length != deconstructParameters.Length)
        {
            return false;
        }

        for (var index = 0; index < constructorParameters.Length; index++)
        {
            if (!string.Equals(
                    constructorParameters[index].Name,
                    deconstructParameters[index].Name,
                    StringComparison.Ordinal)
                || !SymbolEqualityComparer.Default.Equals(
                    constructorParameters[index].Type,
                    deconstructParameters[index].Type))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ParametersMatchProperties(
        ImmutableArray<IParameterSymbol> parameters,
        IReadOnlyList<IPropertySymbol> properties)
    {
        foreach (var parameter in parameters)
        {
            var matched = false;
            foreach (var property in properties)
            {
                if (string.Equals(property.Name, parameter.Name, StringComparison.Ordinal)
                    && SymbolEqualityComparer.Default.Equals(property.Type, parameter.Type))
                {
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsRecordCopyConstructor(INamedTypeSymbol type, IMethodSymbol constructor) =>
        constructor.Parameters.Length == 1
        && SymbolEqualityComparer.Default.Equals(constructor.Parameters[0].Type, type);

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
