using Mugi.Testing;

namespace Mugi;

public partial class App<TContext>
    where TContext : Context, new()
{
    /// <summary>
    /// Sends an in-process request through the configured pipeline without starting a server.
    /// The method token is normalized to uppercase. Streamed response bodies are collected in full.
    /// The captured headers include values set by the handler and by Mugi's response handling;
    /// transport-level headers added by Kestrel, such as Date and Server, are not present.
    /// </summary>
    /// <param name="method">The HTTP method. Tokens are normalized to uppercase.</param>
    /// <param name="target">
    /// The request target, including an optional query string (for example <c>/users/42?full=1</c>).
    /// </param>
    /// <param name="options">Optional request body and headers.</param>
    public Task<TestResponse> Request(string method, string target, TestRequestOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        ArgumentNullException.ThrowIfNull(target);
        TestHost.ValidateOptions(options);
        return SendInProcessAsync(method.ToUpperInvariant(), target, options);
    }

    private async Task<TestResponse> SendInProcessAsync(
        string method,
        string target,
        TestRequestOptions? options)
    {
        await using var exchange = TestHost.CreateExchange(method, target, options);
        await ExecuteAsync(exchange.Features).ConfigureAwait(false);
        return TestResponse.FromExchange(exchange);
    }
}
