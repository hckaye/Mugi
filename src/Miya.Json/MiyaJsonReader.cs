namespace Miya.Json;

/// <summary>
/// Reads a complete UTF-8 JSON document from a span. This is the frozen public contract;
/// the implementation lands with the MiyaJson runtime unit (milestone 3).
/// </summary>
public ref struct MiyaJsonReader
{
    public MiyaJsonReader(ReadOnlySpan<byte> utf8Json, MiyaJsonOptions options)
        => throw new NotImplementedException("MiyaJson runtime is implemented in milestone 3.");

    /// <summary>Consumes a null token if one is next. Returns whether it did.</summary>
    public bool TryReadNull() => throw new NotImplementedException();

    public bool ReadBool() => throw new NotImplementedException();

    public int ReadInt32() => throw new NotImplementedException();

    public long ReadInt64() => throw new NotImplementedException();

    public uint ReadUInt32() => throw new NotImplementedException();

    public ulong ReadUInt64() => throw new NotImplementedException();

    public float ReadSingle() => throw new NotImplementedException();

    public double ReadDouble() => throw new NotImplementedException();

    public decimal ReadDecimal() => throw new NotImplementedException();

    public string? ReadString() => throw new NotImplementedException();

    public Guid ReadGuid() => throw new NotImplementedException();

    public DateTime ReadDateTime() => throw new NotImplementedException();

    public DateTimeOffset ReadDateTimeOffset() => throw new NotImplementedException();

    public void ReadBeginObject() => throw new NotImplementedException();

    /// <summary>Consumes the object terminator if it is next. Returns whether the object ended.</summary>
    public bool TryReadEndObject() => throw new NotImplementedException();

    /// <summary>Reads the next property name and returns its unescaped UTF-8 bytes.</summary>
    public ReadOnlySpan<byte> ReadPropertyName() => throw new NotImplementedException();

    public void ReadBeginArray() => throw new NotImplementedException();

    /// <summary>Consumes the array terminator if it is next. Returns whether the array ended.</summary>
    public bool TryReadEndArray() => throw new NotImplementedException();

    /// <summary>Skips one complete value of any kind.</summary>
    public void SkipValue() => throw new NotImplementedException();

    /// <summary>Asserts that only whitespace remains after the document.</summary>
    public void ExpectEnd() => throw new NotImplementedException();
}
