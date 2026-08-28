using System.Collections;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace Mugi.Testing;

internal sealed class TestExchange : IAsyncDisposable
{
    private readonly MemoryStream _requestStream;
    private readonly TestRequestBodyPipeFeature _requestBody;

    private TestExchange(
        TestFeatureCollection features,
        HttpResponseFeature response,
        TestResponseBodyFeature responseBody,
        TestRequestLifetimeFeature lifetime,
        MemoryStream requestStream,
        TestRequestBodyPipeFeature requestBody)
    {
        Features = features;
        Response = response;
        ResponseBody = responseBody;
        Lifetime = lifetime;
        _requestStream = requestStream;
        _requestBody = requestBody;
    }

    public TestFeatureCollection Features { get; }

    public HttpResponseFeature Response { get; }

    public TestResponseBodyFeature ResponseBody { get; }

    public TestRequestLifetimeFeature Lifetime { get; }

    public string BodyText => Encoding.UTF8.GetString(ResponseBody.Body.ToArray());

    public static TestExchange Create(
        string method = "GET",
        string path = "/",
        string queryString = "",
        byte[]? body = null,
        IReadOnlyDictionary<string, string>? headers = null,
        string? rawTarget = null,
        string? scheme = null,
        string? protocol = null,
        IHttpConnectionFeature? connection = null,
        bool? upgradable = null,
        string? extendedConnectProtocol = null)
    {
        body ??= [];
        var requestHeaders = new HeaderDictionary();
        if (headers is not null)
        {
            foreach (var header in headers)
            {
                requestHeaders[header.Key] = header.Value;
            }
        }

        return Create(
            method,
            path,
            queryString,
            body,
            requestHeaders,
            rawTarget ?? string.Concat(path, queryString),
            scheme,
            protocol,
            connection,
            upgradable,
            extendedConnectProtocol);
    }

    public static TestExchange Create(
        string method,
        string path,
        string queryString,
        byte[] body,
        HeaderDictionary requestHeaders,
        string rawTarget,
        string? scheme = null,
        string? protocol = null,
        IHttpConnectionFeature? connection = null,
        bool? upgradable = null,
        string? extendedConnectProtocol = null)
    {
        var requestStream = new MemoryStream(body, writable: false);
        var request = new HttpRequestFeature
        {
            Method = method,
            Path = path,
            QueryString = queryString,
            RawTarget = rawTarget,
            Headers = requestHeaders,
            Body = requestStream,
        };
        if (scheme is not null)
        {
            request.Scheme = scheme;
        }

        if (protocol is not null)
        {
            request.Protocol = protocol;
        }

        var response = new HttpResponseFeature
        {
            StatusCode = StatusCodes.Status200OK,
            Headers = new HeaderDictionary(),
        };
        var responseBody = new TestResponseBodyFeature();
        var requestBody = new TestRequestBodyPipeFeature(requestStream);
        var lifetime = new TestRequestLifetimeFeature();
        var features = new TestFeatureCollection();
        features.Set<IHttpRequestFeature>(request);
        features.Set<IHttpResponseFeature>(response);
        features.Set<IHttpResponseBodyFeature>(responseBody);
        features.Set<IRequestBodyPipeFeature>(requestBody);
        features.Set<IHttpRequestLifetimeFeature>(lifetime);
        if (connection is not null)
        {
            features.Set<IHttpConnectionFeature>(connection);
        }

        if (upgradable.HasValue)
        {
            features.Set<IHttpUpgradeFeature>(new TestUpgradeFeature(response, upgradable.Value));
        }

        if (extendedConnectProtocol is not null)
        {
            features.Set<IHttpExtendedConnectFeature>(
                new TestExtendedConnectFeature(response, extendedConnectProtocol));
        }

        return new TestExchange(features, response, responseBody, lifetime, requestStream, requestBody);
    }

    public async ValueTask DisposeAsync()
    {
        await _requestBody.Reader.CompleteAsync();
        await ResponseBody.DisposeAsync();
        _requestStream.Dispose();
        Lifetime.Dispose();
    }
}

internal sealed class TestFeatureCollection : IFeatureCollection
{
    private readonly Dictionary<Type, object> _features = new();

    public bool IsReadOnly => false;

    public int Revision { get; private set; }

    public object? this[Type key]
    {
        get => _features.TryGetValue(key, out var value) ? value : null;
        set
        {
            if (value is null)
            {
                _features.Remove(key);
            }
            else
            {
                _features[key] = value;
            }

            Revision++;
        }
    }

    public TFeature? Get<TFeature>() => this[typeof(TFeature)] is TFeature feature ? feature : default;

    public void Set<TFeature>(TFeature? instance)
    {
        this[typeof(TFeature)] = instance;
    }

    public IEnumerator<KeyValuePair<Type, object>> GetEnumerator() => _features.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class TestRequestBodyPipeFeature : IRequestBodyPipeFeature
{
    public TestRequestBodyPipeFeature(Stream stream)
    {
        Reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
    }

    public PipeReader Reader { get; }
}

internal sealed class TestResponseBodyFeature : IHttpResponseBodyFeature, IAsyncDisposable
{
    public TestResponseBodyFeature()
    {
        Body = new MemoryStream();
        Writer = PipeWriter.Create(Body, new StreamPipeWriterOptions(leaveOpen: true));
    }

    public MemoryStream Body { get; }

    public Stream Stream => Body;

    public PipeWriter Writer { get; }

    public bool Started { get; private set; }

    public bool Completed { get; private set; }

    public void DisableBuffering()
    {
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Started = true;
        return Task.CompletedTask;
    }

    public Task SendFileAsync(
        string path,
        long offset,
        long? count,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task CompleteAsync()
    {
        Completed = true;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await Writer.CompleteAsync();
        Body.Dispose();
    }
}

internal sealed class TestRequestLifetimeFeature : IHttpRequestLifetimeFeature, IDisposable
{
    private readonly CancellationTokenSource _source = new();

    public CancellationToken RequestAborted
    {
        get => _source.Token;
        set => throw new NotSupportedException();
    }

    public bool WasAborted { get; private set; }

    public void Abort()
    {
        WasAborted = true;
        _source.Cancel();
    }

    public void Dispose() => _source.Dispose();
}

internal static class TestHost
{
    public static TestExchange CreateExchange(string method, string target, TestRequestOptions? options)
    {
        SplitTarget(target, out var path, out var queryString);
        var body = ResolveBody(options, out var bodyProvided);
        var requestHeaders = new HeaderDictionary();
        if (options?.Headers is { } headers)
        {
            for (var i = 0; i < headers.Count; i++)
            {
                var header = headers[i];
                ArgumentException.ThrowIfNullOrEmpty(header.Key);
                ArgumentNullException.ThrowIfNull(header.Value);
                requestHeaders[header.Key] = requestHeaders.TryGetValue(header.Key, out var existing)
                    ? StringValues.Concat(existing, header.Value)
                    : new StringValues(header.Value);
            }
        }

        if (bodyProvided && !requestHeaders.ContainsKey("Content-Length"))
        {
            requestHeaders["Content-Length"] = body.Length.ToString(CultureInfo.InvariantCulture);
        }

        return TestExchange.Create(method, path, queryString, body, requestHeaders, target);
    }

    public static void ValidateOptions(TestRequestOptions? options)
    {
        if (options is not null && options.TextBody is not null && !options.Body.IsEmpty)
        {
            throw new ArgumentException("Body and TextBody cannot both be set.", nameof(options));
        }
    }

    private static byte[] ResolveBody(TestRequestOptions? options, out bool bodyProvided)
    {
        if (options is null)
        {
            bodyProvided = false;
            return [];
        }

        if (options.TextBody is not null)
        {
            bodyProvided = true;
            return Encoding.UTF8.GetBytes(options.TextBody);
        }

        if (!options.Body.IsEmpty)
        {
            bodyProvided = true;
            return options.Body.ToArray();
        }

        bodyProvided = false;
        return [];
    }

    private static void SplitTarget(string target, out string path, out string queryString)
    {
        var queryIndex = target.IndexOf('?');
        if (queryIndex < 0)
        {
            path = target;
            queryString = "";
            return;
        }

        path = target[..queryIndex];
        queryString = target[queryIndex..];
    }
}

internal sealed class TestUpgradeFeature : IHttpUpgradeFeature
{
    private readonly HttpResponseFeature _response;

    public TestUpgradeFeature(HttpResponseFeature response, bool isUpgradableRequest)
    {
        _response = response;
        IsUpgradableRequest = isUpgradableRequest;
    }

    public bool IsUpgradableRequest { get; }

    public bool WasUpgraded { get; private set; }

    public Task<Stream> UpgradeAsync()
    {
        if (!IsUpgradableRequest)
        {
            throw new InvalidOperationException("The request is not upgradable.");
        }

        WasUpgraded = true;
        _response.StatusCode = StatusCodes.Status101SwitchingProtocols;
        _response.Headers["Connection"] = "Upgrade";
        return Task.FromResult(Stream.Null);
    }
}

internal sealed class TestExtendedConnectFeature : IHttpExtendedConnectFeature
{
    private readonly HttpResponseFeature _response;

    public TestExtendedConnectFeature(HttpResponseFeature response, string protocol)
    {
        _response = response;
        Protocol = protocol;
    }

    public bool IsExtendedConnect => true;

    public string Protocol { get; }

    public bool WasAccepted { get; private set; }

    public ValueTask<Stream> AcceptAsync()
    {
        WasAccepted = true;
        _response.StatusCode = StatusCodes.Status200OK;
        return new ValueTask<Stream>(Stream.Null);
    }
}
