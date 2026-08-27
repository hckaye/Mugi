using System.Collections;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Miya.Benchmarks;

internal sealed class InMemoryRequestHarness : IDisposable
{
    private static readonly Options Options = new();

    private readonly Handler<Context> _handler;
    private readonly Context _context = new();
    private readonly BenchmarkFeatureCollection _features = new();
    private bool _initialized;

    public InMemoryRequestHarness(Handler<Context> handler)
    {
        _handler = handler;
    }

    public int Invoke(string method, string path)
    {
        if (_initialized)
        {
            ContextAccess.ResetFrameworkState(_context, retainBuffers: true);
        }

        _features.Reset(method, path);
        ContextAccess.Initialize(_context, _features, Options);
        _initialized = true;
        _handler(_context).GetAwaiter().GetResult();
        return _features.Response.StatusCode;
    }

    public void Dispose()
    {
        if (_initialized)
        {
            ContextAccess.ResetFrameworkState(_context, retainBuffers: false);
            _initialized = false;
        }

        _features.Dispose();
    }
}

internal static class ContextAccess
{
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Initialize")]
    internal static extern void Initialize(
        Context context,
        IFeatureCollection features,
        Options? options);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ResetFrameworkState")]
    internal static extern void ResetFrameworkState(Context context, bool retainBuffers);
}

internal sealed class BenchmarkFeatureCollection : IFeatureCollection, IDisposable
{
    private readonly BenchmarkResponseBodyFeature _responseBody = new();

    public BenchmarkFeatureCollection()
    {
        Request = new HttpRequestFeature
        {
            Body = Stream.Null,
            Headers = new HeaderDictionary(),
            Method = "GET",
            Path = "/",
            QueryString = string.Empty,
        };
        Response = new HttpResponseFeature
        {
            Body = Stream.Null,
            Headers = new HeaderDictionary(),
            StatusCode = StatusCodes.Status200OK,
        };
    }

    public HttpRequestFeature Request { get; }

    public HttpResponseFeature Response { get; }

    public bool IsReadOnly => false;

    public int Revision { get; private set; }

    public object? this[Type key]
    {
        get
        {
            if (key == typeof(IHttpRequestFeature))
            {
                return Request;
            }

            if (key == typeof(IHttpResponseFeature))
            {
                return Response;
            }

            return key == typeof(IHttpResponseBodyFeature) ? _responseBody : null;
        }
        set => throw new NotSupportedException();
    }

    public TFeature? Get<TFeature>() => this[typeof(TFeature)] is TFeature feature ? feature : default;

    public void Set<TFeature>(TFeature? instance) => throw new NotSupportedException();

    public IEnumerator<KeyValuePair<Type, object>> GetEnumerator()
    {
        yield return new KeyValuePair<Type, object>(typeof(IHttpRequestFeature), Request);
        yield return new KeyValuePair<Type, object>(typeof(IHttpResponseFeature), Response);
        yield return new KeyValuePair<Type, object>(typeof(IHttpResponseBodyFeature), _responseBody);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Reset(string method, string path)
    {
        Request.Method = method;
        Request.Path = path;
        Request.QueryString = string.Empty;
        Request.Headers.Clear();
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ReasonPhrase = null;
        Response.Headers.Clear();
        _responseBody.Reset();
        Revision++;
    }

    public void Dispose() => _responseBody.Dispose();
}

internal sealed class BenchmarkResponseBodyFeature : IHttpResponseBodyFeature, IDisposable
{
    public BenchmarkResponseBodyFeature()
    {
        Writer = PipeWriter.Create(Stream.Null, new StreamPipeWriterOptions(leaveOpen: true));
    }

    public Stream Stream => Stream.Null;

    public PipeWriter Writer { get; }

    public void DisableBuffering()
    {
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task SendFileAsync(
        string path,
        long offset,
        long? count,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task CompleteAsync() => Task.CompletedTask;

    public void Reset()
    {
    }

    public void Dispose() => Writer.Complete();
}
