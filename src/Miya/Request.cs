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
            ClaimBody();
            ValidateContentLength(_context.Options.MaxRequestBodyBytes);
            return GetBodyReader();
        }
    }

    public string? Header(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return Feature.Headers.TryGetValue(name, out var value) ? value.ToString() : null;
    }

    public string? Query(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        _query ??= ParseQuery(Feature.QueryString);
        return _query.TryGetValue(name, out var value) ? value : null;
    }

    public async ValueTask<string> Text()
    {
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
        var limit = Math.Min(_context.Options.MaxRequestBodyBytes, _context.Options.MaxJsonBodyBytes);
        using var body = await ReadBody(limit).ConfigureAwait(false);
        return MiyaJson.Deserialize<T>(body.WrittenMemory.Span, _context.Options.Json);
    }

    internal void Reset()
    {
        _query = null;
        Volatile.Write(ref _bodyClaimed, 0);
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

        var destination = new PooledByteBufferWriter();
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

    private static BadHttpRequestException BodyTooLarge(int limit) => new(
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
        if (value.IndexOfAny('%', '+') < 0)
        {
            return value.ToString();
        }

        Context.ValidatePercentEscapes(value);
        var encoded = value.ToString().Replace('+', ' ');
        try
        {
            return Uri.UnescapeDataString(encoded);
        }
        catch (UriFormatException exception)
        {
            throw new BadHttpRequestException("The query string contains an invalid escape sequence.", StatusCodes.Status400BadRequest, exception);
        }
    }
}
