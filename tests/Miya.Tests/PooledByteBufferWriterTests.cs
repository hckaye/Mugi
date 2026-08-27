using System.Buffers;

namespace Miya.Tests;

public sealed class PooledByteBufferWriterTests
{
    [Fact(Timeout = 10_000)]
    public async Task BufferLargerThanPoolingThresholdIsDiscarded()
    {
        var pool = new RecordingArrayPool();
        using (var writer = new PooledByteBufferWriter(
            maxPooledBufferByteLength: 4,
            pool: pool))
        {
            writer.GetSpan(5)[..5].Fill(1);
            writer.Advance(5);
        }

        Assert.True(Assert.Single(pool.RentSizes) >= 5);
        Assert.Empty(pool.Returned);
        await Task.CompletedTask;
    }

    private sealed class RecordingArrayPool : ArrayPool<byte>
    {
        public List<int> RentSizes { get; } = [];

        public List<byte[]> Returned { get; } = [];

        public override byte[] Rent(int minimumLength)
        {
            RentSizes.Add(minimumLength);
            return new byte[minimumLength];
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            Returned.Add(array);
        }
    }
}
