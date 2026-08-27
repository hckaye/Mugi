namespace Miya.Schema;

public static class EndpointExtensions
{
    public static App<C> Get<C, T>(
        this App<C> app,
        string pattern,
        Schema<T> schema,
        Func<C, T, ValueTask> handler)
        where C : Context, new() => OnCore(app, "GET", pattern, schema, handler);

    public static App<C> Post<C, T>(
        this App<C> app,
        string pattern,
        Schema<T> schema,
        Func<C, T, ValueTask> handler)
        where C : Context, new() => OnCore(app, "POST", pattern, schema, handler);

    public static App<C> Put<C, T>(
        this App<C> app,
        string pattern,
        Schema<T> schema,
        Func<C, T, ValueTask> handler)
        where C : Context, new() => OnCore(app, "PUT", pattern, schema, handler);

    public static App<C> Patch<C, T>(
        this App<C> app,
        string pattern,
        Schema<T> schema,
        Func<C, T, ValueTask> handler)
        where C : Context, new() => OnCore(app, "PATCH", pattern, schema, handler);

    public static App<C> Delete<C, T>(
        this App<C> app,
        string pattern,
        Schema<T> schema,
        Func<C, T, ValueTask> handler)
        where C : Context, new() => OnCore(app, "DELETE", pattern, schema, handler);

    public static App<C> Head<C, T>(
        this App<C> app,
        string pattern,
        Schema<T> schema,
        Func<C, T, ValueTask> handler)
        where C : Context, new() => OnCore(app, "HEAD", pattern, schema, handler);

    public static App<C> Options<C, T>(
        this App<C> app,
        string pattern,
        Schema<T> schema,
        Func<C, T, ValueTask> handler)
        where C : Context, new() => OnCore(app, "OPTIONS", pattern, schema, handler);

    public static App<C> On<C, T>(
        this App<C> app,
        string method,
        string pattern,
        Schema<T> schema,
        Func<C, T, ValueTask> handler)
        where C : Context, new() => OnCore(app, method, pattern, schema, handler);

    private static App<C> OnCore<C, T>(
        App<C> app,
        string method,
        string pattern,
        Schema<T> schema,
        Func<C, T, ValueTask> handler)
        where C : Context, new()
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(handler);

        return app.On(method, pattern, async context =>
        {
            var result = await schema.Binder.Bind(context).ConfigureAwait(false);

            if (!result.Success)
            {
                return;
            }

            await handler(context, result.Value).ConfigureAwait(false);
        });
    }
}
