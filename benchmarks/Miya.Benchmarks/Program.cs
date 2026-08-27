using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Miya.Benchmarks;
using Perfolizer.Horology;

var shortRun = string.Equals(
    Environment.GetEnvironmentVariable("MIYA_BENCHMARK_SHORT"),
    "1",
    StringComparison.Ordinal);
var finalRun = string.Equals(
    Environment.GetEnvironmentVariable("MIYA_BENCHMARK_FINAL"),
    "1",
    StringComparison.Ordinal);
var serializerComparison =
    !args.Contains("--routing", StringComparer.Ordinal) &&
    !args.Contains("--spanjson", StringComparer.Ordinal);
var finalJob = Job.Default
    .WithWarmupCount(5)
    .WithIterationCount(10)
    .WithLaunchCount(1);
var baseJob = shortRun
    ? Job.ShortRun
    : finalRun && serializerComparison
        ? finalJob
            .WithIterationCount(20)
            .WithIterationTime(TimeInterval.FromMilliseconds(250))
        : finalRun
            ? finalJob
            : Job.Default;
var jitJobId = shortRun ? "JIT-Short" : finalRun ? "JIT-Final" : "JIT";
var aotJobId = shortRun ? "NativeAOT-Short" : finalRun ? "NativeAOT-Final" : "NativeAOT";
var jitConfig = ManualConfig.Create(DefaultConfig.Instance)
    .AddJob(baseJob.WithRuntime(CoreRuntime.Core10_0).WithId(jitJobId));
var aotConfig = ManualConfig.Create(DefaultConfig.Instance)
    .AddJob(baseJob.WithRuntime(NativeAotRuntime.Net10_0).WithId(aotJobId));
var mainConfig = ManualConfig.Create(jitConfig)
    .AddJob(baseJob.WithRuntime(NativeAotRuntime.Net10_0).WithId(aotJobId));

if (args.Contains("--spanjson", StringComparer.Ordinal))
{
    var forwarded = args.Where(argument => !string.Equals(argument, "--spanjson", StringComparison.Ordinal)).ToArray();
    BenchmarkRunner.Run<SpanJsonReferenceBenchmarks>(jitConfig, forwarded);
}
else if (args.Contains("--routing", StringComparer.Ordinal))
{
    var jitOnly = args.Contains("--jit-only", StringComparer.Ordinal);
    var aotOnly = args.Contains("--aot-only", StringComparer.Ordinal);
    var forwarded = args.Where(argument =>
        !string.Equals(argument, "--routing", StringComparison.Ordinal) &&
        !string.Equals(argument, "--jit-only", StringComparison.Ordinal) &&
        !string.Equals(argument, "--aot-only", StringComparison.Ordinal)).ToArray();
    var config = aotOnly ? aotConfig : jitOnly ? jitConfig : mainConfig;
    var switcherArguments = forwarded.Length == 0 ? ["--filter", "*"] : forwarded;
    BenchmarkSwitcher.FromTypes([typeof(RoutingBenchmarks), typeof(PipelineBenchmarks)])
        .Run(switcherArguments, config);
}
else
{
    var jitOnly = args.Contains("--jit-only", StringComparer.Ordinal);
    var aotOnly = args.Contains("--aot-only", StringComparer.Ordinal);
    var forwarded = args.Where(argument =>
        !string.Equals(argument, "--jit-only", StringComparison.Ordinal) &&
        !string.Equals(argument, "--aot-only", StringComparison.Ordinal)).ToArray();
    BenchmarkRunner.Run<SerializerBenchmarks>(aotOnly ? aotConfig : jitOnly ? jitConfig : mainConfig, forwarded);
}
