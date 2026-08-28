using System;
using System.Collections.Generic;
using System.Linq;

namespace Miya.Generators.Core;

internal sealed class JsonCodecSourceEmitter
{
    private readonly IReadOnlyList<IJsonCodecModel> _models;

    internal JsonCodecSourceEmitter(IReadOnlyList<IJsonCodecModel> models)
    {
        _models = models;
    }

    internal void EmitCodecs(CodeWriter writer)
    {
        foreach (var model in _models)
        {
            EmitCodec(writer, model);
            writer.Line();
        }
    }

    internal void EmitCodec(CodeWriter writer, IJsonCodecModel model)
    {
        writer.Open(
            "internal sealed class " + model.CodecName
            + " : global::Miya.Json.IJsonCodec<" + model.TypeName + ">");
        writer.Line("internal static readonly " + model.CodecName + " Instance = new " + model.CodecName + "();");
        writer.Line();
        writer.Line(
            "public void Write(ref global::Miya.Json.JsonWriter writer, " + model.ValueTypeName
            + " value) => WriteValue(ref writer, value);");
        writer.Line(
            "public " + model.ValueTypeName
            + " Read(ref global::Miya.Json.JsonReader reader) => ReadValue(ref reader, 0);");
        writer.Line();
        writer.Open(
            "internal static void WriteValue(ref global::Miya.Json.JsonWriter writer, "
            + model.ValueTypeName + " value)");
        EmitWriteBody(writer, model);
        writer.Close();
        writer.Line();
        writer.Open(
            "internal static " + model.ValueTypeName
            + " ReadValue(ref global::Miya.Json.JsonReader reader, int depth)");
        EmitReadBody(writer, model);
        writer.Close();
        writer.Close();
    }

    internal void EmitRegistration(CodeWriter writer, string className)
    {
        writer.Open("internal static class " + className);
        writer.Line("[global::System.Runtime.CompilerServices.ModuleInitializer]");
        writer.Open("internal static void Initialize()");
        foreach (var model in _models)
        {
            writer.Line(
                "global::Miya.Json.Json.Register<" + model.TypeName + ">("
                + model.CodecName + ".Instance);");
        }

        writer.Close();
        writer.Close();
    }

    internal void EmitDeferredRegistration(CodeWriter writer, string className)
    {
        writer.Open("internal static class " + className);
        writer.Open("static " + className + "()");
        foreach (var model in _models)
        {
            writer.Line(
                "global::Miya.Json.Json.Register<" + model.TypeName + ">(" +
                "global::Miya.Json.Json.ResolveCodec<" + model.TypeName + ">(" +
                model.CodecName + ".Instance));");
        }

        writer.Close();
        writer.Line();
        writer.Open("internal static void EnsureRegistered()");
        writer.Close();
        writer.Close();
    }

    internal void EmitSingleRegistration(
        CodeWriter writer,
        IJsonCodecModel model,
        string className)
    {
        writer.Open("internal static class " + className);
        writer.Line("[global::System.Runtime.CompilerServices.ModuleInitializer]");
        writer.Open("internal static void Initialize()");
        writer.Line(
            "global::Miya.Json.Json.Register<" + model.TypeName + ">("
            + model.CodecName + ".Instance);");
        writer.Close();
        writer.Close();
    }

    private static void EmitWriteBody(CodeWriter writer, IJsonCodecModel model)
    {
        switch (model.Kind)
        {
            case JsonTypeKind.Boolean:
                writer.Line("writer.WriteBool(value);");
                return;
            case JsonTypeKind.Byte:
            case JsonTypeKind.SByte:
            case JsonTypeKind.Int16:
            case JsonTypeKind.UInt16:
                writer.Line("writer.WriteNumber((int)value);");
                return;
            case JsonTypeKind.Int32:
            case JsonTypeKind.UInt32:
            case JsonTypeKind.Int64:
            case JsonTypeKind.UInt64:
            case JsonTypeKind.Single:
            case JsonTypeKind.Double:
            case JsonTypeKind.Decimal:
                writer.Line("writer.WriteNumber(value);");
                return;
            case JsonTypeKind.Char:
                writer.Line("writer.WriteString(value.ToString());");
                return;
            case JsonTypeKind.String:
                writer.Line("writer.WriteString(value);");
                return;
            case JsonTypeKind.Guid:
                writer.Line("writer.WriteGuid(value);");
                return;
            case JsonTypeKind.DateTime:
                writer.Line("writer.WriteDateTime(value);");
                return;
            case JsonTypeKind.DateTimeOffset:
                writer.Line("writer.WriteDateTimeOffset(value);");
                return;
            case JsonTypeKind.Enum:
                if (model.EnumMembers is not null)
                {
                    EmitWriteStringEnum(writer, model);
                }
                else
                {
                    writer.Line(
                        WriteCall(
                            model.EnumUnderlyingType!,
                            "(" + model.EnumUnderlyingType!.TypeName + ")value"));
                }

                return;
            case JsonTypeKind.Nullable:
                writer.Open("if (!value.HasValue)");
                writer.Line("writer.WriteNull();");
                writer.Line("return;");
                writer.Close();
                writer.Line(WriteCall(model.ElementType!, "value.Value"));
                return;
            case JsonTypeKind.Array:
            case JsonTypeKind.List:
                EmitWriteSequence(writer, model);
                return;
            case JsonTypeKind.Dictionary:
                EmitWriteDictionary(writer, model);
                return;
            case JsonTypeKind.Object:
                EmitWriteObject(writer, model);
                return;
            default:
                throw new InvalidOperationException("Unknown JSON type kind.");
        }
    }

    private static void EmitWriteStringEnum(CodeWriter writer, IJsonCodecModel model)
    {
        writer.Line("writer.WriteString(value switch");
        writer.Line("{");
        foreach (var member in model.EnumMembers!)
        {
            writer.Line(
                "    " + model.TypeName + "." + member.Value + " => "
                + GeneratedNaming.Literal(member.Key) + ",");
        }

        writer.Line("    _ => value.ToString(),");
        writer.Line("});");
    }

    private static void EmitWriteSequence(CodeWriter writer, IJsonCodecModel model)
    {
        EmitNullWrite(writer);
        writer.Line(
            "writer.EnterContainer(" +
            (model.Kind == JsonTypeKind.Array ? "value.Length" : "value.Count") + ");");
        writer.Line("writer.WriteRaw(\"[\"u8);");
        writer.Line("var index = 0;");
        writer.Open("foreach (var item in value)");
        writer.Open("if (index != 0)");
        writer.Line("writer.WriteRaw(\",\"u8);");
        writer.Close();
        writer.Line(WriteCall(model.ElementType!, "item"));
        writer.Open("if ((++index & 4095) == 0)");
        writer.Line("writer.ThrowIfCancellationRequested();");
        writer.Close();
        writer.Close();
        writer.Line("writer.WriteRaw(\"]\"u8);");
        writer.Line("writer.ExitContainer();");
    }

    private static void EmitWriteDictionary(CodeWriter writer, IJsonCodecModel model)
    {
        EmitNullWrite(writer);
        writer.Line("writer.EnterContainer(value.Count);");
        writer.Line("writer.WriteRaw(\"{\"u8);");
        writer.Line("var index = 0;");
        writer.Open("foreach (var pair in value)");
        writer.Open("if (index != 0)");
        writer.Line("writer.WriteRaw(\",\"u8);");
        writer.Close();
        writer.Line("writer.WriteString(pair.Key);");
        writer.Line("writer.WriteRaw(\":\"u8);");
        writer.Line(WriteCall(model.DictionaryValueType!, "pair.Value"));
        writer.Open("if ((++index & 4095) == 0)");
        writer.Line("writer.ThrowIfCancellationRequested();");
        writer.Close();
        writer.Close();
        writer.Line("writer.WriteRaw(\"}\"u8);");
        writer.Line("writer.ExitContainer();");
    }

    private static void EmitWriteObject(CodeWriter writer, IJsonCodecModel model)
    {
        if (model.IsReferenceType)
        {
            EmitNullWrite(writer);
        }

        writer.Line("writer.EnterContainer(" + model.Properties.Count + ");");
        if (model.Properties.Count == 0)
        {
            writer.Line("writer.WriteRaw(\"{}\"u8);");
            writer.Line("writer.ExitContainer();");
            return;
        }

        for (var index = 0; index < model.Properties.Count; index++)
        {
            var property = model.Properties[index];
            var prefix = GeneratedNaming.JsonMemberPrefix(property.JsonName, index == 0);
            writer.Line("writer.WriteRaw(" + GeneratedNaming.Utf8Literal(prefix) + ");");
            writer.Line(WriteCall(property.Type, "value." + property.Identifier));
        }

        writer.Line("writer.WriteRaw(\"}\"u8);");
        writer.Line("writer.ExitContainer();");
    }

    private static void EmitReadBody(CodeWriter writer, IJsonCodecModel model)
    {
        switch (model.Kind)
        {
            case JsonTypeKind.Boolean:
                writer.Line("return reader.ReadBool();");
                return;
            case JsonTypeKind.Byte:
                EmitCheckedIntegerRead(writer, "byte", "Byte");
                return;
            case JsonTypeKind.SByte:
                EmitCheckedIntegerRead(writer, "sbyte", "SByte");
                return;
            case JsonTypeKind.Int16:
                EmitCheckedIntegerRead(writer, "short", "Int16");
                return;
            case JsonTypeKind.UInt16:
                EmitCheckedIntegerRead(writer, "ushort", "UInt16");
                return;
            case JsonTypeKind.Int32:
                writer.Line("return reader.ReadInt32();");
                return;
            case JsonTypeKind.UInt32:
                writer.Line("return reader.ReadUInt32();");
                return;
            case JsonTypeKind.Int64:
                writer.Line("return reader.ReadInt64();");
                return;
            case JsonTypeKind.UInt64:
                writer.Line("return reader.ReadUInt64();");
                return;
            case JsonTypeKind.Single:
                writer.Line("return reader.ReadSingle();");
                return;
            case JsonTypeKind.Double:
                writer.Line("return reader.ReadDouble();");
                return;
            case JsonTypeKind.Decimal:
                writer.Line("return reader.ReadDecimal();");
                return;
            case JsonTypeKind.Char:
                writer.Line("var text = reader.ReadString();");
                writer.Open("if (text is null || text.Length != 1)");
                writer.Line("throw new global::Miya.Json.JsonException(\"Expected a single JSON character.\", isInputError: true);");
                writer.Close();
                writer.Line("return text[0];");
                return;
            case JsonTypeKind.String:
                writer.Line("return reader.ReadString();");
                return;
            case JsonTypeKind.Guid:
                writer.Line("return reader.ReadGuid();");
                return;
            case JsonTypeKind.DateTime:
                writer.Line("return reader.ReadDateTime();");
                return;
            case JsonTypeKind.DateTimeOffset:
                writer.Line("return reader.ReadDateTimeOffset();");
                return;
            case JsonTypeKind.Enum:
                if (model.EnumMembers is not null)
                {
                    EmitReadStringEnum(writer, model);
                }
                else
                {
                    writer.Line(
                        "return (" + model.TypeName + ")"
                        + ReadCall(model.EnumUnderlyingType!, "depth") + ";");
                }

                return;
            case JsonTypeKind.Nullable:
                writer.Open("if (reader.TryReadNull())");
                writer.Line("return null;");
                writer.Close();
                writer.Line("return " + ReadCall(model.ElementType!, "depth") + ";");
                return;
            case JsonTypeKind.Array:
            case JsonTypeKind.List:
                EmitReadSequence(writer, model);
                return;
            case JsonTypeKind.Dictionary:
                EmitReadDictionary(writer, model);
                return;
            case JsonTypeKind.Object:
                EmitReadObject(writer, model);
                return;
            default:
                throw new InvalidOperationException("Unknown JSON type kind.");
        }
    }

    private static void EmitReadStringEnum(CodeWriter writer, IJsonCodecModel model)
    {
        writer.Line("var value = reader.ReadString();");
        writer.Line("return value switch");
        writer.Line("{");
        foreach (var member in model.EnumMembers!)
        {
            writer.Line(
                "    " + GeneratedNaming.Literal(member.Key) + " => "
                + model.TypeName + "." + member.Value + ",");
        }

        writer.Line(
            "    _ => throw new global::Miya.Json.JsonException("
            + GeneratedNaming.Literal("Unknown value for '" + model.TypeName + "'.")
            + ", isInputError: true),");
        writer.Line("};");
    }

    private static void EmitReadSequence(CodeWriter writer, IJsonCodecModel model)
    {
        EmitNullRead(writer);
        var listType = "global::System.Collections.Generic.List<" + model.ElementTypeName + ">";
        writer.Line("var result = new " + listType + "();");
        writer.Line("reader.ReadBeginArray();");
        writer.Open("while (!reader.TryReadEndArray())");
        var read = ReadCall(model.ElementType!, "depth + 1");
        if (model.ElementIsNonNullableReference)
        {
            read += " ?? throw new global::Miya.Json.JsonException("
                + GeneratedNaming.Literal("An array item cannot be null.")
                + ", isInputError: true)";
        }

        writer.Line("result.Add(" + read + ");");
        writer.Close();
        writer.Line(model.Kind == JsonTypeKind.Array ? "return result.ToArray();" : "return result;");
    }

    private static void EmitReadDictionary(CodeWriter writer, IJsonCodecModel model)
    {
        EmitNullRead(writer);
        writer.Line(
            "var result = new " + model.TypeName
            + "(global::System.StringComparer.Ordinal);");
        writer.Line("reader.ReadBeginObject();");
        writer.Open("while (!reader.TryReadEndObject())");
        writer.Line("var name = reader.ReadPropertyName();");
        writer.Line("var key = global::System.Text.Encoding.UTF8.GetString(name);");
        writer.Line(
            "result[key] = " + ReadCall(model.DictionaryValueType!, "depth + 1") + ";");
        writer.Close();
        writer.Line("return result;");
    }

    private static void EmitReadObject(CodeWriter writer, IJsonCodecModel model)
    {
        if (model.IsReferenceType)
        {
            EmitNullRead(writer);
        }

        for (var index = 0; index < model.Properties.Count; index++)
        {
            var property = model.Properties[index];
            writer.Line(property.TypeName + " property" + index + " = " + property.InitialValue + ";");
            if (property.Required)
            {
                writer.Line("var hasProperty" + index + " = false;");
            }
        }

        writer.Line("reader.ReadBeginObject();");
        writer.Open("while (!reader.TryReadEndObject())");
        if (model.Properties.Count == 0)
        {
            writer.Line("reader.ReadPropertyName();");
            writer.Line("reader.SkipValue();");
        }
        else
        {
            writer.Line("var propertyName = reader.ReadPropertyName();");
            writer.Open("switch (propertyName.Length)");
            var groups = model.Properties
                .Select((property, index) => new
                {
                    Property = property,
                    Index = index,
                    ByteLength = System.Text.Encoding.UTF8.GetByteCount(property.JsonName),
                })
                .GroupBy(static property => property.ByteLength)
                .OrderBy(static group => group.Key);
            foreach (var group in groups)
            {
                writer.Line("case " + group.Key + ":");
                var first = true;
                foreach (var item in group)
                {
                    writer.Open(
                        (first ? "if" : "else if") + " (global::System.MemoryExtensions.SequenceEqual(propertyName, "
                        + GeneratedNaming.Utf8Literal(item.Property.JsonName) + "))");
                    var read = ReadCall(item.Property.Type, "depth + 1");
                    if (item.Property.IsNonNullableReference)
                    {
                        read += " ?? throw new global::Miya.Json.JsonException("
                            + GeneratedNaming.Literal("Property '" + item.Property.JsonName + "' cannot be null.")
                            + ", isInputError: true)";
                    }

                    writer.Line("property" + item.Index + " = " + read + ";");
                    if (item.Property.Required)
                    {
                        writer.Line("hasProperty" + item.Index + " = true;");
                    }

                    writer.Close();
                    first = false;
                }

                writer.Open("else");
                writer.Line("reader.SkipValue();");
                writer.Close();
                writer.Line("break;");
            }

            writer.Line("default:");
            writer.Line("    reader.SkipValue();");
            writer.Line("    break;");
            writer.Close();
        }

        writer.Close();

        var required = model.Properties
            .Select((property, index) => new { Property = property, Index = index })
            .Where(static item => item.Property.Required)
            .ToList();
        foreach (var item in required)
        {
            writer.Open("if (!hasProperty" + item.Index + ")");
            writer.Line(
                "throw new global::Miya.Json.JsonException("
                + GeneratedNaming.Literal("Required property '" + item.Property.JsonName + "' is missing for '"
                    + model.DisplayTypeName + "'.")
                + ", isInputError: true);");
            writer.Close();
        }

        EmitObjectConstruction(writer, model);
    }

    private static void EmitObjectConstruction(CodeWriter writer, IJsonCodecModel model)
    {
        var primaryIndexes = new List<int>();
        foreach (var primary in model.PrimaryProperties)
        {
            for (var propertyIndex = 0; propertyIndex < model.Properties.Count; propertyIndex++)
            {
                if (string.Equals(
                        primary.Identifier,
                        model.Properties[propertyIndex].Identifier,
                        StringComparison.Ordinal))
                {
                    primaryIndexes.Add(propertyIndex);
                    break;
                }
            }
        }

        var constructor = "new " + model.TypeName + "("
            + string.Join(", ", primaryIndexes.Select(static index => "property" + index))
            + ")";
        var remaining = Enumerable.Range(0, model.Properties.Count)
            .Where(index => !model.Properties[index].IsPrimary)
            .ToList();
        if (remaining.Count == 0)
        {
            writer.Line("return " + constructor + ";");
            return;
        }

        writer.Line("return " + constructor);
        writer.Line("{");
        foreach (var index in remaining)
        {
            writer.Line("    " + model.Properties[index].Identifier + " = property" + index + ",");
        }

        writer.Line("};");
    }

    private static void EmitNullWrite(CodeWriter writer)
    {
        writer.Open("if (value is null)");
        writer.Line("writer.WriteNull();");
        writer.Line("return;");
        writer.Close();
    }

    private static void EmitNullRead(CodeWriter writer)
    {
        writer.Open("if (reader.TryReadNull())");
        writer.Line("return null;");
        writer.Close();
    }

    private static void EmitCheckedIntegerRead(CodeWriter writer, string targetType, string typeName)
    {
        writer.Open("try");
        writer.Line("return checked((" + targetType + ")reader.ReadInt32());");
        writer.Close();
        writer.Open("catch (global::System.OverflowException exception)");
        writer.Line(
            "throw new global::Miya.Json.JsonException("
            + GeneratedNaming.Literal("The JSON number is outside the " + typeName + " range.")
            + ", exception, isInputError: true);");
        writer.Close();
    }

    private static string WriteCall(IJsonCodecModel model, string value) =>
        model.CodecName + ".WriteValue(ref writer, " + value + ");";

    private static string ReadCall(IJsonCodecModel model, string depth) =>
        model.CodecName + ".ReadValue(ref reader, " + depth + ")";
}
