using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Miya.Json;

namespace Miya;

[Flags]
public enum MiyaProtocols
{
    Http1 = 1,
    Http2 = 2,
    Http3 = 4,
    Http1AndHttp2 = Http1 | Http2,
    Http1AndHttp2AndHttp3 = Http1 | Http2 | Http3,
}

public sealed class MiyaOptions
{
    private MiyaProtocols? _protocols;

    public int? Port { get; init; }

    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>
    /// Gets the certificate used to terminate TLS. A certificate enables HTTP/1.1 and HTTP/2 by default.
    /// </summary>
    public X509Certificate2? Certificate { get; init; }

    /// <summary>
    /// Gets the enabled HTTP protocols. Without a certificate the default is HTTP/1.1. With a certificate
    /// the default is HTTP/1.1 and HTTP/2. Cleartext HTTP/2 requires selecting HTTP/2 alone.
    /// </summary>
    public MiyaProtocols Protocols
    {
        get => _protocols ?? (Certificate is null ? MiyaProtocols.Http1 : MiyaProtocols.Http1AndHttp2);
        init => _protocols = value;
    }

    /// <summary>
    /// Configures Kestrel after Miya adds its listener and before the server starts.
    /// Configure TLS through <see cref="Certificate"/> and protocols through <see cref="Protocols"/>.
    /// </summary>
    public Action<KestrelServerOptions>? ConfigureKestrel { get; init; }

    /// <summary>
    /// Optional service registration for the internal Kestrel host. Miya never requires dependency
    /// injection; this hook exists for advanced Kestrel customization only. Setting it selects the
    /// service-backed hosting path even for cleartext endpoints. The registered services stay inside
    /// the server and are not exposed to handlers or middleware.
    /// </summary>
    public Action<IServiceCollection>? ConfigureServices { get; init; }

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

        const MiyaProtocols allProtocols = MiyaProtocols.Http1 | MiyaProtocols.Http2 | MiyaProtocols.Http3;
        var protocols = Protocols;
        if (protocols == 0 || (protocols & ~allProtocols) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Protocols),
                "Protocols must contain one or more defined HTTP protocol flags.");
        }

        if (Certificate is null)
        {
            if ((protocols & MiyaProtocols.Http3) != 0)
            {
                throw new InvalidOperationException("HTTP/3 requires a TLS certificate.");
            }

            if (protocols is not MiyaProtocols.Http1 and not MiyaProtocols.Http2)
            {
                throw new InvalidOperationException(
                    "A cleartext listener must select exactly HTTP/1.1 or HTTP/2. " +
                    "HTTP/2 prior knowledge cannot share a cleartext listener with HTTP/1.1.");
            }
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
