namespace Mugi;

/// <summary>
/// Adapts middleware written against <see cref="Context"/> so it can run on any <see cref="App{TContext}"/>.
/// </summary>
public static class AppMiddlewareExtensions
{
    /// <summary>
    /// Adds middleware written against <see cref="Context"/> to <paramref name="app"/>.
    /// Adapting onto a typed <see cref="App{TContext}"/> allocates two small objects per
    /// request (the next shim and its closure). <see cref="App"/> does not use this adapter;
    /// the instance <see cref="App{TContext}.Use(Middleware{TContext})"/> method wins.
    /// </summary>
    /// <param name="app">The application that receives the middleware.</param>
    /// <param name="middleware">The middleware to adapt and register.</param>
    /// <typeparam name="TContext">The application context type.</typeparam>
    /// <returns>The same <paramref name="app"/> instance.</returns>
    public static App<TContext> Use<TContext>(this App<TContext> app, Middleware<Context> middleware)
        where TContext : Context, new()
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(middleware);
        return app.Use(Adapt<TContext>(middleware));
    }

    /// <summary>
    /// Adds path-scoped middleware written against <see cref="Context"/> to <paramref name="app"/>.
    /// Adapting onto a typed <see cref="App{TContext}"/> allocates two small objects per
    /// request (the next shim and its closure). <see cref="App"/> does not use this adapter;
    /// the instance <see cref="App{TContext}.Use(string, Middleware{TContext})"/> method wins.
    /// </summary>
    /// <param name="app">The application that receives the middleware.</param>
    /// <param name="pattern">The route pattern that limits where the middleware runs.</param>
    /// <param name="middleware">The middleware to adapt and register.</param>
    /// <typeparam name="TContext">The application context type.</typeparam>
    /// <returns>The same <paramref name="app"/> instance.</returns>
    public static App<TContext> Use<TContext>(
        this App<TContext> app,
        string pattern,
        Middleware<Context> middleware)
        where TContext : Context, new()
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(middleware);
        return app.Use(pattern, Adapt<TContext>(middleware));
    }

    private static Middleware<TContext> Adapt<TContext>(Middleware<Context> middleware)
        where TContext : Context, new()
    {
        return (context, next) => middleware(context, inner =>
        {
            if (!ReferenceEquals(inner, context))
            {
                throw new InvalidOperationException(
                    "Middleware typed against Context must call next with the same context instance.");
            }

            return next(context);
        });
    }
}
