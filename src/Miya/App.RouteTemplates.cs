namespace Miya;

public partial class App<TContext>
    where TContext : Context, new()
{
    public App<TContext> Get(RouteTemplate pattern, Handler<TContext> handler) => On("GET", pattern, handler);

    public App<TContext> Post(RouteTemplate pattern, Handler<TContext> handler) => On("POST", pattern, handler);

    public App<TContext> Put(RouteTemplate pattern, Handler<TContext> handler) => On("PUT", pattern, handler);

    public App<TContext> Delete(RouteTemplate pattern, Handler<TContext> handler) => On("DELETE", pattern, handler);

    public App<TContext> Patch(RouteTemplate pattern, Handler<TContext> handler) => On("PATCH", pattern, handler);

    public App<TContext> Head(RouteTemplate pattern, Handler<TContext> handler) => On("HEAD", pattern, handler);

    public App<TContext> Options(RouteTemplate pattern, Handler<TContext> handler) => On("OPTIONS", pattern, handler);

    public App<TContext> All(RouteTemplate pattern, Handler<TContext> handler) =>
        AddRoute(Router<TContext>.AllMethods, pattern, handler);

    public App<TContext> On(string method, RouteTemplate pattern, Handler<TContext> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        ValidateMethod(method);
        return AddRoute(method, pattern, handler);
    }

    private App<TContext> AddRoute(string method, RouteTemplate pattern, Handler<TContext> handler)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(handler);
        _routes.Add(new RouteEntry<TContext>(method, pattern.Pattern, handler, _registrationOrder++));
        InvalidateBuild();
        return this;
    }
}
