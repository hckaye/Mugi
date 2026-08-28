namespace Mugi;

/// <summary>A validated route pattern that can be reused without parsing it again.</summary>
public sealed class RouteTemplate
{
    private RouteTemplate(RoutePattern pattern)
    {
        Pattern = pattern;
    }

    internal RoutePattern Pattern { get; }

    /// <summary>Parses and validates a route pattern.</summary>
    public static RouteTemplate Parse(string pattern) => new(RoutePattern.Parse(pattern));

    /// <summary>Creates a route template from generator-validated segment data.</summary>
    public static RouteTemplate Precompiled(
        string pattern,
        string[] segmentValues,
        byte[] segmentKinds,
        int[] parameterIndices,
        string[] parameterNames)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(segmentValues);
        ArgumentNullException.ThrowIfNull(segmentKinds);
        ArgumentNullException.ThrowIfNull(parameterIndices);
        ArgumentNullException.ThrowIfNull(parameterNames);

        if (segmentValues.Length != segmentKinds.Length || segmentValues.Length != parameterIndices.Length)
        {
            throw new ArgumentException("Precompiled route segment arrays must have the same length.");
        }

        var segments = new RouteSegment[segmentValues.Length];
        for (var index = 0; index < segments.Length; index++)
        {
            var kind = (RouteSegmentKind)segmentKinds[index];
            if (kind is not (RouteSegmentKind.Static or RouteSegmentKind.Parameter or RouteSegmentKind.Wildcard))
            {
                throw new ArgumentOutOfRangeException(nameof(segmentKinds), "A route segment kind is invalid.");
            }

            segments[index] = new RouteSegment(kind, segmentValues[index], parameterIndices[index]);
        }

        return new RouteTemplate(RoutePattern.CreatePrecompiled(
            pattern,
            segments,
            (string[])parameterNames.Clone()));
    }
}
