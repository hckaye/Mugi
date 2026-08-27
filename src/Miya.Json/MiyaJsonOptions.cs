namespace Miya.Json;

/// <summary>
/// Limits and behavior switches for MiyaJson. Instances are immutable; the defaults are
/// safe for untrusted input.
/// </summary>
public sealed class MiyaJsonOptions
{
    public static MiyaJsonOptions Default { get; } = new();

    /// <summary>Maximum nesting depth of objects and arrays.</summary>
    public int MaxDepth { get; init; } = 64;

    /// <summary>Maximum length in UTF-8 bytes of a single string token.</summary>
    public int MaxStringByteLength { get; init; } = 1024 * 1024;

    /// <summary>Maximum number of elements in a single array, or members in a single object.</summary>
    public int MaxCollectionSize { get; init; } = 1024 * 1024;

    /// <summary>Rejects NaN and Infinity when false (the default). JSON has no representation for them.</summary>
    public bool AllowNonFiniteNumbers { get; init; }
}
