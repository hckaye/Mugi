using System.Globalization;

namespace Miya.LoadBench;

internal static class LoadBenchProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length > 0 && string.Equals(args[0], "server", StringComparison.Ordinal))
            {
                var framework = GetOption(args, "--framework")
                    ?? throw new ArgumentException("The server mode requires --framework.");
                var port = ParseNonNegativeInt(GetOption(args, "--port") ?? "0", "--port");
                await ServerHost.RunAsync(framework, port).ConfigureAwait(false);
                return 0;
            }

            if (args.Length > 0 && !string.Equals(args[0], "run", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unknown command '{args[0]}'. Use 'run' or 'server'.");
            }

            var concurrency = ParsePositiveInt(GetOption(args, "--concurrency") ?? "128", "--concurrency");
            var duration = ParsePositiveDouble(GetOption(args, "--duration") ?? "10", "--duration");
            var warmup = ParseNonNegativeDouble(GetOption(args, "--warmup") ?? "3", "--warmup");
            var iterations = ParsePositiveInt(GetOption(args, "--iterations") ?? "1", "--iterations");

            var runner = new BenchmarkRunner(
                concurrency,
                TimeSpan.FromSeconds(duration),
                TimeSpan.FromSeconds(warmup),
                iterations);
            await runner.RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (!string.Equals(args[index], name, StringComparison.Ordinal))
            {
                continue;
            }

            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"Option '{name}' requires a value.");
            }

            return args[index + 1];
        }

        return null;
    }

    private static int ParsePositiveInt(string value, string option)
    {
        var parsed = ParseNonNegativeInt(value, option);
        return parsed > 0
            ? parsed
            : throw new ArgumentOutOfRangeException(option, "The value must be positive.");
    }

    private static int ParseNonNegativeInt(string value, string option)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
        {
            throw new ArgumentOutOfRangeException(option, $"Invalid non-negative integer '{value}'.");
        }

        return parsed;
    }

    private static double ParsePositiveDouble(string value, string option)
    {
        var parsed = ParseNonNegativeDouble(value, option);
        return parsed > 0
            ? parsed
            : throw new ArgumentOutOfRangeException(option, "The value must be positive.");
    }

    private static double ParseNonNegativeDouble(string value, string option)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || !double.IsFinite(parsed)
            || parsed < 0)
        {
            throw new ArgumentOutOfRangeException(option, $"Invalid non-negative number '{value}'.");
        }

        return parsed;
    }
}
