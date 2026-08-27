using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Miya.Benchmarks;

var shortRun = string.Equals(
    Environment.GetEnvironmentVariable("MIYA_BENCHMARK_SHORT"),
    "1",
    StringComparison.Ordinal);
var baseJob = shortRun ? Job.ShortRun : Job.Default;
var jitConfig = ManualConfig.Create(DefaultConfig.Instance)
    .AddJob(baseJob.WithRuntime(CoreRuntime.Core10_0).WithId(shortRun ? "JIT-Short" : "JIT"));
var aotConfig = ManualConfig.Create(DefaultConfig.Instance)
    .AddJob(baseJob.WithRuntime(NativeAotRuntime.Net10_0).WithId(shortRun ? "NativeAOT-Short" : "NativeAOT"));
var mainConfig = ManualConfig.Create(jitConfig)
    .AddJob(baseJob.WithRuntime(NativeAotRuntime.Net10_0).WithId(shortRun ? "NativeAOT-Short" : "NativeAOT"));

if (args.Contains("--spanjson", StringComparer.Ordinal))
{
    var forwarded = args.Where(argument => !string.Equals(argument, "--spanjson", StringComparison.Ordinal)).ToArray();
    BenchmarkRunner.Run<SpanJsonReferenceBenchmarks>(jitConfig, forwarded);
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
