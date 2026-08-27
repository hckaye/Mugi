namespace Miya.Tests;

public sealed class ContextPoolTests
{
    [Fact]
    public async Task BuiltInContextIsReused()
    {
        var contexts = new List<Context>();
        var app = new App();
        app.Get("/", context =>
        {
            contexts.Add(context);
            return context.Text("ok");
        });

        await using var first = await TestApp.Send(app);
        await using var second = await TestApp.Send(app);

        Assert.Same(contexts[0], contexts[1]);
    }

    [Fact]
    public async Task DerivedContextIsCreatedForEveryRequestByDefault()
    {
        var contexts = new List<DerivedContext>();
        var app = new App<DerivedContext>();
        app.Get("/", context =>
        {
            contexts.Add(context);
            return context.Text(context.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        });

        await using var first = await TestApp.Send(app);
        await using var second = await TestApp.Send(app);

        Assert.Equal(2, contexts.Count);
        Assert.NotSame(contexts[0], contexts[1]);
        Assert.NotEqual(contexts[0].Id, contexts[1].Id);
    }

    [Fact]
    public async Task PoolableDerivedContextIsReusedAndOnReturnClearsUserState()
    {
        var contexts = new List<PoolableDerivedContext>();
        var valuesAtStart = new List<int>();
        var app = new App<PoolableDerivedContext>();
        app.Get("/", context =>
        {
            contexts.Add(context);
            valuesAtStart.Add(context.Value);
            context.Value = 42;
            return context.Text("ok");
        });

        await using var first = await TestApp.Send(app);
        await using var second = await TestApp.Send(app);

        Assert.Same(contexts[0], contexts[1]);
        Assert.Equal([0, 0], valuesAtStart);
        Assert.Equal(2, contexts[0].ReturnCount);
    }

    public sealed class DerivedContext : Context
    {
        private static int _nextId;

        public DerivedContext()
        {
            Id = Interlocked.Increment(ref _nextId);
        }

        public int Id { get; }
    }

    public sealed class PoolableDerivedContext : Context, IPoolableContext
    {
        public int Value { get; set; }

        public int ReturnCount { get; private set; }

        public void OnReturn()
        {
            Value = 0;
            ReturnCount++;
        }
    }
}
