using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Miya.LoadBench;

internal sealed class BenchmarkRunner
{
    private static readonly byte[] EchoBody = Encoding.UTF8.GetBytes(
        "{\"id\":123,\"name\":\"Ada\",\"message\":\"hello from load bench\"}");
    private static readonly Scenario[] Scenarios =
    [
        new("plaintext", HttpMethod.Get, "/", "Hello"),
        new("json-get", HttpMethod.Get, "/users/123", "{\"id\":\"123\",\"name\":\"Miya\"}"),
        new("json-post", HttpMethod.Post, "/echo", Encoding.UTF8.GetString(EchoBody)),
    ];
    private static readonly string[] Frameworks = ["miya", "aspnet"];

    private readonly int _concurrency;
    private readonly TimeSpan _duration;
    private readonly TimeSpan _warmup;
    private readonly int _iterations;

    public BenchmarkRunner(int concurrency, TimeSpan duration, TimeSpan warmup, int iterations)
    {
        _concurrency = concurrency;
        _duration = duration;
        _warmup = warmup;
        _iterations = iterations;
    }

    public async Task RunAsync()
    {
        Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"Logical processors: {Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture)}");
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Concurrency: {_concurrency}, warmup: {_warmup.TotalSeconds:0.###} s, measurement: {_duration.TotalSeconds:0.###} s, iterations: {_iterations}"));
        Console.WriteLine();

        var samples = new List<BenchmarkResult>();
        foreach (var scenario in Scenarios)
        {
            for (var iteration = 0; iteration < _iterations; iteration++)
            {
                var frameworks = iteration % 2 == 0 ? Frameworks : Frameworks.Reverse();
                foreach (var framework in frameworks)
                {
                    Console.Error.WriteLine(
                        $"Running {framework} {scenario.Name}, iteration {iteration + 1}/{_iterations}...");
                    var sample = await RunScenarioAsync(framework, scenario).ConfigureAwait(false);
                    samples.Add(sample);
                    Console.Error.WriteLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"Completed {framework} {scenario.Name}: {sample.Throughput:0} req/s, p99 {sample.P99Milliseconds:0.000} ms"));
                }
            }
        }

        Console.WriteLine($"Results: median of {_iterations.ToString(CultureInfo.InvariantCulture)} iteration(s)");
        Console.WriteLine("| Endpoint | Framework | Throughput (req/s) | p50 (ms) | p99 (ms) | Peak working set (MiB) | Allocated (B/request) |");
        Console.WriteLine("| --- | --- | ---: | ---: | ---: | ---: | ---: |");
        foreach (var scenario in Scenarios)
        {
            foreach (var framework in Frameworks)
            {
                var displayName = framework == "aspnet" ? "ASP.NET Core" : "Miya";
                var result = MedianResult(samples.Where(sample =>
                    string.Equals(sample.Scenario, scenario.Name, StringComparison.Ordinal)
                    && string.Equals(sample.Framework, displayName, StringComparison.Ordinal)));
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"| {result.Scenario} | {result.Framework} | {result.Throughput:0} | {result.P50Milliseconds:0.000} | {result.P99Milliseconds:0.000} | {result.PeakWorkingSetBytes / 1024d / 1024d:0.0} | {result.AllocatedBytesPerRequest:0.0} |"));
            }
        }
    }

    private async Task<BenchmarkResult> RunScenarioAsync(string framework, Scenario scenario)
    {
        await using var server = await ServerProcess.StartAsync(framework).ConfigureAwait(false);
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = _concurrency,
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
            UseCookies = false,
            UseProxy = false,
        };
        using var client = new HttpClient(handler)
        {
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var uri = new Uri(server.Address, scenario.Path);

        await ValidateResponseAsync(client, uri, scenario).ConfigureAwait(false);
        if (_warmup > TimeSpan.Zero)
        {
            await RunLoadAsync(client, uri, scenario, _warmup, collectLatencies: false).ConfigureAwait(false);
        }

        await server.StartMeasurementAsync().ConfigureAwait(false);
        var load = await RunLoadAsync(client, uri, scenario, _duration, collectLatencies: true).ConfigureAwait(false);
        var metrics = await server.StopMeasurementAsync().ConfigureAwait(false);
        var peakWorkingSet = await server.GetPeakWorkingSetAsync().ConfigureAwait(false);

        if (metrics.RequestCount != load.RequestCount)
        {
            throw new InvalidOperationException(
                $"Server counted {metrics.RequestCount} requests but the client completed {load.RequestCount}.");
        }

        return new BenchmarkResult(
            scenario.Name,
            framework == "aspnet" ? "ASP.NET Core" : "Miya",
            load.RequestCount / load.Elapsed.TotalSeconds,
            load.P50Milliseconds,
            load.P99Milliseconds,
            peakWorkingSet,
            metrics.RequestCount == 0 ? 0 : (double)metrics.AllocatedBytes / metrics.RequestCount);
    }

    private async Task<LoadResult> RunLoadAsync(
        HttpClient client,
        Uri uri,
        Scenario scenario,
        TimeSpan duration,
        bool collectLatencies)
    {
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = new Task<WorkerResult>[_concurrency];
        for (var index = 0; index < workers.Length; index++)
        {
            workers[index] = RunWorkerAsync(client, uri, scenario, duration, collectLatencies, startGate.Task);
        }

        var startedAt = Stopwatch.GetTimestamp();
        startGate.SetResult();
        var workerResults = await Task.WhenAll(workers).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        var requestCount = workerResults.Sum(static result => result.RequestCount);
        var errors = workerResults.Sum(static result => result.ErrorCount);
        if (errors != 0)
        {
            var firstError = workerResults.Select(static result => result.FirstError).First(static error => error is not null);
            throw new InvalidOperationException(
                $"The load client observed {errors} failed requests. First error: {firstError}");
        }

        if (!collectLatencies)
        {
            return new LoadResult(requestCount, elapsed, 0, 0);
        }

        var latencies = new long[checked((int)requestCount)];
        var offset = 0;
        foreach (var worker in workerResults)
        {
            worker.Latencies.CopyTo(latencies, offset);
            offset += worker.Latencies.Count;
        }

        Array.Sort(latencies);
        return new LoadResult(
            requestCount,
            elapsed,
            ToMilliseconds(Percentile(latencies, 0.50)),
            ToMilliseconds(Percentile(latencies, 0.99)));
    }

    private static async Task<WorkerResult> RunWorkerAsync(
        HttpClient client,
        Uri uri,
        Scenario scenario,
        TimeSpan duration,
        bool collectLatencies,
        Task startGate)
    {
        var latencies = collectLatencies ? new List<long>(4096) : [];
        var buffer = new byte[256];
        long requestCount = 0;
        long errorCount = 0;
        string? firstError = null;

        await startGate.ConfigureAwait(false);
        var deadline = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            var startedAt = Stopwatch.GetTimestamp();
            try
            {
                using var request = CreateRequest(scenario, uri);
                using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    errorCount++;
                    firstError ??= $"HTTP {(int)response.StatusCode}";
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                while (await stream.ReadAsync(buffer).ConfigureAwait(false) != 0)
                {
                }

                requestCount++;
                if (collectLatencies)
                {
                    latencies.Add(Stopwatch.GetTimestamp() - startedAt);
                }
            }
            catch (Exception exception)
            {
                errorCount++;
                firstError ??= exception.Message;
            }
        }

        return new WorkerResult(requestCount, errorCount, firstError, latencies);
    }

    private static async Task ValidateResponseAsync(HttpClient client, Uri uri, Scenario scenario)
    {
        using var request = CreateRequest(scenario, uri);
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK || !string.Equals(body, scenario.ExpectedBody, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Validation failed for {scenario.Name}: HTTP {(int)response.StatusCode}, body '{body}'.");
        }

        if (response.Version != HttpVersion.Version11)
        {
            throw new InvalidOperationException(
                $"Expected HTTP/1.1 but the server used HTTP/{response.Version}.");
        }
    }

    private static HttpRequestMessage CreateRequest(Scenario scenario, Uri uri)
    {
        var request = new HttpRequestMessage(scenario.Method, uri)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        if (scenario.Method == HttpMethod.Post)
        {
            request.Content = new ByteArrayContent(EchoBody);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        return request;
    }

    private static long Percentile(long[] sortedValues, double percentile)
    {
        if (sortedValues.Length == 0)
        {
            throw new InvalidOperationException("No latency samples were collected.");
        }

        var index = Math.Max(0, (int)Math.Ceiling(sortedValues.Length * percentile) - 1);
        return sortedValues[index];
    }

    private static double ToMilliseconds(long stopwatchTicks) =>
        stopwatchTicks * 1000d / Stopwatch.Frequency;

    private static BenchmarkResult MedianResult(IEnumerable<BenchmarkResult> samples)
    {
        var values = samples.ToArray();
        if (values.Length == 0)
        {
            throw new InvalidOperationException("No benchmark samples were collected.");
        }

        return new BenchmarkResult(
            values[0].Scenario,
            values[0].Framework,
            Median(values.Select(static value => value.Throughput)),
            Median(values.Select(static value => value.P50Milliseconds)),
            Median(values.Select(static value => value.P99Milliseconds)),
            Median(values.Select(static value => value.PeakWorkingSetBytes)),
            Median(values.Select(static value => value.AllocatedBytesPerRequest)));
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2
            : sorted[middle];
    }

    private static long Median(IEnumerable<long> values)
    {
        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2
            : sorted[middle];
    }

    private sealed record Scenario(string Name, HttpMethod Method, string Path, string ExpectedBody);

    private sealed record WorkerResult(
        long RequestCount,
        long ErrorCount,
        string? FirstError,
        List<long> Latencies);

    private readonly record struct LoadResult(
        long RequestCount,
        TimeSpan Elapsed,
        double P50Milliseconds,
        double P99Milliseconds);

    private readonly record struct BenchmarkResult(
        string Scenario,
        string Framework,
        double Throughput,
        double P50Milliseconds,
        double P99Milliseconds,
        long PeakWorkingSetBytes,
        double AllocatedBytesPerRequest);
}

internal sealed class ServerProcess : IAsyncDisposable
{
    private static readonly TimeSpan ProtocolTimeout = TimeSpan.FromSeconds(30);
    private readonly Process _process;
    private readonly CancellationTokenSource _workingSetCancellation = new();
    private readonly Task<long> _sampledWorkingSet;
    private readonly Task<string> _standardError;
    private bool _exited;

    private ServerProcess(Process process, Task<string> standardError, Uri address)
    {
        _process = process;
        _standardError = standardError;
        _sampledWorkingSet = SampleWorkingSetAsync(process.Id, _workingSetCancellation.Token);
        Address = address;
    }

    public Uri Address { get; }

    public static async Task<ServerProcess> StartAsync(string framework)
    {
        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        if (string.IsNullOrEmpty(assemblyPath))
        {
            throw new InvalidOperationException("The benchmark assembly path is unavailable.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(assemblyPath);
        startInfo.ArgumentList.Add("server");
        startInfo.ArgumentList.Add("--framework");
        startInfo.ArgumentList.Add(framework);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add("0");

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the server process.");
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            var ready = await ReadLineAsync(process, static line => line.StartsWith("READY ", StringComparison.Ordinal))
                .ConfigureAwait(false);
            var address = new Uri(ready["READY ".Length..], UriKind.Absolute);
            return new ServerProcess(process, standardError, address);
        }
        catch
        {
            await TerminateAsync(process, standardError).ConfigureAwait(false);
            throw;
        }
    }

    public async Task StartMeasurementAsync()
    {
        await _process.StandardInput.WriteLineAsync("START").ConfigureAwait(false);
        await _process.StandardInput.FlushAsync().ConfigureAwait(false);
        _ = await ReadLineAsync(
            _process,
            static line => string.Equals(line, "MEASUREMENT_STARTED", StringComparison.Ordinal)).ConfigureAwait(false);
    }

    public async Task<ServerMetricsSnapshot> StopMeasurementAsync()
    {
        await _process.StandardInput.WriteLineAsync("STOP").ConfigureAwait(false);
        await _process.StandardInput.FlushAsync().ConfigureAwait(false);
        var line = await ReadLineAsync(
            _process,
            static candidate => candidate.StartsWith("METRICS ", StringComparison.Ordinal)).ConfigureAwait(false);
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var requestCount)
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var allocatedBytes))
        {
            throw new InvalidOperationException($"Invalid server metrics line '{line}'.");
        }

        return new ServerMetricsSnapshot(requestCount, allocatedBytes);
    }

    public async Task<long> GetPeakWorkingSetAsync()
    {
        using var target = Process.GetProcessById(_process.Id);
        target.Refresh();
        var reportedPeak = target.PeakWorkingSet64;
        _workingSetCancellation.Cancel();
        var sampledPeak = await _sampledWorkingSet.ConfigureAwait(false);
        return Math.Max(reportedPeak, sampledPeak);
    }

    public async ValueTask DisposeAsync()
    {
        if (_exited)
        {
            return;
        }

        _exited = true;
        try
        {
            if (!_process.HasExited)
            {
                await _process.StandardInput.WriteLineAsync("EXIT").ConfigureAwait(false);
                await _process.StandardInput.FlushAsync().ConfigureAwait(false);
                await _process.WaitForExitAsync().WaitAsync(ProtocolTimeout).ConfigureAwait(false);
            }

            var standardError = await _standardError.ConfigureAwait(false);
            if (_process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Server process exited with code {_process.ExitCode}:{Environment.NewLine}{standardError}");
            }
        }
        catch
        {
            await TerminateAsync(_process, _standardError).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _workingSetCancellation.Cancel();
            _workingSetCancellation.Dispose();
            _process.Dispose();
        }
    }

    private static async Task<string> ReadLineAsync(Process process, Func<string, bool> predicate)
    {
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync().WaitAsync(ProtocolTimeout).ConfigureAwait(false);
            if (line is null)
            {
                throw new InvalidOperationException("The server exited before completing the control protocol.");
            }

            if (predicate(line))
            {
                return line;
            }
        }
    }

    private static async Task TerminateAsync(Process process, Task<string> standardError)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        await process.WaitForExitAsync().ConfigureAwait(false);
        _ = await standardError.ConfigureAwait(false);
        process.Dispose();
    }

    private static async Task<long> SampleWorkingSetAsync(int processId, CancellationToken cancellationToken)
    {
        long peak = 0;
        using var target = Process.GetProcessById(processId);
        try
        {
            while (true)
            {
                target.Refresh();
                peak = Math.Max(peak, target.WorkingSet64);
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            target.Refresh();
            return Math.Max(peak, target.HasExited ? 0 : target.WorkingSet64);
        }
    }
}
