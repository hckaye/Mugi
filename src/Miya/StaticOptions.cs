namespace Miya;

/// <summary>Configures a static file route.</summary>
public sealed class StaticOptions
{
    /// <summary>Gets the filesystem directory used as the static file root.</summary>
    public string? Root { get; init; }

    /// <summary>Gets the embedded resource source used for static files.</summary>
    public StaticSource? Source { get; init; }

    /// <summary>
    /// Gets the file served for a directory request. Set this to an empty string to disable index files.
    /// </summary>
    public string Index { get; init; } = "index.html";

    /// <summary>Gets the cache policy sent with every static response when set.</summary>
    public string? CacheControl { get; init; }

    /// <summary>Gets a value indicating whether filesystem files may use sibling Brotli or gzip files.</summary>
    public bool Precompressed { get; init; } = true;
}
