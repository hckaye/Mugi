using System.Buffers;

namespace Miya.Json.Tests;

internal enum SampleState
{
    Unknown,
    Ready = 7,
}

internal sealed class NestedDto
{
    public long Count { get; set; }
    public string? Note { get; set; }
}

internal sealed class RecursiveNode
{
    public int Value { get; set; }
    public RecursiveNode? Next { get; set; }
}

internal sealed class SampleDto
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public bool Active { get; set; }
    public List<int> Scores { get; set; } = [];
    public Dictionary<string, string?> Tags { get; set; } = [];
    public NestedDto? Details { get; set; }
    public int? Optional { get; set; }
    public SampleState State { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid RequestId { get; set; }
    public decimal Balance { get; set; }
    public RecursiveNode? Node { get; set; }
}

internal sealed class SampleDtoCodec : IMiyaJsonCodec<SampleDto>
{
    public static SampleDtoCodec Instance { get; } = new();

    public void Write(ref MiyaJsonWriter writer, SampleDto? value)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteRaw("{\"id\":"u8);
        writer.WriteNumber(value.Id);
        writer.WriteRaw(",\"name\":"u8);
        writer.WriteString(value.Name);
        writer.WriteRaw(",\"active\":"u8);
        writer.WriteBool(value.Active);
        writer.WriteRaw(",\"scores\":"u8);
        WriteInt32List(ref writer, value.Scores);
        writer.WriteRaw(",\"tags\":"u8);
        WriteStringDictionary(ref writer, value.Tags);
        writer.WriteRaw(",\"details\":"u8);
        NestedDtoCodec.Instance.Write(ref writer, value.Details);
        writer.WriteRaw(",\"optional\":"u8);
        if (value.Optional.HasValue)
        {
            writer.WriteNumber(value.Optional.Value);
        }
        else
        {
            writer.WriteNull();
        }

        writer.WriteRaw(",\"state\":"u8);
        writer.WriteNumber((int)value.State);
        writer.WriteRaw(",\"createdAt\":"u8);
        writer.WriteDateTime(value.CreatedAt);
        writer.WriteRaw(",\"updatedAt\":"u8);
        writer.WriteDateTimeOffset(value.UpdatedAt);
        writer.WriteRaw(",\"requestId\":"u8);
        writer.WriteGuid(value.RequestId);
        writer.WriteRaw(",\"balance\":"u8);
        writer.WriteNumber(value.Balance);
        writer.WriteRaw(",\"node\":"u8);
        RecursiveNodeCodec.Instance.Write(ref writer, value.Node);
        writer.WriteRaw("}"u8);
    }

    public SampleDto? Read(ref MiyaJsonReader reader)
    {
        if (reader.TryReadNull())
        {
            return null;
        }

        var result = new SampleDto { Name = string.Empty };
        var hasId = false;
        var hasName = false;
        reader.ReadBeginObject();
        while (!reader.TryReadEndObject())
        {
            var name = reader.ReadPropertyName();
            if (name.SequenceEqual("id"u8))
            {
                result.Id = reader.ReadInt32();
                hasId = true;
            }
            else if (name.SequenceEqual("name"u8))
            {
                result.Name = reader.ReadString() ?? throw new MiyaJsonException("The required name cannot be null.");
                hasName = true;
            }
            else if (name.SequenceEqual("active"u8))
            {
                result.Active = reader.ReadBool();
            }
            else if (name.SequenceEqual("scores"u8))
            {
                result.Scores = ReadInt32List(ref reader);
            }
            else if (name.SequenceEqual("tags"u8))
            {
                result.Tags = ReadStringDictionary(ref reader);
            }
            else if (name.SequenceEqual("details"u8))
            {
                result.Details = NestedDtoCodec.Instance.Read(ref reader);
            }
            else if (name.SequenceEqual("optional"u8))
            {
                result.Optional = reader.TryReadNull() ? null : reader.ReadInt32();
            }
            else if (name.SequenceEqual("state"u8))
            {
                result.State = (SampleState)reader.ReadInt32();
            }
            else if (name.SequenceEqual("createdAt"u8))
            {
                result.CreatedAt = reader.ReadDateTime();
            }
            else if (name.SequenceEqual("updatedAt"u8))
            {
                result.UpdatedAt = reader.ReadDateTimeOffset();
            }
            else if (name.SequenceEqual("requestId"u8))
            {
                result.RequestId = reader.ReadGuid();
            }
            else if (name.SequenceEqual("balance"u8))
            {
                result.Balance = reader.ReadDecimal();
            }
            else if (name.SequenceEqual("node"u8))
            {
                result.Node = RecursiveNodeCodec.Instance.Read(ref reader);
            }
            else
            {
                reader.SkipValue();
            }
        }

        if (!hasId || !hasName)
        {
            throw new MiyaJsonException("The required id and name properties must be present.");
        }

        return result;
    }

    private static void WriteInt32List(ref MiyaJsonWriter writer, List<int> values)
    {
        writer.WriteRaw("["u8);
        for (var index = 0; index < values.Count; index++)
        {
            if (index != 0)
            {
                writer.WriteRaw(","u8);
            }

            writer.WriteNumber(values[index]);
        }

        writer.WriteRaw("]"u8);
    }

    private static List<int> ReadInt32List(ref MiyaJsonReader reader)
    {
        var result = new List<int>();
        reader.ReadBeginArray();
        while (!reader.TryReadEndArray())
        {
            result.Add(reader.ReadInt32());
        }

        return result;
    }

    private static void WriteStringDictionary(ref MiyaJsonWriter writer, Dictionary<string, string?> values)
    {
        writer.WriteRaw("{"u8);
        var index = 0;
        foreach (var pair in values)
        {
            if (index++ != 0)
            {
                writer.WriteRaw(","u8);
            }

            writer.WriteString(pair.Key);
            writer.WriteRaw(":"u8);
            writer.WriteString(pair.Value);
        }

        writer.WriteRaw("}"u8);
    }

    private static Dictionary<string, string?> ReadStringDictionary(ref MiyaJsonReader reader)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        reader.ReadBeginObject();
        while (!reader.TryReadEndObject())
        {
            var name = reader.ReadPropertyName();
            result[System.Text.Encoding.UTF8.GetString(name)] = reader.ReadString();
        }

        return result;
    }
}

internal sealed class NestedDtoCodec : IMiyaJsonCodec<NestedDto>
{
    public static NestedDtoCodec Instance { get; } = new();

    public void Write(ref MiyaJsonWriter writer, NestedDto? value)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteRaw("{\"count\":"u8);
        writer.WriteNumber(value.Count);
        writer.WriteRaw(",\"note\":"u8);
        writer.WriteString(value.Note);
        writer.WriteRaw("}"u8);
    }

    public NestedDto? Read(ref MiyaJsonReader reader)
    {
        if (reader.TryReadNull())
        {
            return null;
        }

        var result = new NestedDto();
        reader.ReadBeginObject();
        while (!reader.TryReadEndObject())
        {
            var name = reader.ReadPropertyName();
            if (name.SequenceEqual("count"u8))
            {
                result.Count = reader.ReadInt64();
            }
            else if (name.SequenceEqual("note"u8))
            {
                result.Note = reader.ReadString();
            }
            else
            {
                reader.SkipValue();
            }
        }

        return result;
    }
}

internal sealed class RecursiveNodeCodec : IMiyaJsonCodec<RecursiveNode>
{
    public static RecursiveNodeCodec Instance { get; } = new();

    public void Write(ref MiyaJsonWriter writer, RecursiveNode? value)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteRaw("{\"value\":"u8);
        writer.WriteNumber(value.Value);
        writer.WriteRaw(",\"next\":"u8);
        Write(ref writer, value.Next);
        writer.WriteRaw("}"u8);
    }

    public RecursiveNode? Read(ref MiyaJsonReader reader)
    {
        if (reader.TryReadNull())
        {
            return null;
        }

        var result = new RecursiveNode();
        reader.ReadBeginObject();
        while (!reader.TryReadEndObject())
        {
            var name = reader.ReadPropertyName();
            if (name.SequenceEqual("value"u8))
            {
                result.Value = reader.ReadInt32();
            }
            else if (name.SequenceEqual("next"u8))
            {
                result.Next = Read(ref reader);
            }
            else
            {
                reader.SkipValue();
            }
        }

        return result;
    }
}

internal sealed class DelegateCodec<T> : IMiyaJsonCodec<T>
{
    private readonly WriteValue<T> _write;
    private readonly ReadValue<T> _read;

    public DelegateCodec(WriteValue<T> write, ReadValue<T> read)
    {
        _write = write;
        _read = read;
    }

    public void Write(ref MiyaJsonWriter writer, T? value) => _write(ref writer, value);

    public T? Read(ref MiyaJsonReader reader) => _read(ref reader);
}

internal delegate void WriteValue<T>(ref MiyaJsonWriter writer, T? value);
internal delegate T? ReadValue<T>(ref MiyaJsonReader reader);

internal sealed class SkipCodec : IMiyaJsonCodec<bool>
{
    public static SkipCodec Instance { get; } = new();

    public void Write(ref MiyaJsonWriter writer, bool value) => throw new NotSupportedException();

    public bool Read(ref MiyaJsonReader reader)
    {
        reader.SkipValue();
        return true;
    }
}

internal static class JsonTestHelpers
{
    public static byte[] Serialize<T>(T value, IMiyaJsonCodec<T> codec, MiyaJsonOptions? options = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        MiyaJson.Serialize(buffer, value, codec, options);
        return buffer.WrittenSpan.ToArray();
    }
}
