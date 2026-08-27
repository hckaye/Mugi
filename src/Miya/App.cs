using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Miya.Json;

namespace Miya;

public partial class App<TContext>
    where TContext : Context, new()
{
    private readonly List<RouteEntry<TContext>> _routes = [];
    private readonly List<MiddlewareRegistration<TContext>> _middlewares = [];
    private readonly ContextPool<TContext> _contextPool = new();
    private Handler<TContext> _notFound = DefaultNotFound;
    private ErrorHandler<TContext> _errorHandler = DefaultError;
    private Handler<TContext>? _built;
    private int _registrationOrder;

    public App<TContext> Use(Middleware<TContext> middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        _middlewares.Add(new MiddlewareRegistration<TContext>(middleware, Pattern: null, MountPrefix: null));
        InvalidateBuild();
        return this;
    }

    public App<TContext> Use(string pattern, Middleware<TContext> middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        _middlewares.Add(new MiddlewareRegistration<TContext>(middleware, RoutePattern.Parse(pattern), MountPrefix: null));
        InvalidateBuild();
        return this;
    }

    public App<TContext> Get(string pattern, Handler<TContext> handler) => On("GET", pattern, handler);

    public App<TContext> Post(string pattern, Handler<TContext> handler) => On("POST", pattern, handler);

    public App<TContext> Put(string pattern, Handler<TContext> handler) => On("PUT", pattern, handler);

    public App<TContext> Delete(string pattern, Handler<TContext> handler) => On("DELETE", pattern, handler);

    public App<TContext> Patch(string pattern, Handler<TContext> handler) => On("PATCH", pattern, handler);

    public App<TContext> Head(string pattern, Handler<TContext> handler) => On("HEAD", pattern, handler);

    public App<TContext> Options(string pattern, Handler<TContext> handler) => On("OPTIONS", pattern, handler);

    public App<TContext> All(string pattern, Handler<TContext> handler) => AddRoute(Router<TContext>.AllMethods, pattern, handler);

    public App<TContext> On(string method, string pattern, Handler<TContext> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        ValidateMethod(method);
        return AddRoute(method.ToUpperInvariant(), pattern, handler);
    }

    public App<TContext> Route(string prefix, App<TContext> sub)
    {
        ArgumentNullException.ThrowIfNull(sub);
        var normalizedPrefix = NormalizePrefix(prefix);

        foreach (var route in sub._routes)
        {
            AddRoute(route.Method, CombinePath(normalizedPrefix, route.Pattern.Original), route.Handler);
        }

        foreach (var middleware in sub._middlewares)
        {
            if (middleware.Pattern is not null)
            {
                _middlewares.Add(new MiddlewareRegistration<TContext>(
                    middleware.Middleware,
                    RoutePattern.Parse(CombinePath(normalizedPrefix, middleware.Pattern.Original)),
                    MountPrefix: null));
            }
            else
            {
                var mountPrefix = middleware.MountPrefix is null
                    ? normalizedPrefix
                    : CombinePath(normalizedPrefix, middleware.MountPrefix);
                _middlewares.Add(new MiddlewareRegistration<TContext>(
                    middleware.Middleware,
                    Pattern: null,
                    mountPrefix));
            }
        }

        InvalidateBuild();
        return this;
    }

    public App<TContext> NotFound(Handler<TContext> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _notFound = handler;
        InvalidateBuild();
        return this;
    }

    public App<TContext> OnError(ErrorHandler<TContext> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _errorHandler = handler;
        InvalidateBuild();
        return this;
    }

    public Handler<TContext> Build()
    {
        if (_built is not null)
        {
            return _built;
        }

        ValidateDuplicateRoutes();
        var router = new Router<TContext>(_routes, _notFound);
        Handler<TContext> pipeline = router.Dispatch;

        for (var i = _middlewares.Count - 1; i >= 0; i--)
        {
            var node = new MiddlewareNode<TContext>(_middlewares[i], pipeline, i);
            pipeline = node.InvokeHandler;
        }

        var execution = new ExecutionHandler<TContext>(pipeline, _errorHandler, _middlewares.Count);
        _built = execution.InvokeHandler;
        return _built;
    }

    internal TContext CreateContext(IFeatureCollection features, MiyaOptions? options = null)
    {
        var context = _contextPool.Rent();
        try
        {
            context.Initialize(features, options);
            return context;
        }
        catch
        {
            _contextPool.Return(context);
            throw;
        }
    }

    internal void ReleaseContext(TContext context) => _contextPool.Return(context);

    internal async ValueTask ExecuteAsync(IFeatureCollection features, MiyaOptions? options = null)
    {
        var context = CreateContext(features, options);
        try
        {
            await Build()(context).ConfigureAwait(false);
        }
        finally
        {
            ReleaseContext(context);
        }
    }

    private App<TContext> AddRoute(string method, string pattern, Handler<TContext> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var parsed = RoutePattern.Parse(pattern);
        _routes.Add(new RouteEntry<TContext>(method, parsed, handler, _registrationOrder++));
        InvalidateBuild();
        return this;
    }

    private void ValidateDuplicateRoutes()
    {
        for (var i = 0; i < _routes.Count; i++)
        {
            for (var j = i + 1; j < _routes.Count; j++)
            {
                if (string.Equals(_routes[i].Method, _routes[j].Method, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        _routes[i].Pattern.Original,
                        _routes[j].Pattern.Original,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Duplicate route patterns '{_routes[i].Pattern.Original}' and '{_routes[j].Pattern.Original}' " +
                        $"are registered for method '{_routes[i].Method}'.");
                }
            }
        }
    }

    private void InvalidateBuild() => _built = null;

    private static string NormalizePrefix(string prefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        if (prefix[0] != '/')
        {
            throw new ArgumentException("Route prefixes must start with '/'.", nameof(prefix));
        }

        var normalized = prefix.TrimEnd('/');
        return normalized.Length == 0 ? "/" : normalized;
    }

    private static string CombinePath(string prefix, string path)
    {
        if (prefix == "/")
        {
            return path;
        }

        return string.Concat(prefix, path);
    }

    private static void ValidateMethod(string method)
    {
        foreach (var character in method)
        {
            if (character is < '!' or > '~'
                || character is '(' or ')' or '<' or '>' or '@' or ',' or ';' or ':' or '\\'
                    or '"' or '/' or '[' or ']' or '?' or '=' or '{' or '}')
            {
                throw new ArgumentException("The HTTP method contains an invalid character.", nameof(method));
            }
        }
    }

    private static ValueTask DefaultNotFound(TContext context) => context.NotFound();

    private static ValueTask DefaultError(TContext context, Exception exception)
    {
        var status = exception switch
        {
            BadHttpRequestException badRequest => badRequest.StatusCode,
            MiyaJsonException => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status500InternalServerError,
        };

        context.Status(status);
        return context.Text(status switch
        {
            StatusCodes.Status400BadRequest => "Bad Request",
            StatusCodes.Status413PayloadTooLarge => "Payload Too Large",
            _ => "Internal Server Error",
        });
    }
}

public sealed partial class App : App<Context>
{
}

internal sealed class ExecutionHandler<TContext>
    where TContext : Context
{
    private readonly Handler<TContext> _pipeline;
    private readonly ErrorHandler<TContext> _errorHandler;
    private readonly int _middlewareCount;

    public ExecutionHandler(
        Handler<TContext> pipeline,
        ErrorHandler<TContext> errorHandler,
        int middlewareCount)
    {
        _pipeline = pipeline;
        _errorHandler = errorHandler;
        _middlewareCount = middlewareCount;
        InvokeHandler = Invoke;
    }

    public Handler<TContext> InvokeHandler { get; }

    private async ValueTask Invoke(TContext context)
    {
        context.PrepareMiddlewareSlots(_middlewareCount);
        try
        {
            await _pipeline(context).ConfigureAwait(false);
            await context.CompleteResponse().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.Aborted.IsCancellationRequested)
        {
            context.AbortResponse();
        }
        catch (Exception exception)
        {
            if (context.ResponseStarted)
            {
                context.AbortResponse();
                return;
            }

            context.ResetResponseForError();
            try
            {
                await _errorHandler(context, exception).ConfigureAwait(false);
                await context.CompleteResponse().ConfigureAwait(false);
            }
            catch
            {
                if (context.ResponseStarted)
                {
                    context.AbortResponse();
                    return;
                }

                context.ResetResponseForError();
                context.Status(StatusCodes.Status500InternalServerError);
                await context.Text("Internal Server Error").ConfigureAwait(false);
                await context.CompleteResponse().ConfigureAwait(false);
            }
        }
    }
}

internal sealed class ContextPool<TContext>
    where TContext : Context, new()
{
    private readonly ConcurrentQueue<TContext> _contexts = new();
    private readonly bool _poolingEnabled;
    private readonly bool _callOnReturn;
    private TContext? _firstContext;
    private int _pooledCount;

    public ContextPool()
    {
        _poolingEnabled = ContextPoolPolicy<TContext>.PoolingEnabled;
        _callOnReturn = ContextPoolPolicy<TContext>.CallOnReturn;
        _firstContext = ContextPoolPolicy<TContext>.TakeProbeOrCreate();
    }

    public TContext Rent()
    {
        var first = Interlocked.Exchange(ref _firstContext, null);
        if (first is not null)
        {
            return first;
        }

        if (_poolingEnabled && _contexts.TryDequeue(out var context))
        {
            Interlocked.Decrement(ref _pooledCount);
            return context;
        }

        return new TContext();
    }

    public void Return(TContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var reusable = _poolingEnabled;
        try
        {
            if (_callOnReturn)
            {
                ((IPoolableContext)context).OnReturn();
            }
        }
        catch
        {
            reusable = false;
            throw;
        }
        finally
        {
            context.ResetFrameworkState(reusable);
            if (reusable)
            {
                var count = Interlocked.Increment(ref _pooledCount);
                if (count <= Math.Max(4, Environment.ProcessorCount * 2))
                {
                    _contexts.Enqueue(context);
                }
                else
                {
                    Interlocked.Decrement(ref _pooledCount);
                    context.ResetFrameworkState(retainBuffers: false);
                }
            }
        }
    }
}

internal static class ContextPoolPolicy<TContext>
    where TContext : Context, new()
{
    private static TContext? _probe;

    static ContextPoolPolicy()
    {
        _probe = new TContext();
        PoolingEnabled = typeof(TContext) == typeof(Context) || _probe is IPoolableContext;
        CallOnReturn = _probe is IPoolableContext;
    }

    public static bool PoolingEnabled { get; }

    public static bool CallOnReturn { get; }

    public static TContext TakeProbeOrCreate() =>
        Interlocked.Exchange(ref _probe, null) ?? new TContext();
}
