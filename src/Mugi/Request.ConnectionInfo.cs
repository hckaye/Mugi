using System.Net;
using Microsoft.AspNetCore.Http.Features;

namespace Mugi;

public sealed partial class Request
{
    /// <summary>
    /// The remote IP address of the client, or <see langword="null" /> when the transport
    /// does not provide connection information (for example in-process tests).
    /// </summary>
    public IPAddress? RemoteAddress =>
        _context.Features.Get<IHttpConnectionFeature>()?.RemoteIpAddress;

    /// <summary>
    /// The remote port of the client, or <c>0</c> when the transport does not provide
    /// connection information.
    /// </summary>
    public int RemotePort =>
        _context.Features.Get<IHttpConnectionFeature>()?.RemotePort ?? 0;

    /// <summary>
    /// The local IP address the request was received on, or <see langword="null" /> when the
    /// transport does not provide connection information.
    /// </summary>
    public IPAddress? LocalAddress =>
        _context.Features.Get<IHttpConnectionFeature>()?.LocalIpAddress;

    /// <summary>
    /// The local port the request was received on, or <c>0</c> when the transport does not
    /// provide connection information.
    /// </summary>
    public int LocalPort =>
        _context.Features.Get<IHttpConnectionFeature>()?.LocalPort ?? 0;

    /// <summary>
    /// The HTTP protocol reported by the transport, for example <c>HTTP/1.1</c> or <c>HTTP/2</c>.
    /// </summary>
    public string Protocol => Feature.Protocol;

    /// <summary>
    /// <see langword="true" /> when the request was received over a TLS connection.
    /// </summary>
    public bool IsHttps => string.Equals(Feature.Scheme, "https", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns every decoded value for the query parameter <paramref name="name" />, in request
    /// order. A key with no value (<c>?a</c>) yields a single empty string, matching
    /// <see cref="Query(string)" />. When the key is absent, <see cref="Array.Empty{T}" /> is
    /// returned. Decoding uses the same rules as <see cref="Query(string)" />: <c>+</c> becomes a
    /// space and invalid percent escapes produce the same <see cref="BadHttpRequestException" />.
    /// </summary>
    /// <param name="name">The query parameter name, compared with ordinal case sensitivity.</param>
    /// <returns>
    /// All values for <paramref name="name" /> in request order, or an empty array when the key is
    /// not present.
    /// </returns>
    public string[] QueryAll(string name)
    {
        _context.EnsureActive();
        ArgumentNullException.ThrowIfNull(name);

        var queryString = Feature.QueryString;
        var start = queryString.Length > 0 && queryString[0] == '?' ? 1 : 0;
        List<string>? values = null;
        while (start <= queryString.Length)
        {
            var ampersand = queryString.IndexOf('&', start);
            var end = ampersand < 0 ? queryString.Length : ampersand;
            if (end > start)
            {
                var equals = queryString.IndexOf('=', start, end - start);
                var nameEnd = equals < 0 ? end : equals;
                var valueStart = equals < 0 ? end : equals + 1;
                var key = DecodeQueryPart(queryString.AsSpan(start, nameEnd - start));
                if (string.Equals(key, name, StringComparison.Ordinal))
                {
                    (values ??= new List<string>()).Add(
                        DecodeQueryPart(queryString.AsSpan(valueStart, end - valueStart)));
                }
            }

            if (ampersand < 0)
            {
                break;
            }

            start = ampersand + 1;
        }

        return values is null ? Array.Empty<string>() : values.ToArray();
    }
}
