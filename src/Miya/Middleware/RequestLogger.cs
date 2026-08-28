using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace Miya.Middleware;

/// <summary>
/// Middleware that writes one access log line after each request completes.
/// </summary>
public static class RequestLogger
{
    /// <summary>
    /// Creates middleware that logs the method, path, status code, and elapsed milliseconds.
    /// </summary>
    /// <param name="options">Optional writer configuration. When omitted, output goes to <see cref="Console.Out"/>.</param>
    /// <returns>Middleware written against <see cref="Context"/>.</returns>
    public static Middleware<Context> Middleware(RequestLoggerOptions? options = null)
    {
        var writer = options?.Writer ?? Console.Out;
        ArgumentNullException.ThrowIfNull(writer);
        writer = TextWriter.Synchronized(writer);

        return async (context, next) =>
        {
            var startedAt = Stopwatch.GetTimestamp();
            try
            {
                await next(context).ConfigureAwait(false);
            }
            catch
            {
                WriteLog(writer, context, StatusCodes.Status500InternalServerError, startedAt);
                throw;
            }

            WriteLog(writer, context, context.ResponseStatusCode, startedAt);
        };
    }

    private static void WriteLog(TextWriter writer, Context context, int status, long startedAt)
    {
        var elapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        var newLine = writer.NewLine;
        writer.Write(string.Create(
            CultureInfo.InvariantCulture,
            $"{context.Req.Method} {context.Req.Path} {status} {elapsedMs:0.0}ms{newLine}"));
    }
}

/// <summary>
/// Options for <see cref="RequestLogger"/>.
/// </summary>
public sealed class RequestLoggerOptions
{
    /// <summary>
    /// Gets the writer that receives log lines. When omitted, <see cref="Console.Out"/> is used.
    /// </summary>
    public TextWriter? Writer { get; init; }
}
