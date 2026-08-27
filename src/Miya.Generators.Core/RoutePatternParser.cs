using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Miya.Generators.Core;

internal static class RoutePatternParser
{
    internal static bool TryParse(string pattern, out RoutePatternSpec? result, out string? error)
    {
        result = null;
        error = null;
        if (string.IsNullOrEmpty(pattern))
        {
            error = "patterns cannot be empty";
            return false;
        }

        if (pattern[0] != '/')
        {
            error = "patterns must start with '/'";
            return false;
        }

        if (pattern == "/")
        {
            result = new RoutePatternSpec(
                ImmutableArray<RouteSegmentSpec>.Empty,
                ImmutableArray<string>.Empty);
            return true;
        }

        var rawSegments = pattern.Substring(1).Split(new[] { '/' }, StringSplitOptions.None);
        var segments = ImmutableArray.CreateBuilder<RouteSegmentSpec>(rawSegments.Length);
        var parameterNames = ImmutableArray.CreateBuilder<string>();
        var uniqueNames = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < rawSegments.Length; index++)
        {
            var raw = rawSegments[index];
            if (raw.StartsWith(":", StringComparison.Ordinal) || raw.StartsWith("*", StringComparison.Ordinal))
            {
                var wildcard = raw[0] == '*';
                var name = raw.Substring(1);
                if (name.Length == 0 || name.IndexOf(':') >= 0 || name.IndexOf('*') >= 0)
                {
                    error = "a parameter name is invalid";
                    return false;
                }

                if (wildcard && index != rawSegments.Length - 1)
                {
                    error = "wildcard parameters must be the last segment";
                    return false;
                }

                if (!uniqueNames.Add(name))
                {
                    error = "the parameter '" + name + "' appears more than once";
                    return false;
                }

                segments.Add(new RouteSegmentSpec(
                    wildcard ? (byte)1 : (byte)2,
                    name,
                    parameterNames.Count));
                parameterNames.Add(name);
            }
            else
            {
                segments.Add(new RouteSegmentSpec(3, raw, -1));
            }
        }

        result = new RoutePatternSpec(segments.ToImmutable(), parameterNames.ToImmutable());
        return true;
    }
}
