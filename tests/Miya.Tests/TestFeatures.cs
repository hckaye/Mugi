using System.Collections;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace Miya.Tests;

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

    public string BodyText => System.Text.Encoding.UTF8.GetString(ResponseBody.Body.ToArray());

    public static TestExchange Create(
        string method = "GET",
        string path = "/",
        string queryString = "",
        byte[]? body = null,
        IReadOnlyDictionary<string, string>? headers = null,
        string? rawTarget = null)
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

        var requestStream = new MemoryStream(body, writable: false);
        var request = new HttpRequestFeature
        {
            Method = method,
            Path = path,
            QueryString = queryString,
            RawTarget = rawTarget ?? string.Concat(path, queryString),
            Headers = requestHeaders,
            Body = requestStream,
        };
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

internal static class TestApp
{
    public static async Task<TestExchange> Send<TContext>(
        App<TContext> app,
        string method = "GET",
        string path = "/",
        string queryString = "",
        byte[]? body = null,
        IReadOnlyDictionary<string, string>? headers = null,
        MiyaOptions? options = null,
        string? rawTarget = null)
        where TContext : Context, new()
    {
        var exchange = TestExchange.Create(method, path, queryString, body, headers, rawTarget);
        try
        {
            await app.ExecuteAsync(exchange.Features, options);
            return exchange;
        }
        catch
        {
            await exchange.DisposeAsync();
            throw;
        }
    }
}
