using System.Buffers;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Miya.Json;

namespace Miya;

public sealed class Request
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly Context _context;
    private Dictionary<string, string>? _query;
    private int _bodyClaimed;

    internal Request(Context context)
    {
        _context = context;
    }

    public string Method => Feature.Method;

    public string Path => Feature.Path;

    public PipeReader BodyReader
    {
        get
        {
            _context.EnsureActive();
            ClaimBody();
            var limit = _context.Options.MaxRequestBodyBytes;
            ValidateContentLength(limit);
            return new LimitedPipeReader(GetBodyReader(), limit);
        }
    }

    public string? Header(string name)
    {
        _context.EnsureActive();
        ArgumentException.ThrowIfNullOrEmpty(name);
        return Feature.Headers.TryGetValue(name, out var value) ? value.ToString() : null;
    }

    public string? Query(string name)
    {
        _context.EnsureActive();
        ArgumentNullException.ThrowIfNull(name);
        _query ??= ParseQuery(Feature.QueryString);
        return _query.TryGetValue(name, out var value) ? value : null;
    }

    public async ValueTask<string> Text()
    {
        _context.EnsureActive();
        using var body = await ReadBody(_context.Options.MaxRequestBodyBytes).ConfigureAwait(false);
        try
        {
            return StrictUtf8.GetString(body.WrittenMemory.Span);
        }
        catch (DecoderFallbackException exception)
        {
            throw new BadHttpRequestException("The request body is not valid UTF-8.", StatusCodes.Status400BadRequest, exception);
        }
    }

    public async ValueTask<T?> Json<T>()
    {
        _context.EnsureActive();
        var limit = Math.Min(_context.Options.MaxRequestBodyBytes, _context.Options.MaxJsonBodyBytes);
        using var body = await ReadBody(limit).ConfigureAwait(false);
        return MiyaJson.Deserialize<T>(body.WrittenMemory.Span, _context.Options.Json);
    }

    internal void Reset()
    {
        _query = null;
        Volatile.Write(ref _bodyClaimed, 0);
    }

    internal string GetRouteParameter(ParameterCapture capture)
    {
        _context.EnsureActive();
        var feature = Feature;
        var rawTarget = feature.RawTarget;
        if (!string.IsNullOrEmpty(rawTarget) && rawTarget[0] == '/')
        {
            var queryStart = rawTarget.IndexOf('?');
            var rawPath = queryStart < 0 ? rawTarget.AsSpan() : rawTarget.AsSpan(0, queryStart);
            var segmentStart = 1;
            for (var segmentIndex = 0; segmentIndex < capture.SegmentIndex; segmentIndex++)
            {
                var slash = rawPath[segmentStart..].IndexOf('/');
                if (slash < 0)
                {
                    return DecodeCapturedPath(feature.Path, capture);
                }

                segmentStart += slash + 1;
            }

            if (segmentStart > rawPath.Length)
            {
                return DecodeCapturedPath(feature.Path, capture);
            }

            var nextSlash = rawPath[segmentStart..].IndexOf('/');
            var segmentLength = capture.IsWildcard || nextSlash < 0
                ? rawPath.Length - segmentStart
                : nextSlash;
            return Context.DecodePercentEncoded(
                rawPath.Slice(segmentStart, segmentLength),
                plusAsSpace: false,
                "The route parameter is not valid UTF-8.");
        }

        return DecodeCapturedPath(feature.Path, capture);
    }

    private IHttpRequestFeature Feature =>
        _context.Features.Get<IHttpRequestFeature>()
        ?? throw new InvalidOperationException("The request feature is unavailable.");

    private void ClaimBody()
    {
        if (Interlocked.Exchange(ref _bodyClaimed, 1) != 0)
        {
            throw new InvalidOperationException("The request body can only be consumed once.");
        }
    }

    private PipeReader GetBodyReader()
    {
        return _context.Features.Get<IRequestBodyPipeFeature>()?.Reader
            ?? throw new InvalidOperationException("IRequestBodyPipeFeature is required.");
    }

    private async ValueTask<PooledByteBufferWriter> ReadBody(int limit)
    {
        ClaimBody();
        ValidateContentLength(limit);

        var destination = new PooledByteBufferWriter(_context.Options.Json.MaxPooledBufferByteLength);
        var reader = GetBodyReader();
        try
        {
            while (true)
            {
                var result = await reader.ReadAsync(_context.Aborted).ConfigureAwait(false);
                var sequence = result.Buffer;
                try
                {
                    foreach (var segment in sequence)
                    {
                        if (segment.Length > limit - destination.WrittenCount)
                        {
                            throw BodyTooLarge(limit);
                        }

                        destination.Write(segment.Span);
                    }
                }
                finally
                {
                    reader.AdvanceTo(sequence.End);
                }

                if (result.IsCompleted)
                {
                    return destination;
                }

                if (result.IsCanceled)
                {
                    throw new OperationCanceledException(_context.Aborted);
                }
            }
        }
        catch
        {
            destination.Dispose();
            throw;
        }
    }

    private void ValidateContentLength(int limit)
    {
        if (!Feature.Headers.TryGetValue("Content-Length", out var values))
        {
            return;
        }

        var raw = values.ToString();
        if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length < 0)
        {
            throw new BadHttpRequestException("The Content-Length header is invalid.", StatusCodes.Status400BadRequest);
        }

        if (length > limit)
        {
            throw BodyTooLarge(limit);
        }
    }

    internal static BadHttpRequestException BodyTooLarge(long limit) => new(
        $"The request body exceeds the configured limit of {limit} bytes.",
        StatusCodes.Status413PayloadTooLarge);

    private static Dictionary<string, string> ParseQuery(string queryString)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var start = queryString.Length > 0 && queryString[0] == '?' ? 1 : 0;

        while (start <= queryString.Length)
        {
            var ampersand = queryString.IndexOf('&', start);
            var end = ampersand < 0 ? queryString.Length : ampersand;
            if (end > start)
            {
                var equals = queryString.IndexOf('=', start, end - start);
                var nameEnd = equals < 0 ? end : equals;
                var valueStart = equals < 0 ? end : equals + 1;
                var name = DecodeQueryPart(queryString.AsSpan(start, nameEnd - start));
                if (!result.ContainsKey(name))
                {
                    result.Add(name, DecodeQueryPart(queryString.AsSpan(valueStart, end - valueStart)));
                }
            }

            if (ampersand < 0)
            {
                break;
            }

            start = ampersand + 1;
        }

        return result;
    }

    private static string DecodeQueryPart(ReadOnlySpan<char> value)
    {
        return Context.DecodePercentEncoded(
            value,
            plusAsSpace: true,
            "The query string is not valid UTF-8.");
    }

    private static string DecodeCapturedPath(string path, ParameterCapture capture) =>
        Context.DecodePercentEncoded(
            path.AsSpan(capture.Start, capture.Length),
            plusAsSpace: false,
            "The route parameter is not valid UTF-8.");
}

internal sealed class LimitedPipeReader : PipeReader
{
    private readonly PipeReader _inner;
    private readonly long _limit;
    private ReadOnlySequence<byte> _activeBuffer;
    private long _consumedBytes;
    private bool _hasActiveRead;

    public LimitedPipeReader(PipeReader inner, long limit)
    {
        _inner = inner;
        _limit = limit;
    }

    public override void AdvanceTo(SequencePosition consumed)
    {
        TrackConsumed(consumed);
        _inner.AdvanceTo(consumed);
    }

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        TrackConsumed(consumed);
        _inner.AdvanceTo(consumed, examined);
    }

    public override void CancelPendingRead() => _inner.CancelPendingRead();

    public override void Complete(Exception? exception = null) => _inner.Complete(exception);

    public override ValueTask CompleteAsync(Exception? exception = null) => _inner.CompleteAsync(exception);

    public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        EnsureNoActiveRead();
        var operation = _inner.ReadAsync(cancellationToken);
        if (operation.IsCompletedSuccessfully)
        {
            return new ValueTask<ReadResult>(Validate(operation.Result));
        }

        return AwaitRead(operation);
    }

    public override bool TryRead(out ReadResult result)
    {
        EnsureNoActiveRead();
        if (!_inner.TryRead(out var innerResult))
        {
            result = default;
            return false;
        }

        result = Validate(innerResult);
        return true;
    }

    private async ValueTask<ReadResult> AwaitRead(ValueTask<ReadResult> operation) =>
        Validate(await operation.ConfigureAwait(false));

    private ReadResult Validate(ReadResult result)
    {
        var buffer = result.Buffer;
        if (buffer.Length > _limit - _consumedBytes)
        {
            _inner.AdvanceTo(buffer.End);
            throw Request.BodyTooLarge(_limit);
        }

        _activeBuffer = buffer;
        _hasActiveRead = true;
        return result;
    }

    private void EnsureNoActiveRead()
    {
        if (_hasActiveRead)
        {
            throw new InvalidOperationException("AdvanceTo must be called before reading again.");
        }
    }

    private void TrackConsumed(SequencePosition consumed)
    {
        if (!_hasActiveRead)
        {
            throw new InvalidOperationException("No read operation is active.");
        }

        _consumedBytes = checked(_consumedBytes + _activeBuffer.Slice(0, consumed).Length);
        _activeBuffer = default;
        _hasActiveRead = false;
    }
}
