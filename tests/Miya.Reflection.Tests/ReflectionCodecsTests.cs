using System.Buffers;
using System.Text;
using Miya.Json;
using JsonRuntime = Miya.Json.Json;

namespace Miya.Reflection.Tests;

public sealed class ReflectionCodecsTests : IDisposable
{
    public ReflectionCodecsTests()
    {
        ReflectionCodecs.Disable();
    }

    public void Dispose()
    {
        ReflectionCodecs.Disable();
    }

    [Fact]
    public void RecordWithNestedCollectionsAndValuesRoundTripsWithoutGeneratedCode()
    {
        ReflectionCodecs.Enable();
        var createdAt = new DateTime(2026, 8, 28, 9, 10, 11, DateTimeKind.Utc).AddTicks(1234);
        var value = new RuntimeRecord(
            "Ada",
            new RuntimeAddress("Tokyo", 100),
            [new RuntimeItem(1, RuntimeState.Ready), new RuntimeItem(2, null)],
            new Dictionary<string, int?>
            {
                ["first"] = 10,
                ["missing"] = null,
            },
            createdAt);

        var json = Serialize(value);
        var result = JsonRuntime.Deserialize<RuntimeRecord>(json);

        Assert.NotNull(result);
        Assert.Equal(value.DisplayName, result.DisplayName);
        Assert.Equal(value.Address, result.Address);
        Assert.Equal(value.Items, result.Items);
        Assert.Equal(value.Scores, result.Scores);
        Assert.Equal(value.CreatedAt, result.CreatedAt);
    }

    [Fact]
    public void MutablePocoArrayAndScalarTypesRoundTrip()
    {
        ReflectionCodecs.Enable();
        var value = new MutablePayload
        {
            RequestId = Guid.Parse("c56a4180-65aa-42ec-a945-5fd21dec0538"),
            UpdatedAt = new DateTimeOffset(2026, 8, 28, 18, 10, 11, TimeSpan.FromHours(9)),
            Balance = 123.45m,
            Initial = 'M',
            Values = [int.MinValue, 0, int.MaxValue],
        };

        var result = JsonRuntime.Deserialize<MutablePayload>(Serialize(value));

        Assert.NotNull(result);
        Assert.Equal(value.RequestId, result.RequestId);
        Assert.Equal(value.UpdatedAt, result.UpdatedAt);
        Assert.Equal(value.Balance, result.Balance);
        Assert.Equal(value.Initial, result.Initial);
        Assert.Equal(value.Values, result.Values);
    }

    [Fact]
    public void PrimitiveAndNullableValuesRoundTrip()
    {
        ReflectionCodecs.Enable();

        Assert.True(RoundTrip(true));
        Assert.Equal(byte.MaxValue, RoundTrip(byte.MaxValue));
        Assert.Equal(sbyte.MinValue, RoundTrip(sbyte.MinValue));
        Assert.Equal(short.MinValue, RoundTrip(short.MinValue));
        Assert.Equal(ushort.MaxValue, RoundTrip(ushort.MaxValue));
        Assert.Equal(int.MinValue, RoundTrip(int.MinValue));
        Assert.Equal(uint.MaxValue, RoundTrip(uint.MaxValue));
        Assert.Equal(long.MinValue, RoundTrip(long.MinValue));
        Assert.Equal(ulong.MaxValue, RoundTrip(ulong.MaxValue));
        Assert.Equal(1.25f, RoundTrip(1.25f));
        Assert.Equal(-2.5d, RoundTrip(-2.5d));
        Assert.Equal("text", RoundTrip("text"));
        Assert.Equal(42, RoundTrip<int?>(42));
        Assert.Null(RoundTrip<int?>(null));
    }

    [Fact]
    public void PropertyNamesUseCamelCaseAndStringsUseJsonEscaping()
    {
        ReflectionCodecs.Enable();
        var value = new NamingPayload("quoted \"value\"", "ok");

        var json = Encoding.UTF8.GetString(Serialize(value));

        Assert.Equal("{\"displayName\":\"quoted \\\"value\\\"\",\"urlValue\":\"ok\"}", json);
    }

    [Fact]
    public void Record_constructor_parameters_enforce_presence_and_honor_defaults()
    {
        ReflectionCodecs.Enable();

        var value = JsonRuntime.Deserialize<RuntimeDefault>("{}"u8);
        var nullable = JsonRuntime.Deserialize<RuntimeNullable>("{\"value\":null}"u8);

        Assert.Equal(new RuntimeDefault(), value);
        Assert.Null(nullable!.Value);
        AssertMissingField(() => JsonRuntime.Deserialize<RuntimeRequired>("{}"u8), "id");
        AssertMissingField(
            () => JsonRuntime.Deserialize<RuntimeOuter>("{\"inner\":{}}"u8),
            "id");
        AssertMissingField(
            () => JsonRuntime.Deserialize<RuntimeRequired[]>("[{}]"u8),
            "id");
    }

    [Fact]
    public void CircularReferencesAreStoppedByConfiguredMaxDepth()
    {
        ReflectionCodecs.Enable();
        var node = new RuntimeNode();
        node.Next = node;

        Assert.Throws<JsonException>(() => Serialize(node, new JsonOptions { MaxDepth = 4 }));
    }

    [Fact]
    public void DisableRestoresMissingCodecBehaviorForPreviouslyResolvedType()
    {
        Assert.Throws<JsonException>(() => Serialize(new DisablePayload { Value = 1 }));

        ReflectionCodecs.Enable();
        Assert.Equal("{\"value\":1}", Encoding.UTF8.GetString(
            Serialize(new DisablePayload { Value = 1 })));

        ReflectionCodecs.Disable();
        Assert.Throws<JsonException>(() => Serialize(new DisablePayload { Value = 1 }));
        Assert.Throws<JsonException>(() => JsonRuntime.Deserialize<DisablePayload>("{\"value\":1}"u8));
    }

    [Fact]
    public void RegisteredCodecTakesPriorityOverReflectionFallback()
    {
        ReflectionCodecs.Enable();
        JsonRuntime.Register<RegisteredPayload>(RegisteredPayloadCodec.Instance);

        Assert.Equal("\"registered\"", Encoding.UTF8.GetString(
            Serialize(new RegisteredPayload())));
    }

    private static byte[] Serialize<T>(T value, JsonOptions? options = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        JsonRuntime.Serialize(buffer, value, options);
        return buffer.WrittenSpan.ToArray();
    }

    private static T? RoundTrip<T>(T value) => JsonRuntime.Deserialize<T>(Serialize(value));

    private static void AssertMissingField(Action action, string field)
    {
        var exception = Assert.Throws<JsonException>(action);
        Assert.True(exception.IsInputError);
        Assert.Contains("'" + field + "'", exception.Message, StringComparison.Ordinal);
    }
}

internal enum RuntimeState
{
    Unknown,
    Ready = 7,
}

internal sealed record RuntimeAddress(string City, int PostalCode);

internal sealed record RuntimeItem(int Id, RuntimeState? State);

internal sealed record RuntimeRecord(
    string DisplayName,
    RuntimeAddress Address,
    List<RuntimeItem> Items,
    Dictionary<string, int?> Scores,
    DateTime CreatedAt);

internal sealed record NamingPayload(string DisplayName, string URLValue);

internal sealed record RuntimeRequired(string Id, string Name);

internal sealed record RuntimeDefault(int Count = 42, string Label = "default");

internal sealed record RuntimeNullable(string? Value);

internal sealed record RuntimeOuter(RuntimeRequired Inner);

internal sealed class MutablePayload
{
    public MutablePayload()
    {
    }

    public Guid RequestId { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public decimal Balance { get; set; }

    public char Initial { get; set; }

    public int[] Values { get; set; } = [];
}

internal sealed class RuntimeNode
{
    public RuntimeNode()
    {
    }

    public RuntimeNode? Next { get; set; }
}

internal sealed class DisablePayload
{
    public DisablePayload()
    {
    }

    public int Value { get; set; }
}

internal sealed class RegisteredPayload
{
}

internal sealed class RegisteredPayloadCodec : IJsonCodec<RegisteredPayload>
{
    internal static RegisteredPayloadCodec Instance { get; } = new();

    public void Write(ref JsonWriter writer, RegisteredPayload? value) =>
        writer.WriteString("registered");

    public RegisteredPayload? Read(ref JsonReader reader) => throw new NotSupportedException();
}
