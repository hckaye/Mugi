namespace Miya.Json;

/// <summary>
/// Limits and behavior switches for MiyaJson. Instances are immutable; the defaults are
/// safe for untrusted input.
/// </summary>
public sealed class MiyaJsonOptions
{
    public static MiyaJsonOptions Default { get; } = new();

    /// <summary>Maximum length in bytes of a complete JSON document.</summary>
    public int MaxDocumentByteLength { get; init; } = 1024 * 1024;

    /// <summary>Maximum nesting depth of objects and arrays.</summary>
    public int MaxDepth { get; init; } = 64;

    /// <summary>Maximum length in UTF-8 bytes of a single string token.</summary>
    public int MaxStringByteLength { get; init; } = 1024 * 1024;

    /// <summary>Maximum number of elements in a single array, or members in a single object.</summary>
    public int MaxCollectionSize { get; init; } = 1024 * 1024;

    /// <summary>Maximum number of decimal digits in a single number token.</summary>
    public int MaxNumberDigits { get; init; } = 128;

    /// <summary>Largest temporary byte buffer that may be returned to the shared pool.</summary>
    public int MaxPooledBufferByteLength { get; init; } = 64 * 1024;

    /// <summary>Rejects NaN and Infinity when false (the default). JSON has no representation for them.</summary>
    public bool AllowNonFiniteNumbers { get; init; }

    /// <summary>Cancellation checked during long serialization and parsing operations.</summary>
    public CancellationToken CancellationToken { get; init; }
}
