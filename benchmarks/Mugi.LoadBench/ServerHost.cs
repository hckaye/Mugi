using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using Mugi.Json;

namespace Mugi.LoadBench;

internal static class ServerHost
{
    private const string UserName = "Mugi";

    public static Task RunAsync(string framework, int port) => framework switch
    {
        "mugi" => RunMugiAsync(port),
        "aspnet" => RunAspNetAsync(port),
        _ => throw new ArgumentException($"Unknown framework '{framework}'.", nameof(framework)),
    };

    private static async Task RunMugiAsync(int port)
    {
        var metrics = new ServerMetrics();
        var app = new App();
        app.Use(async (context, next) =>
        {
            metrics.RecordRequest();
            await next(context).ConfigureAwait(false);
        });
        app.Get("/", static context => context.Text("Hello"));
        app.Get("/users/:id", static context =>
            context.Json(new UserResponse(context.Param("id"), UserName)));
        app.Post("/echo", static async context =>
        {
            var payload = await context.Req.Json<EchoPayload>().ConfigureAwait(false)
                ?? throw new Mugi.Json.JsonException("An echo payload is required.");
            await context.Json(payload).ConfigureAwait(false);
        });

        await using var server = await app.StartAsync(new AppOptions
        {
            Port = port,
            Protocols = Protocols.Http1,
        }).ConfigureAwait(false);

        await RunControlLoopAsync(metrics, server.Addresses.Single()).ConfigureAwait(false);
    }

    private static async Task RunAspNetAsync(int port)
    {
        var metrics = new ServerMetrics();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http1;
            });
        });

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            metrics.RecordRequest();
            await next(context).ConfigureAwait(false);
        });
        app.MapGet("/", static () => Results.Text("Hello", "text/plain; charset=utf-8"));
        app.MapGet("/users/{id}", static (string id) =>
            Results.Json(
                new UserResponse(id, UserName),
                LoadBenchJsonContext.Default.UserResponse));
        app.MapPost("/echo", static async (HttpContext context) =>
        {
            var payload = await JsonSerializer.DeserializeAsync(
                context.Request.Body,
                LoadBenchJsonContext.Default.EchoPayload,
                context.RequestAborted).ConfigureAwait(false)
                ?? throw new Microsoft.AspNetCore.Http.BadHttpRequestException("An echo payload is required.");
            return Results.Json(payload, LoadBenchJsonContext.Default.EchoPayload);
        });

        await app.StartAsync().ConfigureAwait(false);
        try
        {
            await RunControlLoopAsync(metrics, app.Urls.Single()).ConfigureAwait(false);
        }
        finally
        {
            await app.StopAsync().ConfigureAwait(false);
        }
    }

    private static async Task RunControlLoopAsync(ServerMetrics metrics, string address)
    {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"READY {address}"));

        while (await Console.In.ReadLineAsync().ConfigureAwait(false) is { } command)
        {
            if (string.Equals(command, "START", StringComparison.Ordinal))
            {
                metrics.Start();
                Console.WriteLine("MEASUREMENT_STARTED");
            }
            else if (string.Equals(command, "STOP", StringComparison.Ordinal))
            {
                var snapshot = metrics.Stop();
                var line = string.Create(
                    CultureInfo.InvariantCulture,
                    $"METRICS {snapshot.RequestCount} {snapshot.AllocatedBytes}");
                Console.Error.WriteLine(line);
                Console.WriteLine(line);
            }
            else if (string.Equals(command, "EXIT", StringComparison.Ordinal))
            {
                return;
            }
            else
            {
                throw new InvalidOperationException($"Unknown control command '{command}'.");
            }
        }
    }
}

internal sealed class ServerMetrics
{
    private long _allocatedAtStart;
    private long _requestCount;
    private int _measuring;

    public void RecordRequest()
    {
        if (Volatile.Read(ref _measuring) != 0)
        {
            Interlocked.Increment(ref _requestCount);
        }
    }

    public void Start()
    {
        if (Volatile.Read(ref _measuring) != 0)
        {
            throw new InvalidOperationException("A measurement is already active.");
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Interlocked.Exchange(ref _requestCount, 0);
        _allocatedAtStart = GC.GetTotalAllocatedBytes(precise: true);
        Volatile.Write(ref _measuring, 1);
    }

    public ServerMetricsSnapshot Stop()
    {
        if (Interlocked.Exchange(ref _measuring, 0) == 0)
        {
            throw new InvalidOperationException("No measurement is active.");
        }

        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - _allocatedAtStart;
        return new ServerMetricsSnapshot(Interlocked.Read(ref _requestCount), allocatedBytes);
    }
}

internal readonly record struct ServerMetricsSnapshot(long RequestCount, long AllocatedBytes);
