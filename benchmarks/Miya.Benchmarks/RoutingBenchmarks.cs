using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Order;

namespace Miya.Benchmarks;

[MemoryDiagnoser]
[CategoriesColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class RoutingBenchmarks
{
    private InMemoryRequestHarness _harness = null!;

    [GlobalSetup]
    public void Setup()
    {
        var app = new App();
        app.Get("/", Empty);
        app.Get("/health", Empty);
        app.Get("/about", Empty);
        app.Get("/api/users", Empty);
        app.Get("/api/users/:id", Empty);
        app.Get("/api/users/:id/orders", Empty);
        app.Get("/api/posts/:slug", Empty);
        app.Get("/assets/*path", Empty);
        app.Get("/admin/settings", Empty);
        app.Get("/search", Empty);
        app.NotFound(static context =>
        {
            context.Status(404);
            return ValueTask.CompletedTask;
        });

        _harness = new InMemoryRequestHarness(app.Build());
        Verify("GET", "/health", 200);
        Verify("GET", "/api/users/42", 200);
        Verify("GET", "/assets/css/site.css", 200);
        Verify("GET", "/missing/path", 404);
        Verify("POST", "/health", 405);
    }

    [GlobalCleanup]
    public void Cleanup() => _harness.Dispose();

    [BenchmarkCategory("Routing"), Benchmark(Baseline = true)]
    public int StaticHit() => _harness.Invoke("GET", "/health");

    [BenchmarkCategory("Routing"), Benchmark]
    public int ParameterHit() => _harness.Invoke("GET", "/api/users/42");

    [BenchmarkCategory("Routing"), Benchmark]
    public int WildcardHit() => _harness.Invoke("GET", "/assets/css/site.css");

    [BenchmarkCategory("Routing"), Benchmark]
    public int NotFound() => _harness.Invoke("GET", "/missing/path");

    [BenchmarkCategory("Routing"), Benchmark]
    public int MethodNotAllowed() => _harness.Invoke("POST", "/health");

    private static ValueTask Empty(Context context) => ValueTask.CompletedTask;

    private void Verify(string method, string path, int expectedStatus)
    {
        var actualStatus = _harness.Invoke(method, path);
        if (actualStatus != expectedStatus)
        {
            throw new InvalidOperationException(
                $"Expected {expectedStatus} for {method} {path}, but received {actualStatus}.");
        }
    }
}

[MemoryDiagnoser]
[CategoriesColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class PipelineBenchmarks
{
    private InMemoryRequestHarness _withoutMiddleware = null!;
    private InMemoryRequestHarness _withFiveMiddleware = null!;

    [GlobalSetup]
    public void Setup()
    {
        _withoutMiddleware = CreateHarness(middlewareCount: 0);
        _withFiveMiddleware = CreateHarness(middlewareCount: 5);
        _ = _withoutMiddleware.Invoke("GET", "/");
        _ = _withFiveMiddleware.Invoke("GET", "/");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _withoutMiddleware.Dispose();
        _withFiveMiddleware.Dispose();
    }

    [BenchmarkCategory("Pipeline"), Benchmark(Baseline = true)]
    public int ZeroMiddleware() => _withoutMiddleware.Invoke("GET", "/");

    [BenchmarkCategory("Pipeline"), Benchmark]
    public int FiveMiddleware() => _withFiveMiddleware.Invoke("GET", "/");

    private static InMemoryRequestHarness CreateHarness(int middlewareCount)
    {
        var app = new App();
        for (var index = 0; index < middlewareCount; index++)
        {
            app.Use(static (context, next) => next(context));
        }

        app.Get("/", static context => ValueTask.CompletedTask);
        return new InMemoryRequestHarness(app.Build());
    }
}
