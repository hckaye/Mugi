using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;
using Miya.Json;

namespace Miya.Benchmarks;

[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class SerializerBenchmarks
{
    private ArrayBufferWriter<byte> _miyaBuffer = null!;
    private ArrayBufferWriter<byte> _stjBuffer = null!;
    private Utf8JsonWriter _stjWriter = null!;
    private JsonWriterOptions _stjWriterOptions;
    private BenchmarkJsonContext _context = null!;
    private SmallDto _small = null!;
    private List<ItemDto> _items = null!;
    private NestedDto _nested = null!;
    private string _escapeHeavy = null!;
    private string _longString = null!;
    private IntegerPayload _integers = null!;
    private byte[] _requestJson = null!;

    [GlobalSetup]
    public void Setup()
    {
        _miyaBuffer = new ArrayBufferWriter<byte>(16);
        _stjBuffer = new ArrayBufferWriter<byte>(16);
        _context = BenchmarkJsonContext.Default;
        _stjWriterOptions = new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        _stjWriter = new Utf8JsonWriter(_stjBuffer, _stjWriterOptions);
        _small = new SmallDto { Id = 42, Name = "Miya", Active = true };
        _items = Enumerable.Range(0, 100)
            .Select(index => new ItemDto { Id = index, Name = $"item-{index}", Enabled = (index & 1) == 0 })
            .ToList();
        _nested = new NestedDto
        {
            Name = "root",
            Values = [1, 2, 3, 5, 8, 13, 21],
            Child = new NestedDto
            {
                Name = "middle",
                Values = [34, 55, 89],
                Child = new NestedDto { Name = "leaf", Values = [144, 233] },
            },
        };
        _escapeHeavy = string.Concat(Enumerable.Repeat("quote=\" slash=\\ line=\n tab=\t 日本語 😀 ", 32));
        _longString = new string('x', 32 * 1024);
        _integers = new IntegerPayload
        {
            Minimum = long.MinValue,
            Maximum = ulong.MaxValue,
            Values = Enumerable.Range(-128, 256).ToArray(),
        };
        var request = new RequestDto
        {
            Id = 12345,
            Query = "native AOT JSON request",
            Page = 7,
            IncludeDetails = true,
            Filters = [3, 5, 8, 13, 21],
        };
        _requestJson = JsonSerializer.SerializeToUtf8Bytes(request, _context.RequestDto);

        ValidateEquivalent(_small, SmallDtoCodec.Instance, _context.SmallDto);
        ValidateEquivalent(_items, ItemDtoListCodec.Instance, _context.ItemDtoList);
        ValidateEquivalent(_nested, NestedDtoCodec.Instance, _context.NestedDto);
        ValidateEquivalent(_escapeHeavy, StringCodec.Instance, _context.String);
        ValidateEquivalent(_longString, StringCodec.Instance, _context.String);
        ValidateEquivalent(_integers, IntegerPayloadCodec.Instance, _context.IntegerPayload);

        _ = SmallDtoMiya();
        _ = SmallDtoStj();
        _miyaBuffer.Clear();
        _stjBuffer.Clear();
    }

    [BenchmarkCategory("Small DTO"), Benchmark]
    public int SmallDtoMiya() => SerializeMiya(_small, SmallDtoCodec.Instance);

    [BenchmarkCategory("Small DTO"), Benchmark(Baseline = true)]
    public int SmallDtoStj() => SerializeStj(_small, _context.SmallDto);

    [BenchmarkCategory("List 100"), Benchmark]
    public int List100Miya() => SerializeMiya(_items, ItemDtoListCodec.Instance);

    [BenchmarkCategory("List 100"), Benchmark(Baseline = true)]
    public int List100Stj() => SerializeStj(_items, _context.ItemDtoList);

    [BenchmarkCategory("Nested"), Benchmark]
    public int NestedMiya() => SerializeMiya(_nested, NestedDtoCodec.Instance);

    [BenchmarkCategory("Nested"), Benchmark(Baseline = true)]
    public int NestedStj() => SerializeStj(_nested, _context.NestedDto);

    [BenchmarkCategory("Escape-heavy string"), Benchmark]
    public int EscapeHeavyMiya() => SerializeMiya(_escapeHeavy, StringCodec.Instance);

    [BenchmarkCategory("Escape-heavy string"), Benchmark(Baseline = true)]
    public int EscapeHeavyStj() => SerializeStj(_escapeHeavy, _context.String);

    [BenchmarkCategory("Long string"), Benchmark]
    public int LongStringMiya() => SerializeMiya(_longString, StringCodec.Instance);

    [BenchmarkCategory("Long string"), Benchmark(Baseline = true)]
    public int LongStringStj() => SerializeStj(_longString, _context.String);

    [BenchmarkCategory("Integer-centric"), Benchmark]
    public int IntegersMiya() => SerializeMiya(_integers, IntegerPayloadCodec.Instance);

    [BenchmarkCategory("Integer-centric"), Benchmark(Baseline = true)]
    public int IntegersStj() => SerializeStj(_integers, _context.IntegerPayload);

    [BenchmarkCategory("Request bind"), Benchmark]
    public RequestDto RequestBindMiya() => MiyaJson.Deserialize(_requestJson, RequestDtoCodec.Instance)!;

    [BenchmarkCategory("Request bind"), Benchmark(Baseline = true)]
    public RequestDto RequestBindStj() => JsonSerializer.Deserialize(_requestJson, _context.RequestDto)!;

    [BenchmarkCategory("Buffer growth"), Benchmark]
    public int BufferGrowthMiya()
    {
        var buffer = new ArrayBufferWriter<byte>(16);
        MiyaJson.Serialize(buffer, _longString, StringCodec.Instance);
        return buffer.WrittenCount;
    }

    [BenchmarkCategory("Buffer growth"), Benchmark(Baseline = true)]
    public int BufferGrowthStj()
    {
        var buffer = new ArrayBufferWriter<byte>(16);
        using var writer = new Utf8JsonWriter(buffer, _stjWriterOptions);
        JsonSerializer.Serialize(writer, _longString, _context.String);
        writer.Flush();
        return buffer.WrittenCount;
    }

    private int SerializeMiya<T>(T value, IMiyaJsonCodec<T> codec)
    {
        _miyaBuffer.Clear();
        MiyaJson.Serialize(_miyaBuffer, value, codec);
        return _miyaBuffer.WrittenCount;
    }

    private int SerializeStj<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        _stjBuffer.Clear();
        _stjWriter.Reset(_stjBuffer);
        JsonSerializer.Serialize(_stjWriter, value, typeInfo);
        _stjWriter.Flush();
        return _stjBuffer.WrittenCount;
    }

    private static void ValidateEquivalent<T>(T value, IMiyaJsonCodec<T> codec, JsonTypeInfo<T> typeInfo)
    {
        var miya = new ArrayBufferWriter<byte>();
        var stj = new ArrayBufferWriter<byte>();
        MiyaJson.Serialize(miya, value, codec);
        using (var writer = new Utf8JsonWriter(stj, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            JsonSerializer.Serialize(writer, value, typeInfo);
            writer.Flush();
        }
        using var miyaDocument = JsonDocument.Parse(miya.WrittenMemory);
        using var stjDocument = JsonDocument.Parse(stj.WrittenMemory);
        if (!JsonElement.DeepEquals(miyaDocument.RootElement, stjDocument.RootElement))
        {
            throw new InvalidOperationException("The serializers produced different JSON values.");
        }
    }
}

public sealed class SmallDto
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public bool Active { get; set; }
}

public sealed class ItemDto
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public bool Enabled { get; set; }
}

public sealed class NestedDto
{
    public required string Name { get; set; }
    public int[] Values { get; set; } = [];
    public NestedDto? Child { get; set; }
}

public sealed class IntegerPayload
{
    public long Minimum { get; set; }
    public ulong Maximum { get; set; }
    public int[] Values { get; set; } = [];
}

public sealed class RequestDto
{
    public int Id { get; set; }
    public required string Query { get; set; }
    public int Page { get; set; }
    public bool IncludeDetails { get; set; }
    public List<int> Filters { get; set; } = [];
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Default,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SmallDto), TypeInfoPropertyName = "SmallDto")]
[JsonSerializable(typeof(List<ItemDto>), TypeInfoPropertyName = "ItemDtoList")]
[JsonSerializable(typeof(NestedDto), TypeInfoPropertyName = "NestedDto")]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(IntegerPayload), TypeInfoPropertyName = "IntegerPayload")]
[JsonSerializable(typeof(RequestDto), TypeInfoPropertyName = "RequestDto")]
public sealed partial class BenchmarkJsonContext : JsonSerializerContext;

internal sealed class StringCodec : IMiyaJsonCodec<string>
{
    public static StringCodec Instance { get; } = new();
    public void Write(ref MiyaJsonWriter writer, string? value) => writer.WriteString(value);
    public string? Read(ref MiyaJsonReader reader) => reader.ReadString();
}

internal sealed class SmallDtoCodec : IMiyaJsonCodec<SmallDto>
{
    public static SmallDtoCodec Instance { get; } = new();

    public void Write(ref MiyaJsonWriter writer, SmallDto? value)
    {
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteRaw("{\"id\":"u8);
        writer.WriteNumber(value.Id);
        writer.WriteRaw(",\"name\":"u8);
        writer.WriteString(value.Name);
        writer.WriteRaw(",\"active\":"u8);
        writer.WriteBool(value.Active);
        writer.WriteRaw("}"u8);
    }

    public SmallDto? Read(ref MiyaJsonReader reader) => throw new NotSupportedException();
}

internal sealed class ItemDtoListCodec : IMiyaJsonCodec<List<ItemDto>>
{
    public static ItemDtoListCodec Instance { get; } = new();

    public void Write(ref MiyaJsonWriter writer, List<ItemDto>? value)
    {
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteRaw("["u8);
        for (var index = 0; index < value.Count; index++)
        {
            if (index != 0)
            {
                writer.WriteRaw(","u8);
            }

            var item = value[index];
            writer.WriteRaw("{\"id\":"u8);
            writer.WriteNumber(item.Id);
            writer.WriteRaw(",\"name\":"u8);
            writer.WriteString(item.Name);
            writer.WriteRaw(",\"enabled\":"u8);
            writer.WriteBool(item.Enabled);
            writer.WriteRaw("}"u8);
        }

        writer.WriteRaw("]"u8);
    }

    public List<ItemDto>? Read(ref MiyaJsonReader reader) => throw new NotSupportedException();
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

        writer.WriteRaw("{\"name\":"u8);
        writer.WriteString(value.Name);
        writer.WriteRaw(",\"values\":["u8);
        WriteInt32Values(ref writer, value.Values);
        writer.WriteRaw("],\"child\":"u8);
        Write(ref writer, value.Child);
        writer.WriteRaw("}"u8);
    }

    public NestedDto? Read(ref MiyaJsonReader reader) => throw new NotSupportedException();

    private static void WriteInt32Values(ref MiyaJsonWriter writer, int[] values)
    {
        for (var index = 0; index < values.Length; index++)
        {
            if (index != 0)
            {
                writer.WriteRaw(","u8);
            }

            writer.WriteNumber(values[index]);
        }
    }
}

internal sealed class IntegerPayloadCodec : IMiyaJsonCodec<IntegerPayload>
{
    public static IntegerPayloadCodec Instance { get; } = new();

    public void Write(ref MiyaJsonWriter writer, IntegerPayload? value)
    {
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteRaw("{\"minimum\":"u8);
        writer.WriteNumber(value.Minimum);
        writer.WriteRaw(",\"maximum\":"u8);
        writer.WriteNumber(value.Maximum);
        writer.WriteRaw(",\"values\":["u8);
        for (var index = 0; index < value.Values.Length; index++)
        {
            if (index != 0)
            {
                writer.WriteRaw(","u8);
            }

            writer.WriteNumber(value.Values[index]);
        }

        writer.WriteRaw("]}"u8);
    }

    public IntegerPayload? Read(ref MiyaJsonReader reader) => throw new NotSupportedException();
}

internal sealed class RequestDtoCodec : IMiyaJsonCodec<RequestDto>
{
    public static RequestDtoCodec Instance { get; } = new();

    public void Write(ref MiyaJsonWriter writer, RequestDto? value) => throw new NotSupportedException();

    public RequestDto? Read(ref MiyaJsonReader reader)
    {
        var result = new RequestDto { Query = string.Empty };
        reader.ReadBeginObject();
        while (!reader.TryReadEndObject())
        {
            var name = reader.ReadPropertyName();
            if (name.SequenceEqual("id"u8))
            {
                result.Id = reader.ReadInt32();
            }
            else if (name.SequenceEqual("query"u8))
            {
                result.Query = reader.ReadString()!;
            }
            else if (name.SequenceEqual("page"u8))
            {
                result.Page = reader.ReadInt32();
            }
            else if (name.SequenceEqual("includeDetails"u8))
            {
                result.IncludeDetails = reader.ReadBool();
            }
            else if (name.SequenceEqual("filters"u8))
            {
                result.Filters = ReadFilters(ref reader);
            }
            else
            {
                reader.SkipValue();
            }
        }

        return result;
    }

    private static List<int> ReadFilters(ref MiyaJsonReader reader)
    {
        var result = new List<int>();
        reader.ReadBeginArray();
        while (!reader.TryReadEndArray())
        {
            result.Add(reader.ReadInt32());
        }

        return result;
    }
}
