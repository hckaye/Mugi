using System.Buffers;

namespace Miya;

internal sealed class PooledByteBufferWriter : IBufferWriter<byte>, IDisposable
{
    private byte[]? _buffer;
    private int _written;

    public int WrittenCount => _written;

    public ReadOnlyMemory<byte> WrittenMemory => _buffer is null
        ? ReadOnlyMemory<byte>.Empty
        : _buffer.AsMemory(0, _written);

    public void Advance(int count)
    {
        if (count < 0 || _buffer is null || count > _buffer.Length - _written)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer!.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    public void Clear() => _written = 0;

    public void Reset(int maxRetainedBytes)
    {
        _written = 0;
        if (_buffer is not null && _buffer.Length > maxRetainedBytes)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null;
        }
    }

    public void Dispose()
    {
        if (_buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null;
        }

        _written = 0;
    }

    private void EnsureCapacity(int sizeHint)
    {
        if (sizeHint < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeHint));
        }

        if (sizeHint == 0)
        {
            sizeHint = 1;
        }

        var required = checked(_written + sizeHint);
        if (_buffer is not null && required <= _buffer.Length)
        {
            return;
        }

        var newSize = Math.Max(required, _buffer is null ? 256 : checked(_buffer.Length * 2));
        var replacement = ArrayPool<byte>.Shared.Rent(newSize);
        if (_buffer is not null)
        {
            _buffer.AsSpan(0, _written).CopyTo(replacement);
            ArrayPool<byte>.Shared.Return(_buffer);
        }

        _buffer = replacement;
    }
}
