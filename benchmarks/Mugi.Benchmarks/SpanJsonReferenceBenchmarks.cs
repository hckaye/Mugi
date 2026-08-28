using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;
using SpanJson.Resolvers;

namespace Mugi.Benchmarks;

/// <summary>
/// JIT-only reference measurements. SpanJson 4.2.1 returns byte arrays rather than accepting an
/// IBufferWriter, so these results are not used as the pass/fail baseline.
/// </summary>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class SpanJsonReferenceBenchmarks
{
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
        _small = new SmallDto { Id = 42, Name = "Mugi", Active = true };
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
        _requestJson = Serialize(request);
    }

    [BenchmarkCategory("Small DTO"), Benchmark]
    public int SmallDtoSpanJson() => Serialize(_small).Length;

    [BenchmarkCategory("List 100"), Benchmark]
    public int List100SpanJson() => Serialize(_items).Length;

    [BenchmarkCategory("Nested"), Benchmark]
    public int NestedSpanJson() => Serialize(_nested).Length;

    [BenchmarkCategory("Escape-heavy string"), Benchmark]
    public int EscapeHeavySpanJson() => Serialize(_escapeHeavy).Length;

    [BenchmarkCategory("Long string"), Benchmark]
    public int LongStringSpanJson() => Serialize(_longString).Length;

    [BenchmarkCategory("Integer-centric"), Benchmark]
    public int IntegersSpanJson() => Serialize(_integers).Length;

    [BenchmarkCategory("Request bind"), Benchmark]
    public RequestDto RequestBindSpanJson() => Deserialize<RequestDto>(_requestJson);

    private static byte[] Serialize<T>(T value) =>
        SpanJson.JsonSerializer.Generic.Utf8.Serialize<T, IncludeNullsCamelCaseResolver<byte>>(value);

    private static T Deserialize<T>(byte[] json) =>
        SpanJson.JsonSerializer.Generic.Utf8.Deserialize<T, IncludeNullsCamelCaseResolver<byte>>(json);
}
