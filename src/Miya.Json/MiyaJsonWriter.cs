using System.Buffers;

namespace Miya.Json;

/// <summary>
/// Writes UTF-8 JSON directly into an <see cref="IBufferWriter{Byte}"/>. This is the frozen
/// public contract; the implementation lands with the MiyaJson runtime unit (milestone 3).
/// </summary>
public ref struct MiyaJsonWriter
{
    public MiyaJsonWriter(IBufferWriter<byte> destination, MiyaJsonOptions options)
        => throw new NotImplementedException("MiyaJson runtime is implemented in milestone 3.");

    /// <summary>Writes pre-encoded JSON fragments (property-name literals, structural tokens).</summary>
    public void WriteRaw(scoped ReadOnlySpan<byte> utf8) => throw new NotImplementedException();

    public void WriteNull() => throw new NotImplementedException();

    public void WriteBool(bool value) => throw new NotImplementedException();

    public void WriteString(string? value) => throw new NotImplementedException();

    public void WriteString(scoped ReadOnlySpan<char> value) => throw new NotImplementedException();

    public void WriteNumber(int value) => throw new NotImplementedException();

    public void WriteNumber(long value) => throw new NotImplementedException();

    public void WriteNumber(uint value) => throw new NotImplementedException();

    public void WriteNumber(ulong value) => throw new NotImplementedException();

    public void WriteNumber(float value) => throw new NotImplementedException();

    public void WriteNumber(double value) => throw new NotImplementedException();

    public void WriteNumber(decimal value) => throw new NotImplementedException();

    public void WriteGuid(Guid value) => throw new NotImplementedException();

    public void WriteDateTime(DateTime value) => throw new NotImplementedException();

    public void WriteDateTimeOffset(DateTimeOffset value) => throw new NotImplementedException();

    /// <summary>Flushes any pending bytes to the destination writer.</summary>
    public void Flush() => throw new NotImplementedException();
}
