using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Mugi.Json.Tests;

public sealed class JsonRuntimeTests
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

        var roundTrip = Json.Deserialize(bytes, SampleDtoCodec.Instance);
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
        Assert.Equal(value, Json.Deserialize(bytes, codec));
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

        var value = Json.Deserialize(Encoding.UTF8.GetBytes(json), SampleDtoCodec.Instance);

        Assert.NotNull(value);
        Assert.Equal(9, value.Id);
        Assert.Equal("last", value.Name);
        Assert.Equal("second", value.Tags["a"]);
    }

    [Fact]
    public void MissingRequiredPropertyIsRejectedByCodec()
    {
        Assert.Throws<JsonException>(() =>
            Json.Deserialize("{\"id\":1}"u8, SampleDtoCodec.Instance));
    }

    [Theory]
    [MemberData(nameof(InvalidDocuments))]
    public void InvalidDocumentsThrowJsonException(byte[] utf8Json)
    {
        Assert.Throws<JsonException>(() => Json.Deserialize(utf8Json, SkipCodec.Instance));
    }

    [Fact]
    public void ConfiguredDepthLimitIsEnforcedBeforeEnteringTheNextContainer()
    {
        var options = new JsonOptions { MaxDepth = 2 };

        Assert.Throws<JsonException>(() =>
            Json.Deserialize("[[[]]]"u8, SkipCodec.Instance, options));
    }

    [Fact]
    public void ConfiguredStringCollectionNumberAndDocumentLimitsAreEnforced()
    {
        Assert.Throws<JsonException>(() =>
            Json.Deserialize("\"abcd\""u8, SkipCodec.Instance,
                new JsonOptions { MaxStringByteLength = 3 }));
        Assert.Throws<JsonException>(() =>
            Json.Deserialize("[1,2]"u8, SkipCodec.Instance,
                new JsonOptions { MaxCollectionSize = 1 }));
        Assert.Throws<JsonException>(() =>
            Json.Deserialize("1234"u8, SkipCodec.Instance,
                new JsonOptions { MaxNumberDigits = 3 }));
        Assert.Throws<JsonException>(() =>
            Json.Deserialize("null"u8, SkipCodec.Instance,
                new JsonOptions { MaxDocumentByteLength = 3 }));
    }

    [Fact]
    public void CancellationIsCheckedWhileParsingCollectionsAndLongScans()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var options = new JsonOptions
        {
            CancellationToken = source.Token,
            MaxDepth = 512,
            MaxNumberDigits = 32 * 1024,
        };

        var collection = Encoding.UTF8.GetBytes("[" + string.Join(',', Enumerable.Repeat("1", 10_000)) + "]");
        var deep = Encoding.UTF8.GetBytes(new string('[', 256) + "null" + new string(']', 256));
        var whitespace = Encoding.UTF8.GetBytes(new string(' ', 32 * 1024) + "null");
        var number = Encoding.UTF8.GetBytes(new string('1', 32 * 1024));

        Assert.Throws<OperationCanceledException>(() =>
            Json.Deserialize(collection, SkipCodec.Instance, options));
        Assert.Throws<OperationCanceledException>(() =>
            Json.Deserialize(deep, SkipCodec.Instance, options));
        Assert.Throws<OperationCanceledException>(() =>
            Json.Deserialize(whitespace, SkipCodec.Instance, options));
        Assert.Throws<OperationCanceledException>(() =>
            Json.Deserialize(number, SkipCodec.Instance, options));
    }

    [Fact]
    public void ExceptionClassificationDistinguishesInputFromCodecConfigurationErrors()
    {
        var input = Assert.Throws<JsonException>(() =>
            Json.Deserialize("[1,]"u8, SkipCodec.Instance));
        var configuration = Assert.Throws<JsonException>(() =>
            Json.GetCodec<UnregisteredType>());

        Assert.True(input.IsInputError);
        Assert.False(configuration.IsInputError);
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

    [Fact]
    public void UInt64DigitBoundariesRoundTrip()
    {
        ulong power = 1;
        for (var digits = 1; digits <= 20; digits++)
        {
            if (power > 1)
            {
                Assert.Equal(power - 1, RoundTrip(power - 1, WriteUInt64, ReadUInt64));
            }

            Assert.Equal(power, RoundTrip(power, WriteUInt64, ReadUInt64));
            if (digits == 20)
            {
                break;
            }

            power *= 10;
        }
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
        Assert.True(Json.Deserialize("[]"u8, SkipCodec.Instance));
        Assert.True(Json.Deserialize("{}"u8, SkipCodec.Instance));

        var node = new RecursiveNode { Value = 1, Next = new RecursiveNode { Value = 2 } };
        var bytes = JsonTestHelpers.Serialize(node, RecursiveNodeCodec.Instance);
        var result = Json.Deserialize(bytes, RecursiveNodeCodec.Instance);
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
        Assert.Throws<JsonException>(() => JsonTestHelpers.Serialize(double.NaN, codec));
        Assert.Throws<JsonException>(() => Json.Deserialize("NaN"u8, codec));

        var options = new JsonOptions { AllowNonFiniteNumbers = true };
        var bytes = JsonTestHelpers.Serialize(double.NegativeInfinity, codec, options);
        Assert.Equal("-Infinity", Encoding.UTF8.GetString(bytes));
        Assert.Equal(double.NegativeInfinity, Json.Deserialize(bytes, codec, options));
    }

    [Fact]
    public void WriterRejectsInvalidUtf16AndConfiguredLimits()
    {
        var codec = new DelegateCodec<string>(WriteString, ReadString);
        Assert.Throws<JsonException>(() => JsonTestHelpers.Serialize("\uD800", codec));
        Assert.Throws<JsonException>(() => JsonTestHelpers.Serialize("ab", codec, new JsonOptions
        {
            MaxStringByteLength = 1,
            MaxDocumentByteLength = 16,
        }));
        Assert.Throws<JsonException>(() => JsonTestHelpers.Serialize<string>(null!, codec, new JsonOptions
        {
            MaxDocumentByteLength = 3,
        }));
    }

    [Fact]
    public void WriterDoesNotSplitSurrogatePairsAtChunkBoundaries()
    {
        var value = new string('x', 2047) + "😀tail";
        Assert.Equal(value, RoundTrip(value, WriteString, ReadString));
    }

    [Fact]
    public void LongUnescapedNonAsciiStringRoundTrips()
    {
        var value = string.Concat(Enumerable.Repeat("日本語😀", 1024));
        Assert.Equal(value, RoundTrip(value, WriteString, ReadString));
    }

    [Fact]
    public void LongEscapedAsciiStringRoundTrips()
    {
        var value = new string('x', 4096) + "\"\\\n\t";
        Assert.Equal(value, RoundTrip(value, WriteString, ReadString));
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
        return Assert.IsType<T>(Json.Deserialize(bytes, codec));
    }

    private static void WriteString(ref JsonWriter writer, string? value) => writer.WriteString(value);
    private static string? ReadString(ref JsonReader reader) => reader.ReadString();
    private static void WriteInt32(ref JsonWriter writer, int value) => writer.WriteNumber(value);
    private static int ReadInt32(ref JsonReader reader) => reader.ReadInt32();
    private static void WriteInt64(ref JsonWriter writer, long value) => writer.WriteNumber(value);
    private static long ReadInt64(ref JsonReader reader) => reader.ReadInt64();
    private static void WriteUInt32(ref JsonWriter writer, uint value) => writer.WriteNumber(value);
    private static uint ReadUInt32(ref JsonReader reader) => reader.ReadUInt32();
    private static void WriteUInt64(ref JsonWriter writer, ulong value) => writer.WriteNumber(value);
    private static ulong ReadUInt64(ref JsonReader reader) => reader.ReadUInt64();
    private static void WriteDouble(ref JsonWriter writer, double value) => writer.WriteNumber(value);
    private static double ReadDouble(ref JsonReader reader) => reader.ReadDouble();
    private static void WriteDecimal(ref JsonWriter writer, decimal value) => writer.WriteNumber(value);
    private static decimal ReadDecimal(ref JsonReader reader) => reader.ReadDecimal();
    private static void WriteDateTime(ref JsonWriter writer, DateTime value) => writer.WriteDateTime(value);
    private static DateTime ReadDateTime(ref JsonReader reader) => reader.ReadDateTime();
    private static void WriteDateTimeOffset(ref JsonWriter writer, DateTimeOffset value) => writer.WriteDateTimeOffset(value);
    private static DateTimeOffset ReadDateTimeOffset(ref JsonReader reader) => reader.ReadDateTimeOffset();
    private static void WriteGuid(ref JsonWriter writer, Guid value) => writer.WriteGuid(value);
    private static Guid ReadGuid(ref JsonReader reader) => reader.ReadGuid();

    private static SampleDto CreateSample() => new()
    {
        Id = int.MinValue,
        Name = "Mugi \"runtime\" 日本語 😀",
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

internal sealed class UnregisteredType
{
}
