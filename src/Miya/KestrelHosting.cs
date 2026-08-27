using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Miya;

public partial class App<TContext>
    where TContext : Context, new()
{
    /// <summary>
    /// Runs an HTTP/1.1 cleartext server and blocks the calling thread until shutdown completes.
    /// </summary>
    public void Run(int? port = null)
    {
        var options = new MiyaOptions
        {
            LoggerFactory = StderrLoggerFactory.Instance,
        };

        RunAsyncCore(port, options, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Runs an HTTP/1.1 cleartext server until cancellation or a termination signal requests shutdown.
    /// </summary>
    public Task RunAsync(MiyaOptions? options = null, CancellationToken ct = default) =>
        RunAsyncCore(explicitPort: null, options, ct);

    private async Task RunAsyncCore(
        int? explicitPort,
        MiyaOptions? options,
        CancellationToken ct)
    {
        var server = await StartAsyncCore(explicitPort, options, ct).ConfigureAwait(false);
        try
        {
            await WaitForShutdownRequestAsync(server, ct).ConfigureAwait(false);
        }
        finally
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Starts an HTTP/1.1 cleartext server. Calling UseHttps from ConfigureKestrel is unsupported because
    /// this manual host does not create the DI services that UseHttps requires.
    /// </summary>
    public Task<MiyaServer> StartAsync(
        MiyaOptions? options = null,
        CancellationToken ct = default) =>
        StartAsyncCore(explicitPort: null, options, ct);

    private async Task<MiyaServer> StartAsyncCore(
        int? explicitPort,
        MiyaOptions? options,
        CancellationToken ct)
    {
        var effectiveOptions = options ?? new MiyaOptions();
        effectiveOptions.Validate();
        var port = ResolvePort(
            explicitPort,
            effectiveOptions.Port,
            Environment.GetEnvironmentVariable("PORT"));
        var loggerFactory = effectiveOptions.LoggerFactory ?? NullLoggerFactory.Instance;
        var kestrelOptions = new KestrelServerOptions();
        kestrelOptions.Listen(
            IPAddress.Loopback,
            port,
            static listenOptions => listenOptions.Protocols = HttpProtocols.Http1);

        var transportFactory = new SocketTransportFactory(
            Microsoft.Extensions.Options.Options.Create(new SocketTransportOptions()),
            loggerFactory);
        var kestrel = new KestrelServer(
            Microsoft.Extensions.Options.Options.Create(kestrelOptions),
            transportFactory,
            loggerFactory);

        try
        {
            effectiveOptions.ConfigureKestrel?.Invoke(kestrelOptions);
            var application = new KestrelApplication<TContext>(this, effectiveOptions, Build());
            await kestrel.StartAsync(application, ct).ConfigureAwait(false);

            var addressFeature = kestrel.Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Kestrel did not publish its listening addresses.");
            var addresses = addressFeature.Addresses.ToArray();
            if (addresses.Length == 0)
            {
                throw new InvalidOperationException("Kestrel did not publish a listening address.");
            }

            return new MiyaServer(
                kestrel,
                addresses,
                effectiveOptions.ShutdownTimeout,
                loggerFactory);
        }
        catch
        {
            kestrel.Dispose();
            throw;
        }
    }

    internal static int ResolvePort(
        int? explicitPort,
        int? configuredPort,
        string? environmentPort)
    {
        if (explicitPort is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(
                nameof(explicitPort),
                "Port must be between 0 and 65535.");
        }

        if (explicitPort.HasValue)
        {
            return explicitPort.Value;
        }

        if (configuredPort is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuredPort),
                "Port must be between 0 and 65535.");
        }

        if (configuredPort.HasValue)
        {
            return configuredPort.Value;
        }

        if (int.TryParse(
                environmentPort,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var port)
            && port is >= 0 and <= 65535)
        {
            return port;
        }

        return 3000;
    }

    private static async Task WaitForShutdownRequestAsync(
        MiyaServer server,
        CancellationToken cancellationToken)
    {
        var shutdown = new ShutdownSignal();
        using var cancellationRegistration = cancellationToken.Register(
            static state => ((ShutdownSignal)state!).Request(),
            shutdown);
        using var interrupt = RegisterSignal(PosixSignal.SIGINT, shutdown);
        using var terminate = RegisterSignal(PosixSignal.SIGTERM, shutdown);

        await shutdown.Requested.ConfigureAwait(false);
        await server.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static PosixSignalRegistration? RegisterSignal(
        PosixSignal signal,
        ShutdownSignal shutdown)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        return PosixSignalRegistration.Create(signal, context =>
        {
            if (shutdown.Request())
            {
                context.Cancel = true;
                return;
            }

            Environment.Exit(context.Signal == PosixSignal.SIGINT ? 130 : 143);
        });
    }

    private sealed class ShutdownSignal
    {
        private readonly TaskCompletionSource _requested = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requestCount;

        public Task Requested => _requested.Task;

        public bool Request()
        {
            var first = Interlocked.Increment(ref _requestCount) == 1;
            _requested.TrySetResult();
            return first;
        }
    }
}

/// <summary>
/// Represents a running Miya Kestrel server.
/// </summary>
public sealed class MiyaServer : IAsyncDisposable
{
    private readonly KestrelServer _server;
    private readonly TimeSpan _shutdownTimeout;
    private readonly ILogger _logger;
    private readonly object _sync = new();
    private Task? _stopTask;

    internal MiyaServer(
        KestrelServer server,
        string[] addresses,
        TimeSpan shutdownTimeout,
        ILoggerFactory loggerFactory)
    {
        _server = server;
        _shutdownTimeout = shutdownTimeout;
        _logger = loggerFactory.CreateLogger<MiyaServer>();
        Addresses = new ReadOnlyCollection<string>(addresses);
    }

    /// <summary>
    /// Gets the actual addresses bound by Kestrel after startup.
    /// </summary>
    public IReadOnlyList<string> Addresses { get; }

    /// <summary>
    /// Stops accepting new requests and waits for active requests up to the configured shutdown timeout.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task stopTask;
        lock (_sync)
        {
            _stopTask ??= StopCoreAsync();
            stopTask = _stopTask;
        }

        return cancellationToken.CanBeCanceled
            ? stopTask.WaitAsync(cancellationToken)
            : stopTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => new(StopAsync(CancellationToken.None));

    private async Task StopCoreAsync()
    {
        using var timeout = new CancellationTokenSource(_shutdownTimeout);
        try
        {
            await _server.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Kestrel did not stop within the configured timeout of {ShutdownTimeout}.",
                _shutdownTimeout);
            throw;
        }
        finally
        {
            _server.Dispose();
        }
    }
}

internal sealed class KestrelApplication<TContext> : IHttpApplication<TContext>
    where TContext : Context, new()
{
    private readonly App<TContext> _app;
    private readonly MiyaOptions _options;
    private readonly Handler<TContext> _handler;

    public KestrelApplication(
        App<TContext> app,
        MiyaOptions options,
        Handler<TContext> handler)
    {
        _app = app;
        _options = options;
        _handler = handler;
    }

    public TContext CreateContext(IFeatureCollection contextFeatures) =>
        _app.CreateContext(contextFeatures, _options);

    public Task ProcessRequestAsync(TContext context)
    {
        var operation = _handler(context);
        if (!operation.IsCompletedSuccessfully)
        {
            return operation.AsTask();
        }

        operation.GetAwaiter().GetResult();
        return Task.CompletedTask;
    }

    public void DisposeContext(TContext context, Exception? exception) =>
        _app.ReleaseContext(context);
}

internal sealed class StderrLoggerFactory : ILoggerFactory
{
    public static readonly StderrLoggerFactory Instance = new();

    private StderrLoggerFactory()
    {
    }

    public void AddProvider(ILoggerProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        provider.Dispose();
    }

    public ILogger CreateLogger(string categoryName) => new StderrLogger(categoryName);

    public void Dispose()
    {
    }

    private sealed class StderrLogger(string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            ArgumentNullException.ThrowIfNull(formatter);
            var message = formatter(state, exception);
            lock (Console.Error)
            {
                Console.Error.Write(logLevel);
                Console.Error.Write(": ");
                Console.Error.Write(categoryName);
                Console.Error.Write(": ");
                Console.Error.WriteLine(message);
                if (exception is not null)
                {
                    Console.Error.WriteLine(exception);
                }
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
