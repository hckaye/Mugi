using System.Buffers;

namespace Mugi;

internal sealed class PooledByteBufferWriter : IBufferWriter<byte>, IDisposable
{
    private readonly ArrayPool<byte> _pool;
    private readonly int _maxPooledBufferByteLength;
    private byte[]? _buffer;
    private int _written;

    public PooledByteBufferWriter(
        int maxPooledBufferByteLength = int.MaxValue,
        ArrayPool<byte>? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxPooledBufferByteLength);
        _maxPooledBufferByteLength = maxPooledBufferByteLength;
        _pool = pool ?? ArrayPool<byte>.Shared;
    }

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
            ReturnIfPooled(_buffer);
            _buffer = null;
        }
    }

    public void Dispose()
    {
        if (_buffer is not null)
        {
            ReturnIfPooled(_buffer);
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
        var replacement = _pool.Rent(newSize);
        if (_buffer is not null)
        {
            _buffer.AsSpan(0, _written).CopyTo(replacement);
            ReturnIfPooled(_buffer);
        }

        _buffer = replacement;
    }

    private void ReturnIfPooled(byte[] buffer)
    {
        if (buffer.Length <= _maxPooledBufferByteLength)
        {
            _pool.Return(buffer);
        }
    }
}
