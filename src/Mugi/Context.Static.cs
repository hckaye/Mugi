using System.IO.Pipelines;

namespace Mugi;

public partial class Context
{
    internal async ValueTask Stream(
        string contentType,
        long contentLength,
        Func<PipeWriter, CancellationToken, ValueTask> write)
    {
        EnsureActive();
        ArgumentException.ThrowIfNullOrEmpty(contentType);
        ArgumentOutOfRangeException.ThrowIfNegative(contentLength);
        ArgumentNullException.ThrowIfNull(write);
        EnsureBodyMutable();
        _buffer.Clear();
        _suppressedBodyLength = 0;
        SetFrameworkHeader("Content-Type", contentType);
        _responseState = ResponseState.Streaming;
        ApplyResponseHead(contentLength);

        try
        {
            await ResponseBodyFeature.StartAsync(Aborted).ConfigureAwait(false);
            if (!ShouldSuppressBody())
            {
                await write(ResponseBodyFeature.Writer, Aborted).ConfigureAwait(false);
                var flush = await ResponseBodyFeature.Writer.FlushAsync(Aborted).ConfigureAwait(false);
                if (flush.IsCanceled)
                {
                    throw new OperationCanceledException(Aborted);
                }
            }

            await ResponseBodyFeature.CompleteAsync().ConfigureAwait(false);
            _responseState = ResponseState.Sent;
        }
        catch
        {
            AbortResponse();
            throw;
        }
    }
}
