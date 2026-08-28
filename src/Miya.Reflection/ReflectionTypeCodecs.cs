using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Miya.Json;

namespace Miya.Reflection;

internal abstract class ReflectionTypeCodec
{
    internal abstract void Write(ref JsonWriter writer, object? value);

    internal abstract object? Read(ref JsonReader reader);
}

internal static class ReflectionTypeCodecCache
{
    private static readonly ConcurrentDictionary<Type, ReflectionTypeCodec> Codecs = new();

    internal static ReflectionTypeCodec Get(Type type) => Codecs.GetOrAdd(type, Create);

    private static ReflectionTypeCodec Create(Type type)
    {
        if (type.ContainsGenericParameters || type.IsByRefLike || type.IsPointer || type.IsByRef)
        {
            throw Unsupported(type);
        }

        var nullableType = Nullable.GetUnderlyingType(type);
        if (nullableType is not null)
        {
            return new NullableReflectionCodec(nullableType);
        }

        if (type.IsEnum)
        {
            return new EnumReflectionCodec(type, Enum.GetUnderlyingType(type));
        }

        if (PrimitiveReflectionCodec.Supports(type))
        {
            return new PrimitiveReflectionCodec(type);
        }

        if (type.IsArray)
        {
            if (type.GetArrayRank() != 1 || type.GetElementType() is not { } elementType)
            {
                throw Unsupported(type);
            }

            return new SequenceReflectionCodec(type, elementType, isArray: true);
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            return new SequenceReflectionCodec(type, type.GetGenericArguments()[0], isArray: false);
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            var arguments = type.GetGenericArguments();
            if (arguments[0] != typeof(string))
            {
                throw Unsupported(type);
            }

            return new DictionaryReflectionCodec(type, arguments[1]);
        }

        return PocoReflectionCodec.Create(type);
    }

    private static UnsupportedReflectionTypeException Unsupported(Type type) => new(
        $"Reflection JSON codecs do not support '{type}'.");
}

internal sealed class PrimitiveReflectionCodec : ReflectionTypeCodec
{
    private readonly Type _type;

    internal PrimitiveReflectionCodec(Type type)
    {
        _type = type;
    }

    internal static bool Supports(Type type) =>
        type == typeof(bool) ||
        type == typeof(byte) ||
        type == typeof(sbyte) ||
        type == typeof(short) ||
        type == typeof(ushort) ||
        type == typeof(int) ||
        type == typeof(uint) ||
        type == typeof(long) ||
        type == typeof(ulong) ||
        type == typeof(float) ||
        type == typeof(double) ||
        type == typeof(decimal) ||
        type == typeof(char) ||
        type == typeof(string) ||
        type == typeof(Guid) ||
        type == typeof(DateTime) ||
        type == typeof(DateTimeOffset);

    internal override void Write(ref JsonWriter writer, object? value)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        if (_type == typeof(bool))
        {
            writer.WriteBool((bool)value);
        }
        else if (_type == typeof(byte))
        {
            writer.WriteNumber((int)(byte)value);
        }
        else if (_type == typeof(sbyte))
        {
            writer.WriteNumber((int)(sbyte)value);
        }
        else if (_type == typeof(short))
        {
            writer.WriteNumber((int)(short)value);
        }
        else if (_type == typeof(ushort))
        {
            writer.WriteNumber((int)(ushort)value);
        }
        else if (_type == typeof(int))
        {
            writer.WriteNumber((int)value);
        }
        else if (_type == typeof(uint))
        {
            writer.WriteNumber((uint)value);
        }
        else if (_type == typeof(long))
        {
            writer.WriteNumber((long)value);
        }
        else if (_type == typeof(ulong))
        {
            writer.WriteNumber((ulong)value);
        }
        else if (_type == typeof(float))
        {
            writer.WriteNumber((float)value);
        }
        else if (_type == typeof(double))
        {
            writer.WriteNumber((double)value);
        }
        else if (_type == typeof(decimal))
        {
            writer.WriteNumber((decimal)value);
        }
        else if (_type == typeof(char))
        {
            writer.WriteString(((char)value).ToString());
        }
        else if (_type == typeof(string))
        {
            writer.WriteString((string)value);
        }
        else if (_type == typeof(Guid))
        {
            writer.WriteGuid((Guid)value);
        }
        else if (_type == typeof(DateTime))
        {
            writer.WriteDateTime((DateTime)value);
        }
        else
        {
            writer.WriteDateTimeOffset((DateTimeOffset)value);
        }
    }

    internal override object? Read(ref JsonReader reader)
    {
        if (_type == typeof(string))
        {
            return reader.ReadString();
        }

        if (_type == typeof(bool))
        {
            return reader.ReadBool();
        }

        if (_type == typeof(int))
        {
            return reader.ReadInt32();
        }

        if (_type == typeof(uint))
        {
            return reader.ReadUInt32();
        }

        if (_type == typeof(long))
        {
            return reader.ReadInt64();
        }

        if (_type == typeof(ulong))
        {
            return reader.ReadUInt64();
        }

        if (_type == typeof(float))
        {
            return reader.ReadSingle();
        }

        if (_type == typeof(double))
        {
            return reader.ReadDouble();
        }

        if (_type == typeof(decimal))
        {
            return reader.ReadDecimal();
        }

        if (_type == typeof(char))
        {
            var text = reader.ReadString();
            if (text is null || text.Length != 1)
            {
                throw new JsonException("Expected a single JSON character.", isInputError: true);
            }

            return text[0];
        }

        if (_type == typeof(Guid))
        {
            return reader.ReadGuid();
        }

        if (_type == typeof(DateTime))
        {
            return reader.ReadDateTime();
        }

        if (_type == typeof(DateTimeOffset))
        {
            return reader.ReadDateTimeOffset();
        }

        try
        {
            var value = reader.ReadInt32();
            if (_type == typeof(byte))
            {
                return checked((byte)value);
            }

            if (_type == typeof(sbyte))
            {
                return checked((sbyte)value);
            }

            if (_type == typeof(short))
            {
                return checked((short)value);
            }

            return checked((ushort)value);
        }
        catch (OverflowException exception)
        {
            throw new JsonException(
                $"The JSON number is outside the {_type.Name} range.",
                exception,
                isInputError: true);
        }
    }
}

internal sealed class NullableReflectionCodec : ReflectionTypeCodec
{
    private readonly Type _valueType;

    internal NullableReflectionCodec(Type valueType)
    {
        _valueType = valueType;
    }

    internal override void Write(ref JsonWriter writer, object? value)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        ReflectionTypeCodecCache.Get(_valueType).Write(ref writer, value);
    }

    internal override object? Read(ref JsonReader reader)
    {
        return reader.TryReadNull()
            ? null
            : ReflectionTypeCodecCache.Get(_valueType).Read(ref reader);
    }
}

internal sealed class EnumReflectionCodec : ReflectionTypeCodec
{
    private readonly Type _enumType;
    private readonly Type _underlyingType;

    internal EnumReflectionCodec(Type enumType, Type underlyingType)
    {
        _enumType = enumType;
        _underlyingType = underlyingType;
    }

    internal override void Write(ref JsonWriter writer, object? value)
    {
        var underlyingValue = Convert.ChangeType(value, _underlyingType, CultureInfo.InvariantCulture);
        ReflectionTypeCodecCache.Get(_underlyingType).Write(ref writer, underlyingValue);
    }

    internal override object? Read(ref JsonReader reader)
    {
        var value = ReflectionTypeCodecCache.Get(_underlyingType).Read(ref reader);
        return Enum.ToObject(_enumType, value!);
    }
}

internal sealed class SequenceReflectionCodec : ReflectionTypeCodec
{
    private readonly Type _collectionType;
    private readonly Type _elementType;
    private readonly bool _isArray;

    internal SequenceReflectionCodec(Type collectionType, Type elementType, bool isArray)
    {
        _collectionType = collectionType;
        _elementType = elementType;
        _isArray = isArray;
    }

    internal override void Write(ref JsonWriter writer, object? value)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        var values = (IList)value;
        writer.EnterContainer(values.Count);
        writer.WriteRaw("["u8);
        var elementCodec = ReflectionTypeCodecCache.Get(_elementType);
        for (var index = 0; index < values.Count; index++)
        {
            if (index != 0)
            {
                writer.WriteRaw(","u8);
            }

            elementCodec.Write(ref writer, values[index]);
            if (((index + 1) & 4095) == 0)
            {
                writer.ThrowIfCancellationRequested();
            }
        }

        writer.WriteRaw("]"u8);
        writer.ExitContainer();
    }

    internal override object? Read(ref JsonReader reader)
    {
        if (reader.TryReadNull())
        {
            return null;
        }

        var values = new List<object?>();
        var elementCodec = ReflectionTypeCodecCache.Get(_elementType);
        reader.ReadBeginArray();
        while (!reader.TryReadEndArray())
        {
            values.Add(elementCodec.Read(ref reader));
        }

        if (_isArray)
        {
            var array = Array.CreateInstance(_elementType, values.Count);
            for (var index = 0; index < values.Count; index++)
            {
                array.SetValue(values[index], index);
            }

            return array;
        }

        var result = (IList)Activator.CreateInstance(_collectionType)!;
        foreach (var value in values)
        {
            result.Add(value);
        }

        return result;
    }
}

internal sealed class DictionaryReflectionCodec : ReflectionTypeCodec
{
    private readonly Type _dictionaryType;
    private readonly Type _valueType;

    internal DictionaryReflectionCodec(Type dictionaryType, Type valueType)
    {
        _dictionaryType = dictionaryType;
        _valueType = valueType;
    }

    internal override void Write(ref JsonWriter writer, object? value)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        var values = (IDictionary)value;
        writer.EnterContainer(values.Count);
        writer.WriteRaw("{"u8);
        var valueCodec = ReflectionTypeCodecCache.Get(_valueType);
        var index = 0;
        foreach (DictionaryEntry pair in values)
        {
            if (index != 0)
            {
                writer.WriteRaw(","u8);
            }

            writer.WriteString((string)pair.Key);
            writer.WriteRaw(":"u8);
            valueCodec.Write(ref writer, pair.Value);
            if ((++index & 4095) == 0)
            {
                writer.ThrowIfCancellationRequested();
            }
        }

        writer.WriteRaw("}"u8);
        writer.ExitContainer();
    }

    internal override object? Read(ref JsonReader reader)
    {
        if (reader.TryReadNull())
        {
            return null;
        }

        var result = (IDictionary)Activator.CreateInstance(
            _dictionaryType,
            StringComparer.Ordinal)!;
        var valueCodec = ReflectionTypeCodecCache.Get(_valueType);
        reader.ReadBeginObject();
        while (!reader.TryReadEndObject())
        {
            var key = Encoding.UTF8.GetString(reader.ReadPropertyName());
            result[key] = valueCodec.Read(ref reader);
        }

        return result;
    }
}

internal sealed class PocoReflectionCodec : ReflectionTypeCodec
{
    private readonly Type _type;
    private readonly PocoProperty[] _properties;
    private readonly ConstructorInfo? _constructor;
    private readonly int[] _constructorPropertyIndexes;
    private readonly bool[] _constructorBoundProperties;
    private readonly bool[] _requiredProperties;
    private readonly bool[] _hasConstructorDefaults;
    private readonly object?[] _constructorDefaults;

    private PocoReflectionCodec(
        Type type,
        PocoProperty[] properties,
        ConstructorInfo? constructor,
        int[] constructorPropertyIndexes)
    {
        _type = type;
        _properties = properties;
        _constructor = constructor;
        _constructorPropertyIndexes = constructorPropertyIndexes;
        _constructorBoundProperties = new bool[properties.Length];
        _requiredProperties = new bool[properties.Length];
        _hasConstructorDefaults = new bool[properties.Length];
        _constructorDefaults = new object?[properties.Length];
        for (var index = 0; index < properties.Length; index++)
        {
            _requiredProperties[index] = properties[index].IsRequired;
        }

        var parameters = constructor?.GetParameters();
        foreach (var propertyIndex in constructorPropertyIndexes)
        {
            _constructorBoundProperties[propertyIndex] = true;
        }

        if (parameters is not null)
        {
            for (var index = 0; index < parameters.Length; index++)
            {
                var propertyIndex = constructorPropertyIndexes[index];
                if (parameters[index].HasDefaultValue)
                {
                    _hasConstructorDefaults[propertyIndex] = true;
                    _constructorDefaults[propertyIndex] = parameters[index].DefaultValue;
                }
                else
                {
                    _requiredProperties[propertyIndex] = true;
                }
            }
        }
    }

    internal static PocoReflectionCodec Create(Type type)
    {
        if (type == typeof(object) || type.IsInterface || type.IsAbstract ||
            typeof(Delegate).IsAssignableFrom(type))
        {
            throw Unsupported(type);
        }

        var properties = type
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(static property =>
                property.GetMethod?.IsPublic == true &&
                property.GetIndexParameters().Length == 0)
            .OrderBy(static property => property.MetadataToken)
            .Select(static property => new PocoProperty(property))
            .ToArray();

        if (properties.Select(static property => property.JsonName).Distinct(StringComparer.Ordinal).Count()
            != properties.Length)
        {
            throw Unsupported(type);
        }

        var constructor = SelectConstructor(type, properties, out var constructorPropertyIndexes);
        if (!type.IsValueType && constructor is null)
        {
            throw Unsupported(type);
        }

        var bound = constructorPropertyIndexes.ToHashSet();
        for (var index = 0; index < properties.Length; index++)
        {
            if (!bound.Contains(index) && properties[index].Property.SetMethod?.IsPublic != true)
            {
                throw Unsupported(type);
            }
        }

        return new PocoReflectionCodec(type, properties, constructor, constructorPropertyIndexes);
    }

    internal override void Write(ref JsonWriter writer, object? value)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        writer.EnterContainer(_properties.Length);
        writer.WriteRaw("{"u8);
        for (var index = 0; index < _properties.Length; index++)
        {
            if (index != 0)
            {
                writer.WriteRaw(","u8);
            }

            var property = _properties[index];
            writer.WriteString(property.JsonName);
            writer.WriteRaw(":"u8);
            var propertyValue = property.Property.GetValue(value);
            ReflectionTypeCodecCache.Get(property.Property.PropertyType)
                .Write(ref writer, propertyValue);
        }

        writer.WriteRaw("}"u8);
        writer.ExitContainer();
    }

    internal override object? Read(ref JsonReader reader)
    {
        if (!_type.IsValueType && reader.TryReadNull())
        {
            return null;
        }

        var values = new object?[_properties.Length];
        for (var index = 0; index < _properties.Length; index++)
        {
            var propertyType = _properties[index].Property.PropertyType;
            if (propertyType.IsValueType)
            {
                values[index] = Activator.CreateInstance(propertyType);
            }

            if (_hasConstructorDefaults[index])
            {
                values[index] = _constructorDefaults[index];
            }
        }

        var present = new bool[_properties.Length];
        reader.ReadBeginObject();
        while (!reader.TryReadEndObject())
        {
            var jsonName = reader.ReadPropertyName();
            var propertyIndex = FindProperty(jsonName);
            if (propertyIndex < 0)
            {
                reader.SkipValue();
                continue;
            }

            var property = _properties[propertyIndex].Property;
            var propertyValue = ReflectionTypeCodecCache.Get(property.PropertyType)
                .Read(ref reader);
            if (propertyValue is null && _properties[propertyIndex].DisallowsNull)
            {
                throw new JsonException(
                    $"Property '{_properties[propertyIndex].JsonName}' cannot be null.",
                    isInputError: true);
            }

            values[propertyIndex] = propertyValue;
            present[propertyIndex] = true;
        }

        for (var index = 0; index < _properties.Length; index++)
        {
            if (_requiredProperties[index] && !present[index])
            {
                throw new JsonException(
                    $"Required property '{_properties[index].JsonName}' is missing for '{_type}'.",
                    isInputError: true);
            }
        }

        var instance = CreateInstance(values);
        for (var index = 0; index < _properties.Length; index++)
        {
            if (!_constructorBoundProperties[index])
            {
                _properties[index].Property.SetValue(instance, values[index]);
            }
        }

        return instance;
    }

    private object CreateInstance(object?[] values)
    {
        if (_constructor is null)
        {
            return Activator.CreateInstance(_type)!;
        }

        var arguments = new object?[_constructorPropertyIndexes.Length];
        for (var index = 0; index < arguments.Length; index++)
        {
            arguments[index] = values[_constructorPropertyIndexes[index]];
        }

        return _constructor.Invoke(arguments);
    }

    private int FindProperty(ReadOnlySpan<byte> jsonName)
    {
        for (var index = 0; index < _properties.Length; index++)
        {
            if (jsonName.SequenceEqual(_properties[index].Utf8JsonName))
            {
                return index;
            }
        }

        return -1;
    }

    private static ConstructorInfo? SelectConstructor(
        Type type,
        PocoProperty[] properties,
        out int[] propertyIndexes)
    {
        var candidates = new List<(ConstructorInfo Constructor, int[] PropertyIndexes)>();
        foreach (var constructor in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public))
        {
            var parameters = constructor.GetParameters();
            var indexes = new int[parameters.Length];
            var valid = true;
            for (var index = 0; index < parameters.Length; index++)
            {
                var parameter = parameters[index];
                var propertyIndex = Array.FindIndex(properties, property =>
                    string.Equals(property.Property.Name, parameter.Name, StringComparison.OrdinalIgnoreCase) &&
                    property.Property.PropertyType == parameter.ParameterType);
                if (propertyIndex < 0)
                {
                    valid = false;
                    break;
                }

                indexes[index] = propertyIndex;
            }

            if (!valid || indexes.Distinct().Count() != indexes.Length)
            {
                continue;
            }

            var bound = indexes.ToHashSet();
            if (properties.Where((property, index) =>
                    property.Property.SetMethod?.IsPublic != true && !bound.Contains(index)).Any())
            {
                continue;
            }

            candidates.Add((constructor, indexes));
        }

        var selected = candidates
            .OrderByDescending(static candidate => candidate.PropertyIndexes.Length)
            .ThenBy(static candidate => candidate.Constructor.MetadataToken)
            .FirstOrDefault();
        if (selected.Constructor is not null)
        {
            propertyIndexes = selected.PropertyIndexes;
            return selected.Constructor;
        }

        propertyIndexes = [];
        return null;
    }

    private static UnsupportedReflectionTypeException Unsupported(Type type) => new(
        $"Reflection JSON codecs do not support '{type}' because it has no usable public constructor and properties.");
}

internal sealed class PocoProperty
{
    internal PocoProperty(PropertyInfo property)
    {
        Property = property;
        JsonName = CamelCase(property.Name);
        Utf8JsonName = Encoding.UTF8.GetBytes(JsonName);
        IsRequired = property.IsDefined(typeof(RequiredMemberAttribute), inherit: true);
        DisallowsNull = !property.PropertyType.IsValueType &&
            new NullabilityInfoContext().Create(property).ReadState == NullabilityState.NotNull;
    }

    internal PropertyInfo Property { get; }

    internal string JsonName { get; }

    internal byte[] Utf8JsonName { get; }

    internal bool IsRequired { get; }

    internal bool DisallowsNull { get; }

    private static string CamelCase(string name)
    {
        if (string.IsNullOrEmpty(name) || !char.IsUpper(name[0]))
        {
            return name;
        }

        var characters = name.ToCharArray();
        for (var index = 0; index < characters.Length; index++)
        {
            if (index > 0 && !char.IsUpper(characters[index]))
            {
                break;
            }

            if (index > 0 && index + 1 < characters.Length && !char.IsUpper(characters[index + 1]))
            {
                break;
            }

            characters[index] = char.ToLowerInvariant(characters[index]);
        }

        return new string(characters);
    }
}

internal sealed class UnsupportedReflectionTypeException : NotSupportedException
{
    internal UnsupportedReflectionTypeException(string message)
        : base(message)
    {
    }
}
