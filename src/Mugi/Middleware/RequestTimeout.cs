namespace Mugi.Middleware;

/// <summary>
/// Limits the time available to a request handler.
/// </summary>
public static class RequestTimeout
{
    private const double MaximumTimeoutMilliseconds = uint.MaxValue - 1d;

    /// <summary>
    /// Creates request timeout middleware.
    /// </summary>
    /// <remarks>
    /// A buffered response is replaced with a 504 response when the deadline expires. If streaming has
    /// started, the connection is aborted because the status code can no longer be changed.
    /// </remarks>
    /// <param name="timeout">The request deadline.</param>
    /// <returns>The configured middleware.</returns>
    public static Middleware<Context> Middleware(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > MaximumTimeoutMilliseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The request timeout must be positive and supported by timers.");
        }

        return new RequestTimeoutMiddleware(timeout).Invoke;
    }

    private sealed class RequestTimeoutMiddleware
    {
        private readonly TimeSpan _timeout;

        internal RequestTimeoutMiddleware(TimeSpan timeout)
        {
            _timeout = timeout;
        }

        internal async ValueTask Invoke(Context context, Handler<Context> next)
        {
            using var timeoutControl = context.EnableTimeoutResponseControl();
            var responseScope = context.EnterAbandonableResponseScope();
            ValueTask nextOperation;
            try
            {
                nextOperation = next(context);
            }
            finally
            {
                responseScope.Dispose();
            }

            if (nextOperation.IsCompleted)
            {
                await nextOperation.ConfigureAwait(false);
                return;
            }

            var nextTask = nextOperation.AsTask();
            using var delayCancellation = new CancellationTokenSource();
            var delayTask = Task.Delay(_timeout, delayCancellation.Token);
            var completed = await Task.WhenAny(nextTask, delayTask).ConfigureAwait(false);
            if (ReferenceEquals(completed, nextTask) || nextTask.IsCompleted)
            {
                delayCancellation.Cancel();
                await nextTask.ConfigureAwait(false);
                return;
            }

            var claim = context.TryClaimTimeoutResponse(responseScope.Branch);
            ObserveCompletion(nextTask);
            if (claim == TimeoutResponseClaim.Claimed)
            {
                await context.CompleteTimeoutResponse().ConfigureAwait(false);
            }
        }

        private static void ObserveCompletion(Task task)
        {
            if (task.IsCompleted)
            {
                _ = task.Exception;
                return;
            }

            _ = task.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
