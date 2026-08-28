namespace Mugi.Testing;

/// <summary>
/// Optional body and headers for an in-process <see cref="Mugi.App{TContext}.Request(string, string, TestRequestOptions?)"/> call.
/// </summary>
public sealed class TestRequestOptions
{
    /// <summary>
    /// Gets the request body bytes. Mutually exclusive with <see cref="TextBody"/>; setting both throws.
    /// </summary>
    public ReadOnlyMemory<byte> Body { get; init; }

    /// <summary>
    /// Gets the request body as UTF-8 text. Mutually exclusive with a non-empty <see cref="Body"/>; setting both throws.
    /// </summary>
    public string? TextBody { get; init; }

    /// <summary>
    /// Gets request headers. Repeated names are sent as multiple values.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>>? Headers { get; init; }
}
