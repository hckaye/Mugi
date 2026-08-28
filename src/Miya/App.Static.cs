namespace Miya;

public partial class App<TContext>
    where TContext : Context, new()
{
    /// <summary>Registers a GET route that serves files from a directory or embedded resources.</summary>
    /// <param name="prefix">The URL prefix to serve. A root prefix of "/" is supported.</param>
    /// <param name="options">The static file source and response options.</param>
    /// <returns>This app.</returns>
    /// <remarks>
    /// The route serves the configured index file for the prefix and for paths ending in '/'.
    /// Filesystem paths are checked lexically under the configured root. Symlinks inside that root
    /// are allowed. A file replacement between metadata lookup and opening is handled using the
    /// opened stream length; the write timestamp is read immediately before opening because the
    /// portable BCL API does not expose a file-handle timestamp operation. The file ETag combines
    /// that timestamp and length, which avoids reading the complete file while providing a useful
    /// validator for static files.
    ///
    /// A missing or rejected file invokes the app's configured <see cref="NotFound"/> handler.
    /// The route supports one byte range for filesystem files. Embedded resources use ETags but do
    /// not advertise or process ranges.
    /// </remarks>
    public App<TContext> Static(string prefix, StaticOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var normalizedPrefix = NormalizePrefix(prefix);
        var registration = StaticRegistration.Create(options);
        var handler = new StaticHandler<TContext>(this, registration);

        Get(normalizedPrefix, handler.InvokeRoot);
        var wildcard = normalizedPrefix == "/"
            ? "/*path"
            : string.Concat(normalizedPrefix, "/*path");
        Get(wildcard, handler.InvokePath);
        return this;
    }

    internal ValueTask InvokeStaticNotFound(TContext context)
    {
        context.ClearRouteParameters();
        return _notFound(context);
    }
}
