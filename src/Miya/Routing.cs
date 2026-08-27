using Microsoft.AspNetCore.Http;

namespace Miya;

internal sealed class RoutePattern
{
    private RoutePattern(string original, RouteSegment[] segments, string[] parameterNames)
    {
        Original = original;
        Segments = segments;
        ParameterNames = parameterNames;
        HasWildcard = segments.Length > 0 && segments[^1].Kind == RouteSegmentKind.Wildcard;
    }

    public string Original { get; }

    public RouteSegment[] Segments { get; }

    public string[] ParameterNames { get; }

    public bool HasWildcard { get; }

    public int SegmentCount => Segments.Length;

    public static RoutePattern Parse(string pattern)
    {
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        if (pattern[0] != '/')
        {
            throw new ArgumentException("Route patterns must start with '/'.", nameof(pattern));
        }

        if (pattern == "/")
        {
            return new RoutePattern(pattern, [], []);
        }

        var rawSegments = pattern[1..].Split('/', StringSplitOptions.None);
        var segments = new RouteSegment[rawSegments.Length];
        var parameterNames = new List<string>();
        var uniqueNames = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < rawSegments.Length; i++)
        {
            var raw = rawSegments[i];
            if (raw.StartsWith(':'))
            {
                var name = raw[1..];
                ValidateParameterName(name, pattern);
                if (!uniqueNames.Add(name))
                {
                    throw new ArgumentException($"Route pattern '{pattern}' contains the parameter '{name}' more than once.", nameof(pattern));
                }

                segments[i] = new RouteSegment(RouteSegmentKind.Parameter, name, parameterNames.Count);
                parameterNames.Add(name);
            }
            else if (raw.StartsWith('*'))
            {
                var name = raw[1..];
                ValidateParameterName(name, pattern);
                if (i != rawSegments.Length - 1)
                {
                    throw new ArgumentException($"Wildcard parameters must be the last segment in route pattern '{pattern}'.", nameof(pattern));
                }

                if (!uniqueNames.Add(name))
                {
                    throw new ArgumentException($"Route pattern '{pattern}' contains the parameter '{name}' more than once.", nameof(pattern));
                }

                segments[i] = new RouteSegment(RouteSegmentKind.Wildcard, name, parameterNames.Count);
                parameterNames.Add(name);
            }
            else
            {
                segments[i] = new RouteSegment(RouteSegmentKind.Static, raw, -1);
            }
        }

        return new RoutePattern(pattern, segments, [.. parameterNames]);
    }

    internal static RoutePattern CreatePrecompiled(
        string original,
        RouteSegment[] segments,
        string[] parameterNames) => new(original, segments, parameterNames);

    public bool TryMatch(ReadOnlySpan<char> path, Span<ParameterCapture> captures)
    {
        if (Segments.Length == 0)
        {
            return path.SequenceEqual("/");
        }

        if (path.Length == 0 || path[0] != '/')
        {
            return false;
        }

        var position = 1;
        for (var segmentIndex = 0; segmentIndex < Segments.Length; segmentIndex++)
        {
            var segment = Segments[segmentIndex];
            if (position > path.Length)
            {
                return false;
            }

            if (segment.Kind == RouteSegmentKind.Wildcard)
            {
                if (captures.Length > segment.ParameterIndex)
                {
                    captures[segment.ParameterIndex] = new ParameterCapture(
                        position,
                        path.Length - position,
                        segmentIndex,
                        IsWildcard: true);
                }

                return true;
            }

            var remaining = path[position..];
            var slash = remaining.IndexOf('/');
            var length = slash < 0 ? remaining.Length : slash;
            var value = remaining[..length];

            if (segment.Kind == RouteSegmentKind.Static)
            {
                if (!value.SequenceEqual(segment.Value))
                {
                    return false;
                }
            }
            else
            {
                if (length == 0)
                {
                    return false;
                }

                if (captures.Length > segment.ParameterIndex)
                {
                    captures[segment.ParameterIndex] = new ParameterCapture(
                        position,
                        length,
                        segmentIndex,
                        IsWildcard: false);
                }
            }

            if (slash < 0)
            {
                position = path.Length + 1;
            }
            else
            {
                position += length + 1;
            }
        }

        return position == path.Length + 1;
    }

    public bool StructurallyEquals(RoutePattern other)
    {
        if (Segments.Length != other.Segments.Length)
        {
            return false;
        }

        for (var i = 0; i < Segments.Length; i++)
        {
            var left = Segments[i];
            var right = other.Segments[i];
            if (left.Kind != right.Kind
                || (left.Kind == RouteSegmentKind.Static
                    && !string.Equals(left.Value, right.Value, StringComparison.Ordinal)))
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateParameterName(string name, string pattern)
    {
        if (name.Length == 0 || name.IndexOfAny(':', '*') >= 0)
        {
            throw new ArgumentException($"Route pattern '{pattern}' contains an invalid parameter name.", nameof(pattern));
        }
    }
}

internal readonly record struct RouteSegment(RouteSegmentKind Kind, string Value, int ParameterIndex);

internal enum RouteSegmentKind
{
    Wildcard = 1,
    Parameter = 2,
    Static = 3,
}

internal sealed record RouteEntry<TContext>(
    string Method,
    RoutePattern Pattern,
    Handler<TContext> Handler,
    int RegistrationOrder)
    where TContext : Context;

internal sealed class Router<TContext>
    where TContext : Context
{
    internal const string AllMethods = "*";

    private static readonly string[] StandardMethodOrder =
    [
        "GET",
        "HEAD",
        "POST",
        "PUT",
        "DELETE",
        "PATCH",
        "OPTIONS",
    ];

    private readonly RouteEntry<TContext>[] _routes;
    private readonly Dictionary<string, MethodBuckets> _methods;
    private readonly MethodBuckets? _allMethods;
    private readonly Handler<TContext> _notFound;

    public Router(IReadOnlyList<RouteEntry<TContext>> routes, Handler<TContext> notFound)
    {
        _routes = [.. routes];
        _notFound = notFound;
        _methods = new Dictionary<string, MethodBuckets>(StringComparer.Ordinal);

        var builders = new Dictionary<string, MethodBucketBuilder>(StringComparer.Ordinal);
        foreach (var route in routes)
        {
            if (!builders.TryGetValue(route.Method, out var builder))
            {
                builder = new MethodBucketBuilder();
                builders.Add(route.Method, builder);
            }

            builder.Add(route);
        }

        foreach (var pair in builders)
        {
            var buckets = pair.Value.Build();
            if (pair.Key == AllMethods)
            {
                _allMethods = buckets;
            }
            else
            {
                _methods.Add(pair.Key, buckets);
            }
        }
    }

    public ValueTask Dispatch(TContext context)
    {
        var path = context.Req.Path;
        Context.ValidatePercentEscapes(path.AsSpan());
        var segmentCount = CountSegments(path);
        var method = context.Req.Method;

        RouteEntry<TContext>? route;
        if (string.Equals(method, "HEAD", StringComparison.Ordinal))
        {
            route = FindRoute("HEAD", path, segmentCount, context, includeAll: true)
                ?? FindRoute("GET", path, segmentCount, context, includeAll: false);
        }
        else
        {
            route = FindRoute(method, path, segmentCount, context, includeAll: true);
        }

        if (route is not null)
        {
            CaptureParameters(route.Pattern, path, context);
            return route.Handler(context);
        }

        context.ClearRouteParameters();
        var allowed = GetAllowedMethods(path, context);
        if (allowed.Count == 0)
        {
            return _notFound(context);
        }

        var allowValue = FormatAllow(allowed);
        context.Header("Allow", allowValue);
        context.SetEmptyBody();
        if (string.Equals(method, "OPTIONS", StringComparison.Ordinal))
        {
            context.Status(StatusCodes.Status204NoContent);
        }
        else
        {
            context.Status(StatusCodes.Status405MethodNotAllowed);
        }

        return ValueTask.CompletedTask;
    }

    private RouteEntry<TContext>? FindRoute(
        string method,
        string path,
        int segmentCount,
        TContext context,
        bool includeAll)
    {
        RouteEntry<TContext>? best = null;
        if (_methods.TryGetValue(method, out var buckets))
        {
            best = FindInBuckets(buckets, path, segmentCount, context, best);
        }

        if (includeAll && _allMethods is not null)
        {
            best = FindInBuckets(_allMethods, path, segmentCount, context, best);
        }

        return best;
    }

    private static RouteEntry<TContext>? FindInBuckets(
        MethodBuckets buckets,
        string path,
        int segmentCount,
        TContext context,
        RouteEntry<TContext>? best)
    {
        if (buckets.Exact.TryGetValue(segmentCount, out var exact))
        {
            best = FindBest(exact, path, context, best);
        }

        foreach (var wildcard in buckets.Wildcards)
        {
            if (wildcard.Pattern.SegmentCount <= segmentCount
                && wildcard.Pattern.TryMatch(path.AsSpan(), context.GetParameterCaptureBuffer(wildcard.Pattern.ParameterNames.Length))
                && IsHigherPriority(wildcard, best))
            {
                best = wildcard;
            }
        }

        return best;
    }

    private static RouteEntry<TContext>? FindBest(
        RouteEntry<TContext>[] routes,
        string path,
        TContext context,
        RouteEntry<TContext>? best)
    {
        foreach (var route in routes)
        {
            if (route.Pattern.TryMatch(path.AsSpan(), context.GetParameterCaptureBuffer(route.Pattern.ParameterNames.Length))
                && IsHigherPriority(route, best))
            {
                best = route;
            }
        }

        return best;
    }

    private static bool IsHigherPriority(RouteEntry<TContext> candidate, RouteEntry<TContext>? current)
    {
        if (current is null)
        {
            return true;
        }

        var count = Math.Min(candidate.Pattern.Segments.Length, current.Pattern.Segments.Length);
        for (var i = 0; i < count; i++)
        {
            var left = candidate.Pattern.Segments[i].Kind;
            var right = current.Pattern.Segments[i].Kind;
            if (left != right)
            {
                return left > right;
            }
        }

        return candidate.RegistrationOrder < current.RegistrationOrder;
    }

    private List<string> GetAllowedMethods(string path, TContext context)
    {
        var methods = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in _routes)
        {
            if (route.Method == AllMethods)
            {
                continue;
            }

            if (!route.Pattern.TryMatch(path.AsSpan(), context.GetParameterCaptureBuffer(route.Pattern.ParameterNames.Length)))
            {
                continue;
            }

            methods.Add(route.Method);
            if (string.Equals(route.Method, "GET", StringComparison.Ordinal))
            {
                methods.Add("HEAD");
            }
        }

        if (methods.Count > 0)
        {
            methods.Add("OPTIONS");
        }

        return [.. methods];
    }

    private static string FormatAllow(List<string> methods)
    {
        methods.Sort(static (left, right) =>
        {
            var leftIndex = Array.IndexOf(StandardMethodOrder, left);
            var rightIndex = Array.IndexOf(StandardMethodOrder, right);
            if (leftIndex < 0)
            {
                leftIndex = int.MaxValue;
            }

            if (rightIndex < 0)
            {
                rightIndex = int.MaxValue;
            }

            var comparison = leftIndex.CompareTo(rightIndex);
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left, right);
        });

        return string.Join(", ", methods);
    }

    private static void CaptureParameters(RoutePattern pattern, string path, TContext context)
    {
        var captures = context.GetParameterCaptureBuffer(pattern.ParameterNames.Length);
        if (!pattern.TryMatch(path.AsSpan(), captures))
        {
            throw new InvalidOperationException("The selected route no longer matches the request path.");
        }

        context.SetRouteParameters(pattern.ParameterNames, pattern.ParameterNames.Length);
    }

    private static int CountSegments(string path)
    {
        if (path == "/")
        {
            return 0;
        }

        if (path.Length == 0 || path[0] != '/')
        {
            throw new BadHttpRequestException("The request path must start with '/'.", StatusCodes.Status400BadRequest);
        }

        var count = 1;
        foreach (var character in path.AsSpan(1))
        {
            if (character == '/')
            {
                count++;
            }
        }

        return count;
    }

    private sealed class MethodBucketBuilder
    {
        private readonly Dictionary<int, List<RouteEntry<TContext>>> _exact = new();
        private readonly List<RouteEntry<TContext>> _wildcards = [];

        public void Add(RouteEntry<TContext> route)
        {
            if (route.Pattern.HasWildcard)
            {
                _wildcards.Add(route);
                return;
            }

            if (!_exact.TryGetValue(route.Pattern.SegmentCount, out var routes))
            {
                routes = [];
                _exact.Add(route.Pattern.SegmentCount, routes);
            }

            routes.Add(route);
        }

        public MethodBuckets Build()
        {
            var exact = new Dictionary<int, RouteEntry<TContext>[]>(_exact.Count);
            foreach (var pair in _exact)
            {
                exact.Add(pair.Key, [.. pair.Value]);
            }

            return new MethodBuckets(exact, [.. _wildcards]);
        }
    }

    private sealed record MethodBuckets(
        Dictionary<int, RouteEntry<TContext>[]> Exact,
        RouteEntry<TContext>[] Wildcards);
}

internal sealed record MiddlewareRegistration<TContext>(
    Middleware<TContext> Middleware,
    RoutePattern? Pattern,
    string? MountPrefix)
    where TContext : Context;

internal sealed class MiddlewareNode<TContext>
    where TContext : Context
{
    private readonly Middleware<TContext> _middleware;
    private readonly Handler<TContext> _next;
    private readonly Handler<TContext> _guardedNext;
    private readonly RoutePattern? _pattern;
    private readonly string? _mountPrefix;
    private readonly int _slot;

    public MiddlewareNode(
        MiddlewareRegistration<TContext> registration,
        Handler<TContext> next,
        int slot)
    {
        _middleware = registration.Middleware;
        _pattern = registration.Pattern;
        _mountPrefix = registration.MountPrefix;
        _next = next;
        _slot = slot;
        _guardedNext = InvokeNext;
        InvokeHandler = Invoke;
    }

    public Handler<TContext> InvokeHandler { get; }

    private ValueTask Invoke(TContext context)
    {
        if (!Matches(context.Req.Path))
        {
            return _next(context);
        }

        return _middleware(context, _guardedNext);
    }

    private ValueTask InvokeNext(TContext context)
    {
        context.ClaimNext(_slot);
        return _next(context);
    }

    private bool Matches(string path)
    {
        if (_pattern is not null)
        {
            return _pattern.TryMatch(path.AsSpan(), Span<ParameterCapture>.Empty);
        }

        if (_mountPrefix is null || _mountPrefix == "/")
        {
            return true;
        }

        return path.Equals(_mountPrefix, StringComparison.Ordinal)
            || (path.Length > _mountPrefix.Length
                && path.StartsWith(_mountPrefix, StringComparison.Ordinal)
                && path[_mountPrefix.Length] == '/');
    }
}
