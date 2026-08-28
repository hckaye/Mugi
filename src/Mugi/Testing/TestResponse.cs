using System.Text;
using Microsoft.AspNetCore.Http;

namespace Mugi.Testing;

/// <summary>
/// A response captured from an in-process <c>App.Request</c> call. Headers include values set by
/// the handler and by Mugi's response handling (for example Content-Type and Content-Length).
/// Transport-level headers added by Kestrel, such as Date and Server, are not present.
/// </summary>
public sealed class TestResponse
{
    private readonly KeyValuePair<string, string>[] _headers;
    private readonly byte[] _body;

    private TestResponse(int status, KeyValuePair<string, string>[] headers, byte[] body)
    {
        Status = status;
        _headers = headers;
        _body = body;
    }

    /// <summary>Gets the HTTP status code.</summary>
    public int Status { get; }

    /// <summary>
    /// Gets the captured response headers in the order they were applied. Repeated names, such as
    /// multiple Set-Cookie values, appear as separate entries.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> Headers => _headers;

    /// <summary>Gets the captured response body.</summary>
    public ReadOnlyMemory<byte> Body => _body;

    /// <summary>
    /// Returns the first header value whose name matches <paramref name="name"/>, using
    /// case-insensitive comparison, or null when the header is absent.
    /// </summary>
    public string? Header(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        for (var i = 0; i < _headers.Length; i++)
        {
            if (string.Equals(_headers[i].Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return _headers[i].Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns every header value whose name matches <paramref name="name"/>, using case-insensitive
    /// comparison. The list is empty when the header is absent.
    /// </summary>
    public IReadOnlyList<string> HeaderValues(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var count = 0;
        for (var i = 0; i < _headers.Length; i++)
        {
            if (string.Equals(_headers[i].Key, name, StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        if (count == 0)
        {
            return [];
        }

        var values = new string[count];
        var index = 0;
        for (var i = 0; i < _headers.Length; i++)
        {
            if (string.Equals(_headers[i].Key, name, StringComparison.OrdinalIgnoreCase))
            {
                values[index++] = _headers[i].Value;
            }
        }

        return values;
    }

    /// <summary>Decodes the response body as UTF-8 text.</summary>
    public string Text() => Encoding.UTF8.GetString(_body);

    /// <summary>
    /// Deserializes the response body as JSON using the codec registered for <typeparamref name="T"/>.
    /// </summary>
    public T? Json<T>()
    {
        var codec = global::Mugi.Json.Json.GetCodec<T>();
        return global::Mugi.Json.Json.Deserialize(_body, codec);
    }

    internal static TestResponse FromExchange(TestExchange exchange)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        return new TestResponse(
            exchange.Response.StatusCode,
            CopyHeaders(exchange.Response.Headers),
            exchange.ResponseBody.Body.ToArray());
    }

    private static KeyValuePair<string, string>[] CopyHeaders(IHeaderDictionary headers)
    {
        var count = 0;
        foreach (var pair in headers)
        {
            count += pair.Value.Count;
        }

        if (count == 0)
        {
            return [];
        }

        var copied = new KeyValuePair<string, string>[count];
        var index = 0;
        foreach (var pair in headers)
        {
            var values = pair.Value;
            for (var i = 0; i < values.Count; i++)
            {
                copied[index++] = new KeyValuePair<string, string>(pair.Key, values[i] ?? "");
            }
        }

        return copied;
    }
}
