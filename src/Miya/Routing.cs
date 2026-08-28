using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

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

    private readonly TrieNode _root;
    private readonly Handler<TContext> _notFound;

    public Router(IReadOnlyList<RouteEntry<TContext>> routes, Handler<TContext> notFound)
    {
        _notFound = notFound;
        var builder = new TrieBuilderNode();
        foreach (var route in routes)
        {
            builder.Add(route);
        }

        _root = builder.Build();
    }

    public ValueTask Dispatch(TContext context)
    {
        var path = context.Req.Path;
        Context.ValidatePercentEscapes(path.AsSpan());
        ValidatePath(path);
        var method = context.Req.Method;

        RouteEntry<TContext>? route;
        if (string.Equals(method, "HEAD", StringComparison.Ordinal))
        {
            route = FindRoute("HEAD", path, includeAll: true)
                ?? FindRoute("GET", path, includeAll: false);
        }
        else if (string.Equals(method, "CONNECT", StringComparison.Ordinal))
        {
            route = FindRoute("CONNECT", path, includeAll: true);
            if (route is null)
            {
                var extendedConnect = context.Features.Get<IHttpExtendedConnectFeature>();
                if (extendedConnect?.IsExtendedConnect == true
                    && string.Equals(
                        extendedConnect.Protocol,
                        "websocket",
                        StringComparison.OrdinalIgnoreCase))
                {
                    // RFC 8441 WebSockets share the endpoint registered for HTTP/1.1 GET.
                    route = FindRoute("GET", path, includeAll: false);
                }
            }
        }
        else
        {
            route = FindRoute(method, path, includeAll: true);
        }

        if (route is not null)
        {
            CaptureParameters(route.Pattern, path, context);
            return route.Handler(context);
        }

        context.ClearRouteParameters();
        var allowed = new AllowAccumulator();
        CollectAllowedMethods(path, ref allowed);
        if (allowed.IsEmpty)
        {
            return _notFound(context);
        }

        context.Header("Allow", allowed.Format());
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
        bool includeAll)
    {
        if (path.Length == 1)
        {
            return _root.FindRoute(method, includeAll);
        }

        return FindRoute(_root, path.AsSpan(), 1, method, includeAll);
    }

    private static RouteEntry<TContext>? FindRoute(
        TrieNode node,
        ReadOnlySpan<char> path,
        int position,
        string method,
        bool includeAll)
    {
        var remaining = path[position..];
        var slash = remaining.IndexOf('/');
        var length = slash < 0 ? remaining.Length : slash;
        var segment = remaining[..length];
        var hasMore = slash >= 0;
        var nextPosition = position + length + 1;

        var child = node.FindStatic(segment);
        if (child is not null)
        {
            var route = hasMore
                ? FindRoute(child, path, nextPosition, method, includeAll)
                : child.FindRoute(method, includeAll);
            if (route is not null)
            {
                return route;
            }
        }

        child = node.Parameter;
        if (length > 0 && child is not null)
        {
            var route = hasMore
                ? FindRoute(child, path, nextPosition, method, includeAll)
                : child.FindRoute(method, includeAll);
            if (route is not null)
            {
                return route;
            }
        }

        return node.Wildcard?.FindRoute(method, includeAll);
    }

    private void CollectAllowedMethods(string path, ref AllowAccumulator allowed)
    {
        if (path.Length == 1)
        {
            _root.AddAllowedMethods(ref allowed);
            _root.Wildcard?.AddAllowedMethods(ref allowed);
            return;
        }

        CollectAllowedMethods(_root, path.AsSpan(), 1, ref allowed);
    }

    private static void CollectAllowedMethods(
        TrieNode node,
        ReadOnlySpan<char> path,
        int position,
        ref AllowAccumulator allowed)
    {
        var remaining = path[position..];
        var slash = remaining.IndexOf('/');
        var length = slash < 0 ? remaining.Length : slash;
        var segment = remaining[..length];
        var hasMore = slash >= 0;
        var nextPosition = position + length + 1;

        var child = node.FindStatic(segment);
        if (child is not null)
        {
            if (hasMore)
            {
                CollectAllowedMethods(child, path, nextPosition, ref allowed);
            }
            else
            {
                child.AddAllowedMethods(ref allowed);
            }
        }

        child = node.Parameter;
        if (length > 0 && child is not null)
        {
            if (hasMore)
            {
                CollectAllowedMethods(child, path, nextPosition, ref allowed);
            }
            else
            {
                child.AddAllowedMethods(ref allowed);
            }
        }

        node.Wildcard?.AddAllowedMethods(ref allowed);
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

    private static void ValidatePath(string path)
    {
        if (path.Length == 0 || path[0] != '/')
        {
            throw new BadHttpRequestException("The request path must start with '/'.", StatusCodes.Status400BadRequest);
        }
    }

    private static int GetStandardMethodIndex(string method)
    {
        for (var i = 0; i < StandardMethodOrder.Length; i++)
        {
            if (string.Equals(method, StandardMethodOrder[i], StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private sealed class TrieBuilderNode
    {
        private readonly Dictionary<string, TrieBuilderNode> _static = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RouteEntry<TContext>> _routes = new(StringComparer.Ordinal);
        private TrieBuilderNode? _parameter;
        private TrieBuilderNode? _wildcard;

        public void Add(RouteEntry<TContext> route)
        {
            var node = this;
            foreach (var segment in route.Pattern.Segments)
            {
                if (segment.Kind == RouteSegmentKind.Static)
                {
                    if (!node._static.TryGetValue(segment.Value, out var child))
                    {
                        child = new TrieBuilderNode();
                        node._static.Add(segment.Value, child);
                    }

                    node = child;
                }
                else if (segment.Kind == RouteSegmentKind.Parameter)
                {
                    node._parameter ??= new TrieBuilderNode();
                    node = node._parameter;
                }
                else
                {
                    node._wildcard ??= new TrieBuilderNode();
                    node = node._wildcard;
                }
            }

            if (!node._routes.TryGetValue(route.Method, out var existing)
                || route.RegistrationOrder < existing.RegistrationOrder)
            {
                node._routes[route.Method] = route;
            }
        }

        public TrieNode Build()
        {
            var staticChildren = new StaticEdge[_static.Count];
            var staticIndex = 0;
            foreach (var pair in _static)
            {
                staticChildren[staticIndex++] = new StaticEdge(pair.Key, pair.Value.Build());
            }

            Array.Sort(
                staticChildren,
                static (left, right) => StringComparer.Ordinal.Compare(left.Segment, right.Segment));

            RouteEntry<TContext>? allRoute = null;
            var methodRoutes = new MethodRoute[_routes.Count];
            var methodIndex = 0;
            uint allowMask = 0;
            var customMethodCount = 0;
            foreach (var pair in _routes)
            {
                if (string.Equals(pair.Key, AllMethods, StringComparison.Ordinal))
                {
                    allRoute = pair.Value;
                    continue;
                }

                methodRoutes[methodIndex++] = new MethodRoute(pair.Key, pair.Value);
                var standardIndex = GetStandardMethodIndex(pair.Key);
                if (standardIndex >= 0)
                {
                    allowMask |= 1u << standardIndex;
                }
                else
                {
                    customMethodCount++;
                }

                if (string.Equals(pair.Key, "GET", StringComparison.Ordinal))
                {
                    allowMask |= 1u << 1;
                }
            }

            if (methodIndex > 0)
            {
                allowMask |= 1u << 6;
            }

            if (methodIndex != methodRoutes.Length)
            {
                Array.Resize(ref methodRoutes, methodIndex);
            }

            Array.Sort(
                methodRoutes,
                static (left, right) => StringComparer.Ordinal.Compare(left.Method, right.Method));

            var customMethods = customMethodCount == 0 ? [] : new string[customMethodCount];
            var customIndex = 0;
            for (var i = 0; i < methodRoutes.Length; i++)
            {
                if (GetStandardMethodIndex(methodRoutes[i].Method) < 0)
                {
                    customMethods[customIndex++] = methodRoutes[i].Method;
                }
            }

            return new TrieNode(
                staticChildren,
                _parameter?.Build(),
                _wildcard?.Build(),
                methodRoutes,
                allRoute,
                allowMask,
                customMethods);
        }
    }

    private sealed class TrieNode
    {
        private readonly StaticEdge[] _static;
        private readonly MethodRoute[] _methodRoutes;
        private readonly RouteEntry<TContext>? _allRoute;
        private readonly uint _allowMask;
        private readonly string[] _customMethods;

        public TrieNode(
            StaticEdge[] staticChildren,
            TrieNode? parameter,
            TrieNode? wildcard,
            MethodRoute[] methodRoutes,
            RouteEntry<TContext>? allRoute,
            uint allowMask,
            string[] customMethods)
        {
            _static = staticChildren;
            Parameter = parameter;
            Wildcard = wildcard;
            _methodRoutes = methodRoutes;
            _allRoute = allRoute;
            _allowMask = allowMask;
            _customMethods = customMethods;
        }

        public TrieNode? Parameter { get; }

        public TrieNode? Wildcard { get; }

        public TrieNode? FindStatic(ReadOnlySpan<char> segment)
        {
            var lower = 0;
            var upper = _static.Length - 1;
            while (lower <= upper)
            {
                var middle = lower + ((upper - lower) / 2);
                var comparison = segment.SequenceCompareTo(_static[middle].Segment.AsSpan());
                if (comparison == 0)
                {
                    return _static[middle].Node;
                }

                if (comparison < 0)
                {
                    upper = middle - 1;
                }
                else
                {
                    lower = middle + 1;
                }
            }

            return null;
        }

        public RouteEntry<TContext>? FindRoute(string method, bool includeAll)
        {
            RouteEntry<TContext>? methodRoute = null;
            var lower = 0;
            var upper = _methodRoutes.Length - 1;
            while (lower <= upper)
            {
                var middle = lower + ((upper - lower) / 2);
                var comparison = StringComparer.Ordinal.Compare(method, _methodRoutes[middle].Method);
                if (comparison == 0)
                {
                    methodRoute = _methodRoutes[middle].Route;
                    break;
                }

                if (comparison < 0)
                {
                    upper = middle - 1;
                }
                else
                {
                    lower = middle + 1;
                }
            }

            if (!includeAll || _allRoute is null)
            {
                return methodRoute;
            }

            return methodRoute is null || _allRoute.RegistrationOrder < methodRoute.RegistrationOrder
                ? _allRoute
                : methodRoute;
        }

        public void AddAllowedMethods(ref AllowAccumulator allowed) =>
            allowed.Add(_allowMask, _customMethods);
    }

    private struct AllowAccumulator
    {
        private uint _standardMask;
        private List<string>? _customMethods;

        public readonly bool IsEmpty => _standardMask == 0 && _customMethods is null;

        public void Add(uint standardMask, string[] customMethods)
        {
            _standardMask |= standardMask;
            for (var i = 0; i < customMethods.Length; i++)
            {
                AddCustom(customMethods[i]);
            }
        }

        public string Format()
        {
            _customMethods?.Sort(StringComparer.Ordinal);
            var methodCount = 0;
            var length = 0;
            for (var i = 0; i < StandardMethodOrder.Length; i++)
            {
                if ((_standardMask & (1u << i)) == 0)
                {
                    continue;
                }

                methodCount++;
                length += StandardMethodOrder[i].Length;
            }

            if (_customMethods is not null)
            {
                methodCount += _customMethods.Count;
                for (var i = 0; i < _customMethods.Count; i++)
                {
                    length += _customMethods[i].Length;
                }
            }

            length += (methodCount - 1) * 2;
            return string.Create(
                length,
                this,
                static (destination, state) => state.WriteTo(destination));
        }

        private readonly void WriteTo(Span<char> destination)
        {
            var position = 0;
            var hasMethod = false;
            for (var i = 0; i < StandardMethodOrder.Length; i++)
            {
                if ((_standardMask & (1u << i)) == 0)
                {
                    continue;
                }

                WriteMethod(StandardMethodOrder[i], destination, ref position, ref hasMethod);
            }

            if (_customMethods is null)
            {
                return;
            }

            for (var i = 0; i < _customMethods.Count; i++)
            {
                WriteMethod(_customMethods[i], destination, ref position, ref hasMethod);
            }
        }

        private void AddCustom(string method)
        {
            if (_customMethods is not null)
            {
                for (var i = 0; i < _customMethods.Count; i++)
                {
                    if (string.Equals(_customMethods[i], method, StringComparison.Ordinal))
                    {
                        return;
                    }
                }
            }

            (_customMethods ??= []).Add(method);
        }

        private static void WriteMethod(
            string method,
            Span<char> destination,
            ref int position,
            ref bool hasMethod)
        {
            if (hasMethod)
            {
                destination[position++] = ',';
                destination[position++] = ' ';
            }

            method.AsSpan().CopyTo(destination[position..]);
            position += method.Length;
            hasMethod = true;
        }
    }

    private readonly record struct StaticEdge(string Segment, TrieNode Node);

    private readonly record struct MethodRoute(string Method, RouteEntry<TContext> Route);
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
