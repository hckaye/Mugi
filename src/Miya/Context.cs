using System.Buffers;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;
using Miya.Json;

namespace Miya;

public class Context
{
    private static readonly MiyaOptions DefaultOptions = new();

    private readonly HeaderDictionary _headers = new();
    private readonly PooledByteBufferWriter _buffer = new();
    private readonly ResponseBufferWriter _responseWriter;
    private IFeatureCollection? _features;
    private IHttpResponseFeature? _responseFeature;
    private IHttpResponseBodyFeature? _responseBodyFeature;
    private IHttpRequestLifetimeFeature? _lifetimeFeature;
    private MiyaOptions _options = DefaultOptions;
    private ResponseState _responseState;
    private int _statusCode = StatusCodes.Status200OK;
    private string[]? _parameterNames;
    private ParameterCapture[] _parameterCaptures = [];
    private int _parameterCount;
    private int[] _middlewareCalls = [];

    public Context()
    {
        Req = new Request(this);
        _responseWriter = new ResponseBufferWriter(this);
    }

    public Request Req { get; }

    public bool ResponseStarted =>
        _responseState is ResponseState.Streaming or ResponseState.Sent or ResponseState.Aborted
        || (_responseFeature?.HasStarted ?? false);

    public CancellationToken Aborted => _lifetimeFeature?.RequestAborted ?? CancellationToken.None;

    internal IFeatureCollection Features =>
        _features ?? throw new InvalidOperationException("The context is not attached to a request.");

    internal MiyaOptions Options => _options;

    internal bool IsAborted => _responseState == ResponseState.Aborted;

    public string Param(string name)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(name);
        for (var i = 0; i < _parameterCount; i++)
        {
            if (string.Equals(_parameterNames![i], name, StringComparison.Ordinal))
            {
                var capture = _parameterCaptures[i];
                var value = Req.Path.AsSpan(capture.Start, capture.Length);
                ValidatePercentEscapes(value);
                try
                {
                    return Uri.UnescapeDataString(value.ToString());
                }
                catch (UriFormatException exception)
                {
                    throw new BadHttpRequestException(
                        "The route parameter contains an invalid escape sequence.",
                        StatusCodes.Status400BadRequest,
                        exception);
                }
            }
        }

        throw new KeyNotFoundException($"The route parameter '{name}' does not exist.");
    }

    public string? Query(string name) => Req.Query(name);

    public void Status(int code)
    {
        EnsureHeadersMutable();
        if (code is < 100 or > 999)
        {
            throw new ArgumentOutOfRangeException(nameof(code), "HTTP status codes must be between 100 and 999.");
        }

        _statusCode = code;
    }

    public void Header(string name, string value)
    {
        EnsureHeadersMutable();
        ValidateUserHeader(name, value);
        _headers[name] = value;
    }

    public void AppendHeader(string name, string value)
    {
        EnsureHeadersMutable();
        ValidateUserHeader(name, value);
        _headers[name] = _headers.TryGetValue(name, out var existing)
            ? StringValues.Concat(existing, value)
            : new StringValues(value);
    }

    public ValueTask Text(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        BeginBufferedBody("text/plain; charset=utf-8");
        var byteCount = Encoding.UTF8.GetByteCount(value);
        var destination = _responseWriter.GetSpan(byteCount);
        var written = Encoding.UTF8.GetBytes(value, destination);
        _responseWriter.Advance(written);
        return FinishBodyWrite();
    }

    public ValueTask Html(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        BeginBufferedBody("text/html; charset=utf-8");
        var byteCount = Encoding.UTF8.GetByteCount(value);
        var destination = _responseWriter.GetSpan(byteCount);
        var written = Encoding.UTF8.GetBytes(value, destination);
        _responseWriter.Advance(written);
        return FinishBodyWrite();
    }

    public ValueTask Bytes(ReadOnlyMemory<byte> data, string contentType)
    {
        ArgumentException.ThrowIfNullOrEmpty(contentType);
        BeginBufferedBody(contentType);
        _responseWriter.Write(data.Span);
        return FinishBodyWrite();
    }

    public ValueTask Json<T>(T value)
    {
        BeginBufferedBody("application/json; charset=utf-8");
        MiyaJson.Serialize(_responseWriter, value, _options.Json);
        return FinishBodyWrite();
    }

    public ValueTask Json<T>(T value, IMiyaJsonCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        BeginBufferedBody("application/json; charset=utf-8");
        var writer = new MiyaJsonWriter(_responseWriter, _options.Json);
        codec.Write(ref writer, value);
        writer.Flush();
        return FinishBodyWrite();
    }

    public ValueTask Json<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        BeginBufferedBody("application/json; charset=utf-8");
        using var writer = new Utf8JsonWriter(_responseWriter);
        JsonSerializer.Serialize(writer, value, typeInfo);
        writer.Flush();
        return FinishBodyWrite();
    }

    public async ValueTask Stream(
        string contentType,
        Func<PipeWriter, CancellationToken, ValueTask> write)
    {
        ArgumentException.ThrowIfNullOrEmpty(contentType);
        ArgumentNullException.ThrowIfNull(write);
        EnsureBodyMutable();
        _buffer.Clear();
        SetFrameworkHeader("Content-Type", contentType);
        _responseState = ResponseState.Streaming;
        ApplyResponseHead(contentLength: null);

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

    public ValueTask Redirect(string location, int status = StatusCodes.Status302Found)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (status is < 300 or > 399)
        {
            throw new ArgumentOutOfRangeException(nameof(status), "Redirect status codes must be between 300 and 399.");
        }

        Status(status);
        Header("Location", location);
        SetEmptyBody();
        return ValueTask.CompletedTask;
    }

    public ValueTask NotFound()
    {
        Status(StatusCodes.Status404NotFound);
        return Text("Not Found");
    }

    internal void Initialize(IFeatureCollection features, MiyaOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(features);
        if (_features is not null)
        {
            throw new InvalidOperationException("The context is already attached to a request.");
        }

        _options = options ?? DefaultOptions;
        _options.Validate();
        _features = features;
        _responseFeature = features.Get<IHttpResponseFeature>()
            ?? throw new InvalidOperationException("IHttpResponseFeature is required.");
        _responseBodyFeature = features.Get<IHttpResponseBodyFeature>();
        _lifetimeFeature = features.Get<IHttpRequestLifetimeFeature>();
        _statusCode = _responseFeature.StatusCode;
        _headers.Clear();
        _buffer.Clear();
        _responseState = ResponseState.Empty;
        _parameterNames = null;
        _parameterCount = 0;
        Array.Clear(_middlewareCalls);
        Req.Reset();
    }

    internal void ResetFrameworkState(bool retainBuffers)
    {
        var maxRetainedBufferBytes = _options.MaxRetainedBufferBytes;
        Req.Reset();
        _features = null;
        _responseFeature = null;
        _responseBodyFeature = null;
        _lifetimeFeature = null;
        _options = DefaultOptions;
        _headers.Clear();
        _parameterNames = null;
        _parameterCount = 0;
        Array.Clear(_middlewareCalls);
        _responseState = ResponseState.Empty;
        _statusCode = StatusCodes.Status200OK;

        if (retainBuffers)
        {
            _buffer.Reset(maxRetainedBufferBytes);
        }
        else
        {
            _buffer.Dispose();
        }
    }

    internal void PrepareMiddlewareSlots(int count)
    {
        if (_middlewareCalls.Length < count)
        {
            _middlewareCalls = new int[count];
        }
        else
        {
            Array.Clear(_middlewareCalls, 0, count);
        }
    }

    internal void ClaimNext(int slot)
    {
        if (Interlocked.Exchange(ref _middlewareCalls[slot], 1) != 0)
        {
            throw new InvalidOperationException("Middleware may call next only once per request.");
        }
    }

    internal Span<ParameterCapture> GetParameterCaptureBuffer(int count)
    {
        if (_parameterCaptures.Length < count)
        {
            _parameterCaptures = new ParameterCapture[count];
        }

        return _parameterCaptures.AsSpan(0, count);
    }

    internal void SetRouteParameters(string[] names, int count)
    {
        _parameterNames = names;
        _parameterCount = count;
    }

    internal void ClearRouteParameters()
    {
        _parameterNames = null;
        _parameterCount = 0;
    }

    internal void SetEmptyBody()
    {
        EnsureBodyMutable();
        _buffer.Clear();
        _responseState = ResponseState.Empty;
    }

    internal void ResetResponseForError()
    {
        if (ResponseStarted)
        {
            throw new InvalidOperationException("A response that has started cannot be replaced.");
        }

        _buffer.Clear();
        _headers.Clear();
        _statusCode = StatusCodes.Status200OK;
        _responseState = ResponseState.Empty;
    }

    internal async ValueTask CompleteResponse()
    {
        if (_responseState is ResponseState.Sent or ResponseState.Aborted)
        {
            return;
        }

        if (_responseState == ResponseState.Streaming)
        {
            await CompleteStreamingResponse().ConfigureAwait(false);
            return;
        }

        var bodyLength = _responseState == ResponseState.Buffered ? _buffer.WrittenCount : 0;
        var suppress = ShouldSuppressBody();
        long? contentLength = IsContentLengthForbidden()
            ? null
            : string.Equals(Req.Method, "HEAD", StringComparison.OrdinalIgnoreCase)
                ? bodyLength
                : suppress
                    ? null
                    : bodyLength;

        ApplyResponseHead(contentLength);
        try
        {
            if (!suppress && bodyLength > 0)
            {
                ResponseBodyFeature.Writer.Write(_buffer.WrittenMemory.Span);
                var flush = await ResponseBodyFeature.Writer.FlushAsync(Aborted).ConfigureAwait(false);
                if (flush.IsCanceled)
                {
                    throw new OperationCanceledException(Aborted);
                }
            }
            else
            {
                await ResponseBodyFeature.StartAsync(Aborted).ConfigureAwait(false);
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

    internal void AbortResponse()
    {
        _responseState = ResponseState.Aborted;
        _lifetimeFeature?.Abort();
    }

    internal static void ValidatePercentEscapes(ReadOnlySpan<char> value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '%')
            {
                continue;
            }

            if (i + 2 >= value.Length || !IsHex(value[i + 1]) || !IsHex(value[i + 2]))
            {
                throw new BadHttpRequestException(
                    "The request target contains an invalid percent escape.",
                    StatusCodes.Status400BadRequest);
            }

            i += 2;
        }
    }

    private static bool IsHex(char value) =>
        value is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f';

    private void BeginBufferedBody(string contentType)
    {
        EnsureBodyMutable();
        _buffer.Clear();
        SetFrameworkHeader("Content-Type", contentType);
        _responseState = ResponseState.Buffered;
    }

    private ValueTask FinishBodyWrite()
    {
        return _responseState == ResponseState.Streaming
            ? CompleteStreamingResponse()
            : ValueTask.CompletedTask;
    }

    private async ValueTask CompleteStreamingResponse()
    {
        try
        {
            var flush = await ResponseBodyFeature.Writer.FlushAsync(Aborted).ConfigureAwait(false);
            if (flush.IsCanceled)
            {
                throw new OperationCanceledException(Aborted);
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

    private void PromoteToStreaming()
    {
        if (_responseState != ResponseState.Buffered)
        {
            return;
        }

        _responseState = ResponseState.Streaming;
        ApplyResponseHead(contentLength: null);
        if (_buffer.WrittenCount > 0)
        {
            ResponseBodyFeature.Writer.Write(_buffer.WrittenMemory.Span);
            _buffer.Clear();
        }
    }

    private bool ShouldPromote(int sizeHint)
    {
        if (_responseState != ResponseState.Buffered || ShouldSuppressBody())
        {
            return false;
        }

        return sizeHint > _options.MaxBufferedResponseBytes - _buffer.WrittenCount;
    }

    private bool ShouldSuppressBody() =>
        string.Equals(Req.Method, "HEAD", StringComparison.OrdinalIgnoreCase)
        || IsContentLengthForbidden();

    private bool IsContentLengthForbidden() =>
        _statusCode is >= 100 and < 200 or StatusCodes.Status204NoContent or StatusCodes.Status304NotModified;

    private void ApplyResponseHead(long? contentLength)
    {
        var response = _responseFeature
            ?? throw new InvalidOperationException("The response feature is unavailable.");
        response.StatusCode = _statusCode;

        foreach (var header in _headers)
        {
            response.Headers[header.Key] = header.Value;
        }

        response.Headers.Remove("Transfer-Encoding");
        response.Headers.Remove("Connection");
        if (contentLength.HasValue)
        {
            response.Headers["Content-Length"] = contentLength.Value.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            response.Headers.Remove("Content-Length");
        }
    }

    private IHttpResponseBodyFeature ResponseBodyFeature
    {
        get
        {
            return _responseBodyFeature
                ?? throw new InvalidOperationException("IHttpResponseBodyFeature is required.");
        }
    }

    private void EnsureHeadersMutable()
    {
        EnsureActive();
        if (ResponseStarted)
        {
            throw new InvalidOperationException("Status and headers cannot be changed after the response has started.");
        }
    }

    private void EnsureBodyMutable()
    {
        EnsureActive();
        if (_responseState is ResponseState.Streaming or ResponseState.Sent or ResponseState.Aborted
            || (_responseFeature?.HasStarted ?? false))
        {
            throw new InvalidOperationException("The response body cannot be changed after the response has started.");
        }
    }

    private void SetFrameworkHeader(string name, string value)
    {
        ValidateHeaderName(name);
        ValidateHeaderValue(value);
        _headers[name] = value;
    }

    private void EnsureActive()
    {
        if (_features is null)
        {
            throw new ObjectDisposedException(nameof(Context), "The context is not attached to an active request.");
        }
    }

    private static void ValidateUserHeader(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateHeaderName(name);
        ValidateHeaderValue(value);

        if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The '{name}' header is managed by Miya.");
        }
    }

    private static void ValidateHeaderName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        foreach (var character in name)
        {
            if (character is < '!' or > '~'
                || character is '(' or ')' or '<' or '>' or '@' or ',' or ';' or ':' or '\\'
                    or '"' or '/' or '[' or ']' or '?' or '=' or '{' or '}')
            {
                throw new ArgumentException("The header name contains an invalid character.", nameof(name));
            }
        }
    }

    private static void ValidateHeaderValue(string value)
    {
        foreach (var character in value)
        {
            if (character is '\r' or '\n' or '\0' or '\u007f'
                || (character < ' ' && character != '\t'))
            {
                throw new ArgumentException("The header value contains an invalid character.", nameof(value));
            }
        }
    }

    private sealed class ResponseBufferWriter : IBufferWriter<byte>
    {
        private readonly Context _context;
        private bool _lastWriteWasStreaming;

        public ResponseBufferWriter(Context context)
        {
            _context = context;
        }

        public void Advance(int count)
        {
            if (_lastWriteWasStreaming)
            {
                _context.ResponseBodyFeature.Writer.Advance(count);
            }
            else
            {
                _context._buffer.Advance(count);
            }
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Prepare(sizeHint);
            if (_lastWriteWasStreaming)
            {
                return _context.ResponseBodyFeature.Writer.GetMemory(sizeHint);
            }

            var memory = _context._buffer.GetMemory(sizeHint);
            if (_context.ShouldSuppressBody())
            {
                return memory;
            }

            var remaining = _context._options.MaxBufferedResponseBytes - _context._buffer.WrittenCount;
            return memory[..Math.Min(memory.Length, remaining)];
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Prepare(sizeHint);
            if (_lastWriteWasStreaming)
            {
                return _context.ResponseBodyFeature.Writer.GetSpan(sizeHint);
            }

            var span = _context._buffer.GetSpan(sizeHint);
            if (_context.ShouldSuppressBody())
            {
                return span;
            }

            var remaining = _context._options.MaxBufferedResponseBytes - _context._buffer.WrittenCount;
            return span[..Math.Min(span.Length, remaining)];
        }

        private void Prepare(int sizeHint)
        {
            if (sizeHint < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sizeHint));
            }

            if (_context.ShouldPromote(sizeHint == 0 ? 1 : sizeHint))
            {
                _context.PromoteToStreaming();
            }

            _lastWriteWasStreaming = _context._responseState == ResponseState.Streaming;
        }
    }
}

internal readonly record struct ParameterCapture(int Start, int Length);

internal enum ResponseState
{
    Empty,
    Buffered,
    Streaming,
    Sent,
    Aborted,
}
