namespace Mugi;

public delegate ValueTask Handler<TContext>(TContext context)
    where TContext : Context;

public delegate ValueTask Middleware<TContext>(TContext context, Handler<TContext> next)
    where TContext : Context;

public delegate ValueTask ErrorHandler<TContext>(TContext context, Exception exception)
    where TContext : Context;

public interface IPoolableContext
{
    void OnReturn();
}
