using System.Buffers;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Http;

namespace Miya;

public partial class Context
{
    private const int ResponseControlDisabled = 0;
    private const int ResponseControlAvailable = 1;
    private const int ResponseControlStarting = 2;
    private const int ResponseControlStreaming = 3;
    private const int ResponseControlSurrendered = 4;
    private static ReadOnlySpan<byte> GatewayTimeoutBody => "Gateway Timeout"u8;

    private int _responseControl;
    private int _preventPooling;
    private int _timeoutControlUsers;
    private CancellationTokenSource? _timeoutCancellation;

    internal bool TimeoutResponseControlEnabled =>
        Volatile.Read(ref _responseControl) != ResponseControlDisabled;

    internal bool CanReturnToPool => Volatile.Read(ref _preventPooling) == 0;

    /// <summary>
    /// Gets the response body while it remains buffered.
    /// </summary>
    /// <remarks>
    /// This method returns <see langword="false"/> after the response starts streaming, including automatic
    /// promotion caused by the configured response buffer limit. It also returns <see langword="false"/> when
    /// no body bytes were written. The returned memory is valid only until the response is replaced or the
    /// request finishes.
    /// </remarks>
    /// <param name="body">The buffered response body when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a non-empty response body is still buffered.</returns>
    public bool TryGetBufferedResponse(out ReadOnlyMemory<byte> body)
    {
        EnsureActive();
        if (_responseState == ResponseState.Buffered && _buffer.WrittenCount > 0)
        {
            body = _buffer.WrittenMemory;
            return true;
        }

        body = ReadOnlyMemory<byte>.Empty;
        return false;
    }

    /// <summary>
    /// Replaces the buffered response body and optionally changes its content type.
    /// </summary>
    /// <remarks>
    /// The response must not have started streaming or been sent. Status codes that forbid a response body
    /// discard the supplied bytes. For a HEAD request, the replacement is retained for middleware inspection
    /// but only its length is sent.
    /// </remarks>
    /// <param name="body">The replacement response body.</param>
    /// <param name="contentType">The replacement content type, or <see langword="null"/> to keep the current value.</param>
    public void ReplaceBufferedResponse(ReadOnlyMemory<byte> body, string? contentType = null)
    {
        EnsureBodyMutable();
        if (contentType is not null)
        {
            ArgumentException.ThrowIfNullOrEmpty(contentType);
            SetFrameworkHeader("Content-Type", contentType);
        }

        _buffer.Clear();
        _suppressedBodyLength = 0;
        _responseState = ResponseState.Buffered;
        if (IsContentLengthForbidden())
        {
            return;
        }

        if (!body.IsEmpty)
        {
            _buffer.Write(body.Span);
        }

        if (string.Equals(RequestMethod, "HEAD", StringComparison.Ordinal))
        {
            _suppressedBodyLength = body.Length;
        }
    }

    internal int BufferedResponseStatusCode
    {
        get
        {
            EnsureActive();
            return _statusCode;
        }
    }

    internal string? GetResponseHeader(string name)
    {
        EnsureActive();
        ArgumentException.ThrowIfNullOrEmpty(name);
        return _headers.TryGetValue(name, out var value) ? value.ToString() : null;
    }

    internal AbandonableResponseScope EnterAbandonableResponseScope()
    {
        EnsureActive();
        var previous = CurrentLease.Value!;
        var branch = new RequestLease(this, _activeGeneration, previous);
        CurrentLease.Value = branch;
        return new AbandonableResponseScope(previous, branch);
    }

    internal TimeoutResponseControlScope EnableTimeoutResponseControl()
    {
        EnsureActive();
        if (Interlocked.Increment(ref _timeoutControlUsers) == 1)
        {
            var requestAborted = _lifetimeFeature?.RequestAborted ?? CancellationToken.None;
            _timeoutCancellation = requestAborted.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(requestAborted)
                : new CancellationTokenSource();
            Volatile.Write(ref _responseControl, ResponseControlAvailable);
        }

        return new TimeoutResponseControlScope(this);
    }

    internal TimeoutResponseClaim TryClaimTimeoutResponse(RequestLease branch)
    {
        ArgumentNullException.ThrowIfNull(branch);

        var control = Interlocked.CompareExchange(
            ref _responseControl,
            ResponseControlSurrendered,
            ResponseControlAvailable);
        if (control == ResponseControlStarting)
        {
            var spinner = new SpinWait();
            do
            {
                spinner.SpinOnce();
                control = Volatile.Read(ref _responseControl);
            }
            while (control == ResponseControlStarting);
        }

        if (control == ResponseControlSurrendered)
        {
            Interlocked.Exchange(ref branch.Surrendered, 1);
            PreventPoolingAndCancelTimeout();
            return TimeoutResponseClaim.AlreadySurrendered;
        }

        if (control == ResponseControlStreaming
            || _responseState is ResponseState.Streaming or ResponseState.Sent or ResponseState.Aborted
            || (_responseFeature?.HasStarted ?? false))
        {
            PreventPoolingAndCancelTimeout();
            AbortResponse();
            return TimeoutResponseClaim.ResponseStarted;
        }

        Interlocked.Exchange(ref branch.Surrendered, 1);
        PreventPoolingAndCancelTimeout();
        return TimeoutResponseClaim.Claimed;
    }

    internal async ValueTask CompleteTimeoutResponse()
    {
        if (Volatile.Read(ref _responseControl) != ResponseControlSurrendered)
        {
            throw new InvalidOperationException("The timeout response was not claimed.");
        }

        _buffer.Clear();
        _headers.Clear();
        _suppressedBodyLength = 0;
        _statusCode = StatusCodes.Status504GatewayTimeout;
        _responseState = ResponseState.Buffered;
        SetFrameworkHeader("Content-Type", "text/plain; charset=utf-8");
        _buffer.Write(GatewayTimeoutBody);

        var suppressBody = string.Equals(RequestMethod, "HEAD", StringComparison.Ordinal);
        if (suppressBody)
        {
            _suppressedBodyLength = GatewayTimeoutBody.Length;
        }

        ApplyResponseHead(GatewayTimeoutBody.Length);
        try
        {
            var aborted = _lifetimeFeature?.RequestAborted ?? CancellationToken.None;
            await ResponseBodyFeature.StartAsync(aborted).ConfigureAwait(false);
            if (!suppressBody)
            {
                ResponseBodyFeature.Writer.Write(GatewayTimeoutBody);
                var flush = await ResponseBodyFeature.Writer.FlushAsync(aborted).ConfigureAwait(false);
                if (flush.IsCanceled)
                {
                    throw new OperationCanceledException(aborted);
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

    private string RequestMethod =>
        _features?.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()?.Method
        ?? throw new InvalidOperationException("The request feature is unavailable.");

    private void DisableTimeoutResponseControl()
    {
        if (Interlocked.Decrement(ref _timeoutControlUsers) != 0)
        {
            return;
        }

        var cancellation = _timeoutCancellation;
        _timeoutCancellation = null;
        Volatile.Write(ref _responseControl, ResponseControlDisabled);
        cancellation!.Dispose();
    }

    private bool ClaimStreamingResponse()
    {
        if (Volatile.Read(ref _responseControl) == ResponseControlDisabled)
        {
            return false;
        }

        var control = Interlocked.CompareExchange(
            ref _responseControl,
            ResponseControlStarting,
            ResponseControlAvailable);
        if (control == ResponseControlSurrendered)
        {
            throw new InvalidOperationException("The request handler can no longer change the response.");
        }

        if (control != ResponseControlAvailable)
        {
            throw new InvalidOperationException("The response has already started streaming.");
        }

        return true;
    }

    private void CompleteStreamingResponseClaim(bool timeoutControlled)
    {
        if (timeoutControlled)
        {
            Volatile.Write(ref _responseControl, ResponseControlStreaming);
        }
    }

    private void PreventPoolingAndCancelTimeout()
    {
        Volatile.Write(ref _preventPooling, 1);
        try
        {
            _timeoutCancellation?.Cancel();
        }
        catch (AggregateException)
        {
        }
    }

    internal readonly struct TimeoutResponseControlScope : IDisposable
    {
        private readonly Context _context;

        internal TimeoutResponseControlScope(Context context)
        {
            _context = context;
        }

        public void Dispose()
        {
            _context.DisableTimeoutResponseControl();
        }
    }

    internal readonly struct AbandonableResponseScope : IDisposable
    {
        private readonly RequestLease _previous;

        internal AbandonableResponseScope(
            RequestLease previous,
            RequestLease branch)
        {
            _previous = previous;
            Branch = branch;
        }

        internal RequestLease Branch { get; }

        public void Dispose()
        {
            CurrentLease.Value = _previous;
        }
    }
}

internal enum TimeoutResponseClaim
{
    Claimed,
    ResponseStarted,
    AlreadySurrendered,
}
