using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Miya.Generators.Core;

internal interface IJsonCodecModel
{
    string TypeName { get; }

    string DisplayTypeName { get; }

    string ValueTypeName { get; }

    string CodecName { get; }

    JsonTypeKind Kind { get; }

    bool IsReferenceType { get; }

    IJsonCodecModel? ElementType { get; }

    string? ElementTypeName { get; }

    bool ElementIsNonNullableReference { get; }

    IJsonCodecModel? DictionaryValueType { get; }

    IJsonCodecModel? EnumUnderlyingType { get; }

    IReadOnlyList<IJsonCodecProperty> Properties { get; }

    IReadOnlyList<IJsonCodecProperty> PrimaryProperties { get; }

    IReadOnlyList<KeyValuePair<string, string>>? EnumMembers { get; }
}

internal interface IJsonCodecProperty
{
    string Identifier { get; }

    string JsonName { get; }

    string TypeName { get; }

    IJsonCodecModel Type { get; }

    bool Required { get; }

    string InitialValue { get; }

    bool IsPrimary { get; }

    bool IsNonNullableReference { get; }
}

internal sealed class RoslynJsonCodecModelAdapter : IJsonCodecModel
{
    private readonly JsonTypeModel _model;
    private readonly IReadOnlyDictionary<ITypeSymbol, IJsonCodecModel> _models;
    private readonly GeneratorSettings _settings;
    private IReadOnlyList<IJsonCodecProperty>? _properties;
    private IReadOnlyList<IJsonCodecProperty>? _primaryProperties;

    private RoslynJsonCodecModelAdapter(
        JsonTypeModel model,
        IReadOnlyDictionary<ITypeSymbol, IJsonCodecModel> models,
        GeneratorSettings settings)
    {
        _model = model;
        _models = models;
        _settings = settings;
    }

    internal static ImmutableArray<IJsonCodecModel> Create(
        ImmutableArray<JsonTypeModel> models,
        GeneratorSettings settings)
    {
        var allModels = new Dictionary<ITypeSymbol, JsonTypeModel>(SymbolEqualityComparer.Default);
        foreach (var model in models)
        {
            if (JsonTypeGraphBuilder.TryBuild(model.Type, out var graph, out _)
                && graph is not null)
            {
                foreach (var graphModel in graph.Models)
                {
                    allModels[graphModel.Type] = graphModel;
                }
            }
            else
            {
                allModels[model.Type] = model;
            }
        }

        var adapters = new Dictionary<ITypeSymbol, IJsonCodecModel>(SymbolEqualityComparer.Default);
        foreach (var model in allModels.Values)
        {
            adapters.Add(model.Type, null!);
        }

        foreach (var model in allModels.Values)
        {
            adapters[model.Type] = new RoslynJsonCodecModelAdapter(model, adapters, settings);
        }

        var result = ImmutableArray.CreateBuilder<IJsonCodecModel>(models.Length);
        foreach (var model in allModels.Values)
        {
            result.Add(adapters[model.Type]);
        }

        return result.ToImmutable();
    }

    public string TypeName => TypeNames.NonNullableDisplay(_model.Type);

    public string DisplayTypeName => TypeNames.Display(_model.Type);

    internal ITypeSymbol Symbol => _model.Type;

    public string ValueTypeName => _model.Type.IsReferenceType ? TypeName + "?" : TypeName;

    public string CodecName => TypeNames.CodecName(_model.Type);

    public JsonTypeKind Kind => _model.Kind;

    public bool IsReferenceType => _model.Type.IsReferenceType;

    public IJsonCodecModel? ElementType => _model.ElementType is null
        ? null
        : _models[_model.ElementType];

    public string? ElementTypeName => _model.ElementType is null
        ? null
        : TypeNames.Display(_model.ElementType);

    public bool ElementIsNonNullableReference => false;

    public IJsonCodecModel? DictionaryValueType => _model.DictionaryValueType is null
        ? null
        : _models[_model.DictionaryValueType];

    public IJsonCodecModel? EnumUnderlyingType => _model.EnumUnderlyingType is null
        ? null
        : _models[_model.EnumUnderlyingType];

    public IReadOnlyList<IJsonCodecProperty> Properties => _properties ??= AdaptProperties(_model.Properties);

    public IReadOnlyList<IJsonCodecProperty> PrimaryProperties =>
        _primaryProperties ??= AdaptProperties(_model.PrimaryProperties);

    public IReadOnlyList<KeyValuePair<string, string>>? EnumMembers => null;

    private IReadOnlyList<IJsonCodecProperty> AdaptProperties(
        ImmutableArray<JsonPropertyModel> properties)
    {
        var result = new IJsonCodecProperty[properties.Length];
        for (var index = 0; index < properties.Length; index++)
        {
            var property = properties[index].Property;
            result[index] = new RoslynJsonCodecPropertyAdapter(
                properties[index],
                _models[property.Type],
                GeneratedNaming.JsonPropertyName(property.Name, _settings.Naming));
        }

        return result;
    }
}

internal sealed class RoslynJsonCodecPropertyAdapter : IJsonCodecProperty
{
    private readonly JsonPropertyModel _model;

    internal RoslynJsonCodecPropertyAdapter(
        JsonPropertyModel model,
        IJsonCodecModel type,
        string jsonName)
    {
        _model = model;
        Type = type;
        JsonName = jsonName;
    }

    public string Identifier => GeneratedNaming.Identifier(_model.Property.Name);

    public string JsonName { get; }

    public string TypeName => TypeNames.Display(_model.Property.Type);

    public IJsonCodecModel Type { get; }

    public bool Required => _model.RequiresPresence;

    public string InitialValue => _model.PrimaryParameter is { HasExplicitDefaultValue: true } parameter
        ? FormatDefaultValue(_model.Property.Type, parameter.ExplicitDefaultValue)
        : "default!";

    public bool IsPrimary => _model.IsPrimary;

    public bool IsNonNullableReference => _model.Property.Type.IsReferenceType
        && _model.Property.Type.NullableAnnotation == Microsoft.CodeAnalysis.NullableAnnotation.NotAnnotated;

    private static string FormatDefaultValue(ITypeSymbol type, object? value)
    {
        if (value is null)
        {
            return "default!";
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            return "(" + TypeNames.Display(type) + ")" +
                Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        switch (value)
        {
            case string text:
                return GeneratedNaming.Literal(text);
            case char character:
                return SymbolDisplay.FormatLiteral(character, quote: true);
            case bool boolean:
                return boolean ? "true" : "false";
            case float single when float.IsNaN(single):
                return "global::System.Single.NaN";
            case float single when float.IsPositiveInfinity(single):
                return "global::System.Single.PositiveInfinity";
            case float single when float.IsNegativeInfinity(single):
                return "global::System.Single.NegativeInfinity";
            case float single:
                return single.ToString("R", CultureInfo.InvariantCulture) + "F";
            case double number when double.IsNaN(number):
                return "global::System.Double.NaN";
            case double number when double.IsPositiveInfinity(number):
                return "global::System.Double.PositiveInfinity";
            case double number when double.IsNegativeInfinity(number):
                return "global::System.Double.NegativeInfinity";
            case double number:
                return number.ToString("R", CultureInfo.InvariantCulture) + "D";
            case decimal decimalValue:
                return decimalValue.ToString(CultureInfo.InvariantCulture) + "M";
            case long longValue:
                return longValue.ToString(CultureInfo.InvariantCulture) + "L";
            case ulong ulongValue:
                return ulongValue.ToString(CultureInfo.InvariantCulture) + "UL";
            case uint uintValue:
                return uintValue.ToString(CultureInfo.InvariantCulture) + "U";
            default:
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "default!";
        }
    }
}

internal sealed class OpenApiJsonCodecModelAdapter : IJsonCodecModel
{
    private readonly OpenApiJsonCodecModel _model;
    private readonly IReadOnlyDictionary<OpenApiJsonCodecModel, IJsonCodecModel> _models;
    private IReadOnlyList<IJsonCodecProperty>? _properties;

    private OpenApiJsonCodecModelAdapter(
        OpenApiJsonCodecModel model,
        IReadOnlyDictionary<OpenApiJsonCodecModel, IJsonCodecModel> models)
    {
        _model = model;
        _models = models;
    }

    internal static ImmutableArray<IJsonCodecModel> Create(
        ImmutableArray<OpenApiJsonCodecModel> models)
    {
        var adapters = new Dictionary<OpenApiJsonCodecModel, IJsonCodecModel>();
        foreach (var model in models)
        {
            adapters.Add(model, null!);
        }

        foreach (var model in models)
        {
            adapters[model] = new OpenApiJsonCodecModelAdapter(model, adapters);
        }

        var result = ImmutableArray.CreateBuilder<IJsonCodecModel>(models.Length);
        foreach (var model in models)
        {
            result.Add(adapters[model]);
        }

        return result.ToImmutable();
    }

    public string TypeName => _model.NonNullableTypeName;

    public string DisplayTypeName => _model.NonNullableTypeName;

    public string ValueTypeName => _model.ValueTypeName;

    public string CodecName => _model.CodecName;

    public JsonTypeKind Kind => _model.Kind;

    public bool IsReferenceType => _model.IsReferenceType;

    public IJsonCodecModel? ElementType => _model.ElementType is null
        ? null
        : _models[_model.ElementType];

    public string? ElementTypeName => _model.ElementTypeName;

    public bool ElementIsNonNullableReference => _model.ElementIsNonNullableReference;

    public IJsonCodecModel? DictionaryValueType => null;

    public IJsonCodecModel? EnumUnderlyingType => _model.EnumUnderlyingType is null
        ? null
        : _models[_model.EnumUnderlyingType];

    public IReadOnlyList<IJsonCodecProperty> Properties => _properties ??= AdaptProperties();

    public IReadOnlyList<IJsonCodecProperty> PrimaryProperties => Properties;

    public IReadOnlyList<KeyValuePair<string, string>>? EnumMembers => _model.EnumMembers;

    private IReadOnlyList<IJsonCodecProperty> AdaptProperties()
    {
        var result = new IJsonCodecProperty[_model.Properties.Count];
        for (var index = 0; index < result.Length; index++)
        {
            var property = _model.Properties[index];
            result[index] = new OpenApiJsonCodecPropertyAdapter(property, _models[property.Type]);
        }

        return result;
    }
}

internal sealed class OpenApiJsonCodecPropertyAdapter : IJsonCodecProperty
{
    private readonly OpenApiJsonCodecProperty _property;

    internal OpenApiJsonCodecPropertyAdapter(
        OpenApiJsonCodecProperty property,
        IJsonCodecModel type)
    {
        _property = property;
        Type = type;
    }

    public string Identifier => _property.Identifier;

    public string JsonName => _property.JsonName;

    public string TypeName => _property.TypeName;

    public IJsonCodecModel Type { get; }

    public bool Required => _property.Required;

    public string InitialValue => "default!";

    public bool IsPrimary => _property.IsPrimary;

    public bool IsNonNullableReference => _property.IsNonNullableReference;
}
