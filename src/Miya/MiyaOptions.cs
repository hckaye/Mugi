using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using Miya.Json;

namespace Miya;

public sealed class MiyaOptions
{
    public int? Port { get; init; }

    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>
    /// Configures Kestrel after Miya adds its listener and before the server starts.
    /// Miya guarantees only cleartext HTTP/1.1. Calling UseHttps here is unsupported because
    /// the manual host does not create the DI services that UseHttps requires.
    /// </summary>
    public Action<KestrelServerOptions>? ConfigureKestrel { get; init; }

    public int MaxBufferedResponseBytes { get; init; } = 1024 * 1024;

    public int MaxRequestBodyBytes { get; init; } = 30 * 1024 * 1024;

    public int MaxJsonBodyBytes { get; init; } = 1024 * 1024;

    public int MaxRetainedBufferBytes { get; init; } = 64 * 1024;

    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public MiyaJsonOptions Json { get; init; } = MiyaJsonOptions.Default;

    internal void Validate()
    {
        if (Port is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(Port), "Port must be between 0 and 65535.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxBufferedResponseBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxRequestBodyBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxJsonBodyBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxRetainedBufferBytes);

        if (ShutdownTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ShutdownTimeout), "Shutdown timeout must be positive.");
        }

        ArgumentNullException.ThrowIfNull(Json);
    }
}
