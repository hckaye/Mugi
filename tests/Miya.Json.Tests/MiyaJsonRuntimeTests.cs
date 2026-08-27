using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Miya.Json.Tests;

public sealed class MiyaJsonRuntimeTests
{
    private static readonly JsonSerializerOptions ComparisonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void HandWrittenCodecMatchesSystemTextJsonSemanticsAndRoundTrips()
    {
        var value = CreateSample();

        var bytes = JsonTestHelpers.Serialize(value, SampleDtoCodec.Instance);

        using var actual = JsonDocument.Parse(bytes);
        using var expected = JsonSerializer.SerializeToDocument(value, ComparisonOptions);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement));

        var roundTrip = MiyaJson.Deserialize(bytes, SampleDtoCodec.Instance);
        AssertSampleEqual(value, Assert.IsType<SampleDto>(roundTrip));
    }

    [Fact]
    public void RelaxedStringEscapingOnlyEscapesJsonRequiredCharacters()
    {
        const string value = "<tag>& 'quoted' \"slash\\ control\n 日本語 😀";
        var codec = new DelegateCodec<string>(WriteString, ReadString);

        var bytes = JsonTestHelpers.Serialize(value, codec);

        Assert.Contains("<tag>& 'quoted'", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.Contains("\\\"slash\\\\ control\\n", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.Equal(value, MiyaJson.Deserialize(bytes, codec));
    }

    [Fact]
    public void EscapedPropertyNamesCompareAfterUnescapingAndDuplicatesUseLastValue()
    {
        const string json = """
            {
              "\u0069d": 1,
              "unknown": {"nested": [true, null, "skip", {"x": 2}]},
              "id": 9,
              "name": "first",
              "n\u0061me": "last",
              "tags": {"a": "first", "\u0061": "second"}
            }
            """;

        var value = MiyaJson.Deserialize(Encoding.UTF8.GetBytes(json), SampleDtoCodec.Instance);

        Assert.NotNull(value);
        Assert.Equal(9, value.Id);
        Assert.Equal("last", value.Name);
        Assert.Equal("second", value.Tags["a"]);
    }

    [Fact]
    public void MissingRequiredPropertyIsRejectedByCodec()
    {
        Assert.Throws<MiyaJsonException>(() =>
            MiyaJson.Deserialize("{\"id\":1}"u8, SampleDtoCodec.Instance));
    }

    [Theory]
    [MemberData(nameof(InvalidDocuments))]
    public void InvalidDocumentsThrowMiyaJsonException(byte[] utf8Json)
    {
        Assert.Throws<MiyaJsonException>(() => MiyaJson.Deserialize(utf8Json, SkipCodec.Instance));
    }

    [Fact]
    public void ConfiguredDepthLimitIsEnforcedBeforeEnteringTheNextContainer()
    {
        var options = new MiyaJsonOptions { MaxDepth = 2 };

        Assert.Throws<MiyaJsonException>(() =>
            MiyaJson.Deserialize("[[[]]]"u8, SkipCodec.Instance, options));
    }

    [Fact]
    public void ConfiguredStringCollectionNumberAndDocumentLimitsAreEnforced()
    {
        Assert.Throws<MiyaJsonException>(() =>
            MiyaJson.Deserialize("\"abcd\""u8, SkipCodec.Instance,
                new MiyaJsonOptions { MaxStringByteLength = 3 }));
        Assert.Throws<MiyaJsonException>(() =>
            MiyaJson.Deserialize("[1,2]"u8, SkipCodec.Instance,
                new MiyaJsonOptions { MaxCollectionSize = 1 }));
        Assert.Throws<MiyaJsonException>(() =>
            MiyaJson.Deserialize("1234"u8, SkipCodec.Instance,
                new MiyaJsonOptions { MaxNumberDigits = 3 }));
        Assert.Throws<MiyaJsonException>(() =>
            MiyaJson.Deserialize("null"u8, SkipCodec.Instance,
                new MiyaJsonOptions { MaxDocumentByteLength = 3 }));
    }

    [Fact]
    public void CancellationIsCheckedWhileParsingCollections()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var options = new MiyaJsonOptions { CancellationToken = source.Token };

        Assert.Throws<OperationCanceledException>(() =>
            MiyaJson.Deserialize("[1]"u8, SkipCodec.Instance, options));
    }

    [Fact]
    public void IntegerAndFloatingPointBoundariesRoundTrip()
    {
        Assert.Equal(int.MinValue, RoundTrip(int.MinValue, WriteInt32, ReadInt32));
        Assert.Equal(long.MinValue, RoundTrip(long.MinValue, WriteInt64, ReadInt64));
        Assert.Equal(uint.MaxValue, RoundTrip(uint.MaxValue, WriteUInt32, ReadUInt32));
        Assert.Equal(ulong.MaxValue, RoundTrip(ulong.MaxValue, WriteUInt64, ReadUInt64));
        Assert.Equal(1e308, RoundTrip(1e308, WriteDouble, ReadDouble));

        var negativeZero = RoundTrip(-0.0, WriteDouble, ReadDouble);
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0.0), BitConverter.DoubleToInt64Bits(negativeZero));
    }

    [Theory]
    [InlineData("-79228162514264337593543950335")]
    [InlineData("79228162514264337593543950335")]
    public void DecimalBoundariesRoundTrip(string text)
    {
        var value = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(value, RoundTrip(value, WriteDecimal, ReadDecimal));
    }

    [Fact]
    public void EmptyValuesAndRecursiveModelsRoundTrip()
    {
        var stringCodec = new DelegateCodec<string>(WriteString, ReadString);
        Assert.Equal(string.Empty, RoundTrip(string.Empty, WriteString, ReadString));
        Assert.True(MiyaJson.Deserialize("[]"u8, SkipCodec.Instance));
        Assert.True(MiyaJson.Deserialize("{}"u8, SkipCodec.Instance));

        var node = new RecursiveNode { Value = 1, Next = new RecursiveNode { Value = 2 } };
        var bytes = JsonTestHelpers.Serialize(node, RecursiveNodeCodec.Instance);
        var result = MiyaJson.Deserialize(bytes, RecursiveNodeCodec.Instance);
        Assert.NotNull(result);
        Assert.Equal(1, result.Value);
        Assert.Equal(2, result.Next?.Value);
        Assert.Null(result.Next?.Next);
        Assert.NotNull(stringCodec);
    }

    [Fact]
    public void NonFiniteNumbersAreRejectedByDefaultAndOptionalWhenEnabled()
    {
        var codec = new DelegateCodec<double>(WriteDouble, ReadDouble);
        Assert.Throws<MiyaJsonException>(() => JsonTestHelpers.Serialize(double.NaN, codec));
        Assert.Throws<MiyaJsonException>(() => MiyaJson.Deserialize("NaN"u8, codec));

        var options = new MiyaJsonOptions { AllowNonFiniteNumbers = true };
        var bytes = JsonTestHelpers.Serialize(double.NegativeInfinity, codec, options);
        Assert.Equal("-Infinity", Encoding.UTF8.GetString(bytes));
        Assert.Equal(double.NegativeInfinity, MiyaJson.Deserialize(bytes, codec, options));
    }

    [Fact]
    public void WriterRejectsInvalidUtf16AndConfiguredLimits()
    {
        var codec = new DelegateCodec<string>(WriteString, ReadString);
        Assert.Throws<MiyaJsonException>(() => JsonTestHelpers.Serialize("\uD800", codec));
        Assert.Throws<MiyaJsonException>(() => JsonTestHelpers.Serialize("ab", codec, new MiyaJsonOptions
        {
            MaxStringByteLength = 1,
            MaxDocumentByteLength = 16,
        }));
        Assert.Throws<MiyaJsonException>(() => JsonTestHelpers.Serialize<string>(null!, codec, new MiyaJsonOptions
        {
            MaxDocumentByteLength = 3,
        }));
    }

    [Fact]
    public void DateTimeDateTimeOffsetAndGuidUseRoundTripFormats()
    {
        var dateTime = new DateTime(2026, 8, 27, 12, 34, 56, 789, DateTimeKind.Utc).AddTicks(1234);
        var dateTimeOffset = new DateTimeOffset(2026, 8, 27, 12, 34, 56, 789, TimeSpan.FromHours(9)).AddTicks(1234);
        var guid = Guid.Parse("c56a4180-65aa-42ec-a945-5fd21dec0538");

        Assert.Equal(dateTime, RoundTrip(dateTime, WriteDateTime, ReadDateTime));
        Assert.Equal(dateTimeOffset, RoundTrip(dateTimeOffset, WriteDateTimeOffset, ReadDateTimeOffset));
        Assert.Equal(guid, RoundTrip(guid, WriteGuid, ReadGuid));
    }

    public static TheoryData<byte[]> InvalidDocuments => new()
    {
        Encoding.UTF8.GetBytes(string.Empty),
        "["u8.ToArray(),
        "{"u8.ToArray(),
        "\"unfinished"u8.ToArray(),
        "tru"u8.ToArray(),
        "nul"u8.ToArray(),
        "-"u8.ToArray(),
        "1."u8.ToArray(),
        "1e"u8.ToArray(),
        "01"u8.ToArray(),
        "[1,]"u8.ToArray(),
        "{\"a\":1,}"u8.ToArray(),
        "[1 2]"u8.ToArray(),
        "true false"u8.ToArray(),
        "\"\\x\""u8.ToArray(),
        "\"\\uZZZZ\""u8.ToArray(),
        "\"\\uD800\""u8.ToArray(),
        "\"\\uD800\\u0041\""u8.ToArray(),
        "\"\\uDC00\""u8.ToArray(),
        new byte[] { (byte)'"', 0xC3, 0x28, (byte)'"' },
        new byte[] { (byte)'"', 0xED, 0xA0, 0x80, (byte)'"' },
        new byte[] { (byte)'"', 0xF4, 0x90, 0x80, 0x80, (byte)'"' },
    };

    private static T RoundTrip<T>(T value, WriteValue<T> write, ReadValue<T> read)
    {
        var codec = new DelegateCodec<T>(write, read);
        var bytes = JsonTestHelpers.Serialize(value, codec);
        return Assert.IsType<T>(MiyaJson.Deserialize(bytes, codec));
    }

    private static void WriteString(ref MiyaJsonWriter writer, string? value) => writer.WriteString(value);
    private static string? ReadString(ref MiyaJsonReader reader) => reader.ReadString();
    private static void WriteInt32(ref MiyaJsonWriter writer, int value) => writer.WriteNumber(value);
    private static int ReadInt32(ref MiyaJsonReader reader) => reader.ReadInt32();
    private static void WriteInt64(ref MiyaJsonWriter writer, long value) => writer.WriteNumber(value);
    private static long ReadInt64(ref MiyaJsonReader reader) => reader.ReadInt64();
    private static void WriteUInt32(ref MiyaJsonWriter writer, uint value) => writer.WriteNumber(value);
    private static uint ReadUInt32(ref MiyaJsonReader reader) => reader.ReadUInt32();
    private static void WriteUInt64(ref MiyaJsonWriter writer, ulong value) => writer.WriteNumber(value);
    private static ulong ReadUInt64(ref MiyaJsonReader reader) => reader.ReadUInt64();
    private static void WriteDouble(ref MiyaJsonWriter writer, double value) => writer.WriteNumber(value);
    private static double ReadDouble(ref MiyaJsonReader reader) => reader.ReadDouble();
    private static void WriteDecimal(ref MiyaJsonWriter writer, decimal value) => writer.WriteNumber(value);
    private static decimal ReadDecimal(ref MiyaJsonReader reader) => reader.ReadDecimal();
    private static void WriteDateTime(ref MiyaJsonWriter writer, DateTime value) => writer.WriteDateTime(value);
    private static DateTime ReadDateTime(ref MiyaJsonReader reader) => reader.ReadDateTime();
    private static void WriteDateTimeOffset(ref MiyaJsonWriter writer, DateTimeOffset value) => writer.WriteDateTimeOffset(value);
    private static DateTimeOffset ReadDateTimeOffset(ref MiyaJsonReader reader) => reader.ReadDateTimeOffset();
    private static void WriteGuid(ref MiyaJsonWriter writer, Guid value) => writer.WriteGuid(value);
    private static Guid ReadGuid(ref MiyaJsonReader reader) => reader.ReadGuid();

    private static SampleDto CreateSample() => new()
    {
        Id = int.MinValue,
        Name = "Miya \"runtime\" 日本語 😀",
        Active = true,
        Scores = [0, -1, int.MaxValue],
        Tags = new Dictionary<string, string?>
        {
            ["empty"] = string.Empty,
            ["nullable"] = null,
            ["escaped\\key"] = "line\nvalue",
        },
        Details = new NestedDto { Count = long.MinValue, Note = "nested" },
        Optional = 42,
        State = SampleState.Ready,
        CreatedAt = new DateTime(2026, 8, 27, 1, 2, 3, DateTimeKind.Utc).AddTicks(4567),
        UpdatedAt = new DateTimeOffset(2026, 8, 27, 10, 2, 3, TimeSpan.FromHours(9)).AddTicks(4567),
        RequestId = Guid.Parse("c56a4180-65aa-42ec-a945-5fd21dec0538"),
        Balance = decimal.MinValue,
        Node = new RecursiveNode { Value = 1, Next = new RecursiveNode { Value = 2 } },
    };

    private static void AssertSampleEqual(SampleDto expected, SampleDto actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Active, actual.Active);
        Assert.Equal(expected.Scores, actual.Scores);
        Assert.Equal(expected.Tags, actual.Tags);
        Assert.Equal(expected.Details?.Count, actual.Details?.Count);
        Assert.Equal(expected.Details?.Note, actual.Details?.Note);
        Assert.Equal(expected.Optional, actual.Optional);
        Assert.Equal(expected.State, actual.State);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.UpdatedAt, actual.UpdatedAt);
        Assert.Equal(expected.RequestId, actual.RequestId);
        Assert.Equal(expected.Balance, actual.Balance);
        Assert.Equal(expected.Node?.Value, actual.Node?.Value);
        Assert.Equal(expected.Node?.Next?.Value, actual.Node?.Next?.Value);
    }
}
