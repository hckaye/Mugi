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
        IncludeGeneratedCodecs();
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

        ValidateEquivalent(_small, _context.SmallDto);
        ValidateEquivalent(_items, _context.ItemDtoList);
        ValidateEquivalent(_nested, _context.NestedDto);
        ValidateEquivalent(_escapeHeavy, _context.String);
        ValidateEquivalent(_longString, _context.String);
        ValidateEquivalent(_integers, _context.IntegerPayload);
        ValidateInputContracts();

        _ = SmallDtoMiya();
        _ = SmallDtoStj();
        _miyaBuffer.Clear();
        _stjBuffer.Clear();
    }

    [BenchmarkCategory("Small DTO"), Benchmark]
    public int SmallDtoMiya() => SerializeMiya(_small);

    [BenchmarkCategory("Small DTO"), Benchmark(Baseline = true)]
    public int SmallDtoStj() => SerializeStj(_small, _context.SmallDto);

    [BenchmarkCategory("List 100"), Benchmark]
    public int List100Miya() => SerializeMiya(_items);

    [BenchmarkCategory("List 100"), Benchmark(Baseline = true)]
    public int List100Stj() => SerializeStj(_items, _context.ItemDtoList);

    [BenchmarkCategory("Nested"), Benchmark]
    public int NestedMiya() => SerializeMiya(_nested);

    [BenchmarkCategory("Nested"), Benchmark(Baseline = true)]
    public int NestedStj() => SerializeStj(_nested, _context.NestedDto);

    [BenchmarkCategory("Escape-heavy string"), Benchmark]
    public int EscapeHeavyMiya() => SerializeMiya(_escapeHeavy);

    [BenchmarkCategory("Escape-heavy string"), Benchmark(Baseline = true)]
    public int EscapeHeavyStj() => SerializeStj(_escapeHeavy, _context.String);

    [BenchmarkCategory("Long string"), Benchmark]
    public int LongStringMiya() => SerializeMiya(_longString);

    [BenchmarkCategory("Long string"), Benchmark(Baseline = true)]
    public int LongStringStj() => SerializeStj(_longString, _context.String);

    [BenchmarkCategory("Integer-centric"), Benchmark]
    public int IntegersMiya() => SerializeMiya(_integers);

    [BenchmarkCategory("Integer-centric"), Benchmark(Baseline = true)]
    public int IntegersStj() => SerializeStj(_integers, _context.IntegerPayload);

    [BenchmarkCategory("Request bind"), Benchmark]
    public RequestDto RequestBindMiya() => MiyaJson.Deserialize<RequestDto>(_requestJson)!;

    [BenchmarkCategory("Request bind"), Benchmark(Baseline = true)]
    public RequestDto RequestBindStj() => JsonSerializer.Deserialize(_requestJson, _context.RequestDto)!;

    [BenchmarkCategory("Buffer growth"), Benchmark]
    public int BufferGrowthMiya()
    {
        var buffer = new ArrayBufferWriter<byte>(16);
        MiyaJson.Serialize(buffer, _longString);
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

    private int SerializeMiya<T>(T value)
    {
        _miyaBuffer.Clear();
        MiyaJson.Serialize(_miyaBuffer, value);
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

    private static void ValidateEquivalent<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        var miya = new ArrayBufferWriter<byte>();
        var stj = new ArrayBufferWriter<byte>();
        MiyaJson.Serialize(miya, value);
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

    private static void IncludeGeneratedCodecs()
    {
        MiyaJson.Include<SmallDto>();
        // STJ does not enforce nullable annotations for collection elements, so match that contract.
        MiyaJson.Include<List<ItemDto?>>();
        MiyaJson.Include<NestedDto>();
        MiyaJson.Include<string>();
        MiyaJson.Include<IntegerPayload>();
        MiyaJson.Include<RequestDto>();
    }

    private void ValidateInputContracts()
    {
        ValidateMiyaRejects(
            """{"id":12345,"page":7,"includeDetails":true,"filters":[3,5]}"""u8);
        ValidateStjRejects(
            """{"id":12345,"page":7,"includeDetails":true,"filters":[3,5]}"""u8);
        ValidateMiyaRejects(
            """{"id":12345,"query":null,"page":7,"includeDetails":true,"filters":[3,5]}"""u8);
        ValidateStjRejects(
            """{"id":12345,"query":null,"page":7,"includeDetails":true,"filters":[3,5]}"""u8);
    }

    private static void ValidateMiyaRejects(ReadOnlySpan<byte> json)
    {
        try
        {
            _ = MiyaJson.Deserialize<RequestDto>(json);
        }
        catch (MiyaJsonException exception) when (exception.IsInputError)
        {
            return;
        }

        throw new InvalidOperationException("Miya accepted input that violates the benchmark contract.");
    }

    private void ValidateStjRejects(ReadOnlySpan<byte> json)
    {
        try
        {
            _ = JsonSerializer.Deserialize(json, _context.RequestDto);
        }
        catch (JsonException)
        {
            return;
        }

        throw new InvalidOperationException("STJ accepted input that violates the benchmark contract.");
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
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(SmallDto), TypeInfoPropertyName = "SmallDto")]
[JsonSerializable(typeof(List<ItemDto>), TypeInfoPropertyName = "ItemDtoList")]
[JsonSerializable(typeof(NestedDto), TypeInfoPropertyName = "NestedDto")]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(IntegerPayload), TypeInfoPropertyName = "IntegerPayload")]
[JsonSerializable(typeof(RequestDto), TypeInfoPropertyName = "RequestDto")]
public sealed partial class BenchmarkJsonContext : JsonSerializerContext;
