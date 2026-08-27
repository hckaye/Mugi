using System.Buffers;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;
using Miya.Json;

namespace Miya;

public class Context
{
    private static readonly AppOptions DefaultOptions = new();
    private static readonly AsyncLocal<RequestLease?> CurrentLease = new();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly HeaderDictionary _headers = new();
    private readonly PooledByteBufferWriter _buffer = new();
    private readonly CountingBufferWriter _measurementWriter;
    private readonly Request _request;
    private readonly ResponseBufferWriter _responseWriter;
    private IFeatureCollection? _features;
    private IHttpResponseFeature? _responseFeature;
    private IHttpResponseBodyFeature? _responseBodyFeature;
    private IHttpRequestLifetimeFeature? _lifetimeFeature;
    private AppOptions _options = DefaultOptions;
    private ResponseState _responseState;
    private int _statusCode = StatusCodes.Status200OK;
    private string[]? _parameterNames;
    private ParameterCapture[] _parameterCaptures = [];
    private int _parameterCount;
    private int[] _middlewareCalls = [];
    private long _generationCounter;
    private long _activeGeneration;
    private long _suppressedBodyLength;

    public Context()
    {
        _request = new Request(this);
        _responseWriter = new ResponseBufferWriter(this);
        _measurementWriter = new CountingBufferWriter();
    }

    public Request Req
    {
        get
        {
            EnsureActive();
            return _request;
        }
    }

    public bool ResponseStarted
    {
        get
        {
            EnsureActive();
            return _responseState is ResponseState.Streaming or ResponseState.Sent or ResponseState.Aborted
                || (_responseFeature?.HasStarted ?? false);
        }
    }

    public CancellationToken Aborted
    {
        get
        {
            EnsureActive();
            return _lifetimeFeature?.RequestAborted ?? CancellationToken.None;
        }
    }

    internal IFeatureCollection Features
    {
        get
        {
            EnsureActive();
            return _features!;
        }
    }

    internal AppOptions Options
    {
        get
        {
            EnsureActive();
            return _options;
        }
    }

    internal bool IsAborted => _responseState == ResponseState.Aborted;

    public string Param(string name)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(name);
        for (var i = 0; i < _parameterCount; i++)
        {
            if (string.Equals(_parameterNames![i], name, StringComparison.Ordinal))
            {
                return _request.GetRouteParameter(_parameterCaptures[i]);
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
        if (IsContentLengthForbidden())
        {
            _buffer.Clear();
            _suppressedBodyLength = 0;
        }
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
        EnsureActive();
        ArgumentNullException.ThrowIfNull(value);
        var byteCount = Encoding.UTF8.GetByteCount(value);
        BeginBufferedBody("text/plain; charset=utf-8");
        if (SuppressMeasuredBody(byteCount))
        {
            return ValueTask.CompletedTask;
        }

        var destination = _responseWriter.GetSpan(byteCount);
        var written = Encoding.UTF8.GetBytes(value, destination);
        _responseWriter.Advance(written);
        return FinishBodyWrite();
    }

    public ValueTask Html(string value)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(value);
        var byteCount = Encoding.UTF8.GetByteCount(value);
        BeginBufferedBody("text/html; charset=utf-8");
        if (SuppressMeasuredBody(byteCount))
        {
            return ValueTask.CompletedTask;
        }

        var destination = _responseWriter.GetSpan(byteCount);
        var written = Encoding.UTF8.GetBytes(value, destination);
        _responseWriter.Advance(written);
        return FinishBodyWrite();
    }

    public ValueTask Bytes(ReadOnlyMemory<byte> data, string contentType)
    {
        EnsureActive();
        ArgumentException.ThrowIfNullOrEmpty(contentType);
        BeginBufferedBody(contentType);
        if (SuppressMeasuredBody(data.Length))
        {
            return ValueTask.CompletedTask;
        }

        _responseWriter.Write(data.Span);
        return FinishBodyWrite();
    }

    public ValueTask Json<T>(T value)
    {
        EnsureBodyMutable();
        var codec = global::Miya.Json.Json.GetCodec<T>();
        _measurementWriter.Reset(_options.Json.MaxPooledBufferByteLength);
        long measuredLength;
        try
        {
            global::Miya.Json.Json.Serialize(_measurementWriter, value, codec, _options.Json);
            measuredLength = _measurementWriter.WrittenCount;
        }
        finally
        {
            _measurementWriter.Release();
        }

        BeginBufferedBody("application/json; charset=utf-8");
        if (SuppressMeasuredBody(measuredLength))
        {
            return ValueTask.CompletedTask;
        }

        global::Miya.Json.Json.Serialize(_responseWriter, value, codec, _options.Json);
        return FinishBodyWrite();
    }

    public ValueTask Json<T>(T value, IJsonCodec<T> codec)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(codec);
        EnsureBodyMutable();
        _measurementWriter.Reset(_options.Json.MaxPooledBufferByteLength);
        long measuredLength;
        try
        {
            global::Miya.Json.Json.Serialize(_measurementWriter, value, codec, _options.Json);
            measuredLength = _measurementWriter.WrittenCount;
        }
        finally
        {
            _measurementWriter.Release();
        }

        BeginBufferedBody("application/json; charset=utf-8");
        if (SuppressMeasuredBody(measuredLength))
        {
            return ValueTask.CompletedTask;
        }

        var writer = new JsonWriter(_responseWriter, _options.Json);
        codec.Write(ref writer, value);
        writer.Flush();
        return FinishBodyWrite();
    }

    public async ValueTask Stream(
        string contentType,
        Func<PipeWriter, CancellationToken, ValueTask> write)
    {
        EnsureActive();
        ArgumentException.ThrowIfNullOrEmpty(contentType);
        ArgumentNullException.ThrowIfNull(write);
        EnsureBodyMutable();
        _buffer.Clear();
        _suppressedBodyLength = 0;
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
        EnsureActive();
        ArgumentNullException.ThrowIfNull(location);
        if (status is < 300 or > 399)
        {
            throw new ArgumentOutOfRangeException(nameof(status), "Redirect status codes must be between 300 and 399.");
        }

        ValidateLocation(location);
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

    internal void Initialize(IFeatureCollection features, AppOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(features);
        if (_features is not null)
        {
            throw new InvalidOperationException("The context is already attached to a request.");
        }

        _options = options ?? DefaultOptions;
        _options.Validate();
        _generationCounter = unchecked(_generationCounter + 1);
        if (_generationCounter == 0)
        {
            _generationCounter = 1;
        }

        _activeGeneration = _generationCounter;
        _features = features;
        _responseFeature = features.Get<IHttpResponseFeature>()
            ?? throw new InvalidOperationException("IHttpResponseFeature is required.");
        _responseBodyFeature = features.Get<IHttpResponseBodyFeature>();
        _lifetimeFeature = features.Get<IHttpRequestLifetimeFeature>();
        _statusCode = _responseFeature.StatusCode;
        _headers.Clear();
        _buffer.Clear();
        _suppressedBodyLength = 0;
        _responseState = ResponseState.Empty;
        _parameterNames = null;
        _parameterCount = 0;
        Array.Clear(_middlewareCalls);
        _request.Reset();
        ApplyRequestBodyLimit(features, _options.MaxRequestBodyBytes);
    }

    internal void ResetFrameworkState(bool retainBuffers)
    {
        var maxRetainedBufferBytes = _options.MaxRetainedBufferBytes;
        _request.Reset();
        _activeGeneration = 0;
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
        _suppressedBodyLength = 0;

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
        _suppressedBodyLength = 0;
        _responseState = ResponseState.Empty;
    }

    internal void ResetResponseForError()
    {
        if (ResponseStarted)
        {
            throw new InvalidOperationException("A response that has started cannot be replaced.");
        }

        _buffer.Clear();
        _suppressedBodyLength = 0;
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

        var bufferedBodyLength = _responseState == ResponseState.Buffered ? _buffer.WrittenCount : 0;
        var suppress = ShouldSuppressBody();
        long? contentLength = IsContentLengthForbidden()
            ? null
            : string.Equals(Req.Method, "HEAD", StringComparison.Ordinal)
                ? _suppressedBodyLength
                : suppress
                    ? null
                    : bufferedBodyLength;

        ApplyResponseHead(contentLength);
        try
        {
            if (!suppress && bufferedBodyLength > 0)
            {
                await ResponseBodyFeature.StartAsync(Aborted).ConfigureAwait(false);
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

    internal static string DecodePercentEncoded(
        ReadOnlySpan<char> value,
        bool plusAsSpace,
        string errorMessage)
    {
        ValidatePercentEscapes(value);
        ValidateUnicode(value, errorMessage);
        if (value.IndexOf('%') < 0 && (!plusAsSpace || value.IndexOf('+') < 0))
        {
            return value.ToString();
        }

        var result = new StringBuilder(value.Length);
        Span<byte> stackBytes = stackalloc byte[256];
        var position = 0;
        while (position < value.Length)
        {
            if (plusAsSpace && value[position] == '+')
            {
                result.Append(' ');
                position++;
                continue;
            }

            if (value[position] != '%')
            {
                result.Append(value[position]);
                position++;
                continue;
            }

            var end = position;
            var byteCount = 0;
            while (end < value.Length && value[end] == '%')
            {
                byteCount++;
                end += 3;
            }

            Span<byte> bytes = byteCount <= stackBytes.Length
                ? stackBytes[..byteCount]
                : new byte[byteCount];
            for (var index = 0; index < byteCount; index++)
            {
                var escape = position + (index * 3);
                bytes[index] = (byte)((HexValue(value[escape + 1]) << 4) | HexValue(value[escape + 2]));
            }

            try
            {
                result.Append(StrictUtf8.GetString(bytes));
            }
            catch (DecoderFallbackException exception)
            {
                throw new BadHttpRequestException(
                    errorMessage,
                    StatusCodes.Status400BadRequest,
                    exception);
            }

            position = end;
        }

        return result.ToString();
    }

    internal ExecutionScope EnterExecutionScope()
    {
        if (_features is null || _activeGeneration == 0)
        {
            throw new ObjectDisposedException(nameof(Context), "The context is not attached to an active request.");
        }

        var previous = CurrentLease.Value;
        CurrentLease.Value = new RequestLease(this, _activeGeneration);
        return new ExecutionScope(previous);
    }

    private void BeginBufferedBody(string contentType)
    {
        EnsureBodyMutable();
        _buffer.Clear();
        _suppressedBodyLength = 0;
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
        var writtenCount = _buffer.WrittenCount;
        if (writtenCount > 0)
        {
            var writer = ResponseBodyFeature.Writer;
            var destination = writer.GetSpan(writtenCount);
            _buffer.WrittenMemory.Span.CopyTo(destination);
            writer.Advance(writtenCount);
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

    private bool SuppressMeasuredBody(long bodyLength)
    {
        if (!ShouldSuppressBody())
        {
            return false;
        }

        if (!IsContentLengthForbidden()
            && string.Equals(Req.Method, "HEAD", StringComparison.Ordinal))
        {
            _suppressedBodyLength = bodyLength;
        }

        return true;
    }

    private bool ShouldSuppressBody() =>
        string.Equals(Req.Method, "HEAD", StringComparison.Ordinal)
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

    internal void EnsureActive()
    {
        var lease = CurrentLease.Value;
        if (_features is null
            || _activeGeneration == 0
            || lease is null
            || !ReferenceEquals(lease.Context, this)
            || lease.Generation != _activeGeneration)
        {
            throw new ObjectDisposedException(nameof(Context), "The context is not attached to an active request.");
        }
    }

    private static void ApplyRequestBodyLimit(IFeatureCollection features, int limit)
    {
        var feature = features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is null || feature.IsReadOnly)
        {
            return;
        }

        if (!feature.MaxRequestBodySize.HasValue || feature.MaxRequestBodySize.Value > limit)
        {
            feature.MaxRequestBodySize = limit;
        }
    }

    private static int HexValue(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'A' and <= 'F' => value - 'A' + 10,
        _ => value - 'a' + 10,
    };

    private static void ValidateUnicode(ReadOnlySpan<char> value, string errorMessage)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (!char.IsSurrogate(character))
            {
                continue;
            }

            if (!char.IsHighSurrogate(character)
                || index + 1 >= value.Length
                || !char.IsLowSurrogate(value[index + 1]))
            {
                throw new BadHttpRequestException(errorMessage, StatusCodes.Status400BadRequest);
            }

            index++;
        }
    }

    private static void ValidateLocation(string location)
    {
        if (location.Length == 0)
        {
            throw new ArgumentException("The redirect location must not be empty.", nameof(location));
        }

        for (var index = 0; index < location.Length; index++)
        {
            var character = location[index];
            if (char.IsWhiteSpace(character)
                || char.IsControl(character)
                || character is '\\' or '<' or '>' or '"' or '{' or '}' or '|' or '^' or '`')
            {
                throw new ArgumentException("The redirect location is not a valid URI reference.", nameof(location));
            }

            if (character == '%'
                && (index + 2 >= location.Length
                    || !IsHex(location[index + 1])
                    || !IsHex(location[index + 2])))
            {
                throw new ArgumentException("The redirect location contains an invalid percent escape.", nameof(location));
            }

            if (character == '%')
            {
                index += 2;
                continue;
            }

            if (!char.IsSurrogate(character))
            {
                continue;
            }

            if (!char.IsHighSurrogate(character)
                || index + 1 >= location.Length
                || !char.IsLowSurrogate(location[index + 1]))
            {
                throw new ArgumentException("The redirect location contains invalid Unicode.", nameof(location));
            }

            index++;
        }

        if (Uri.TryCreate(location, UriKind.Absolute, out var absoluteUri))
        {
            if (!absoluteUri.IsWellFormedOriginalString())
            {
                throw new ArgumentException("The redirect location is not a valid URI reference.", nameof(location));
            }

            return;
        }

        var firstDelimiter = location.IndexOfAny('/', '?', '#');
        var colon = location.IndexOf(':');
        if ((colon >= 0 && (firstDelimiter < 0 || colon < firstDelimiter))
            || !Uri.TryCreate(location, UriKind.Relative, out _))
        {
            throw new ArgumentException("The redirect location is not a valid URI reference.", nameof(location));
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

    internal readonly struct ExecutionScope : IDisposable
    {
        private readonly RequestLease? _previous;

        internal ExecutionScope(RequestLease? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            CurrentLease.Value = _previous;
        }
    }

    internal sealed record RequestLease(Context Context, long Generation);

    private sealed class CountingBufferWriter : IBufferWriter<byte>
    {
        private int _maxPooledBufferByteLength;
        private byte[]? _buffer;
        private int _available;

        public long WrittenCount { get; private set; }

        public void Reset(int maxPooledBufferByteLength)
        {
            _maxPooledBufferByteLength = maxPooledBufferByteLength;
            WrittenCount = 0;
            _available = 0;
        }

        public void Advance(int count)
        {
            if (count < 0 || count > _available)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            WrittenCount = checked(WrittenCount + count);
            _available = 0;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            _available = _buffer!.Length;
            return _buffer;
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            _available = _buffer!.Length;
            return _buffer;
        }

        public void Release()
        {
            if (_buffer is not null && _buffer.Length <= _maxPooledBufferByteLength)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
            }

            _buffer = null;
            _available = 0;
        }

        private void EnsureCapacity(int sizeHint)
        {
            if (sizeHint < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sizeHint));
            }

            var required = Math.Max(sizeHint, 256);
            if (_buffer is not null && _buffer.Length >= required)
            {
                return;
            }

            var replacement = ArrayPool<byte>.Shared.Rent(required);
            if (_buffer is not null && _buffer.Length <= _maxPooledBufferByteLength)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
            }

            _buffer = replacement;
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

internal readonly record struct ParameterCapture(
    int Start,
    int Length,
    int SegmentIndex,
    bool IsWildcard);

internal enum ResponseState
{
    Empty,
    Buffered,
    Streaming,
    Sent,
    Aborted,
}
