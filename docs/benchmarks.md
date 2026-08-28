# Benchmarks

[English](benchmarks.md) | [日本語](benchmarks.ja.md)

The measurements ran on an Apple M5 CPU with 10 physical cores, macOS Tahoe 26.5.2, .NET SDK 10.0.203, and .NET runtime 10.0.7 arm64. BenchmarkDotNet 0.15.8 used concurrent workstation GC.

## NativeAOT sample

`dotnet publish samples/Hello/Hello.csproj -c Release` completed with no IL or AOT warnings. The published executable ran without `dotnet` and passed the Text, JSON, 404, 405, and HEAD requests.

| Metric | Result |
| --- | ---: |
| `Hello` executable size | 7,128,392 bytes (6.80 MiB) |
| Process start through completed `GET /`, 10-run median | 8.443 ms |

The startup samples were 21.598, 9.278, 8.435, 8.452, 8.848, 8.710, 7.641, 8.389, 7.446, and 8.114 ms. Each run started a new NativeAOT process on a new loopback port and stopped it after receiving the complete HTTP response.

## Memory and throughput vs ASP.NET Core

`benchmarks/Mugi.LoadBench` starts a Mugi server and an equivalent ASP.NET Core Minimal API server one at a time and drives each with an `HttpClient` load client over HTTP/1.1 loopback (128 concurrent workers, a 3-second warmup, and a 10-second measurement). Both servers return the same bodies from `GET /`, `GET /users/123`, and `POST /echo`. The ASP.NET Core server used `WebApplication.CreateSlimBuilder`, Minimal API routing, and a source-generated System.Text.Json context with camelCase names, with console logging removed and no application services registered.

Throughput was within about 1 to 3 percent between the two on every endpoint, matching the shared Kestrel transport. Mugi used a peak working set about 20 MiB lower and allocated less per request. The table is the median of three iterations.

| Endpoint | Framework | Throughput (req/s) | p99 | Peak working set | Allocated/request |
| --- | --- | ---: | ---: | ---: | ---: |
| plaintext | Mugi | 176,258 | 1.971 ms | 76.8 MiB | 104 B |
| plaintext | ASP.NET Core | 174,897 | 1.960 ms | 97.8 MiB | 1,016 B |
| JSON GET | Mugi | 174,233 | 1.945 ms | 78.7 MiB | 168 B |
| JSON GET | ASP.NET Core | 175,505 | 1.967 ms | 98.4 MiB | 384 B |
| JSON POST | Mugi | 171,429 | 1.968 ms | 78.0 MiB | 400 B |
| JSON POST | ASP.NET Core | 166,704 | 2.048 ms | 100.5 MiB | 648 B |

Per-request allocation is the server's `GC.GetTotalAllocatedBytes` difference across the measured interval divided by the request count. The peak working set is the maximum of `WorkingSet64` sampled every 10 ms while the server ran.

Run the same JIT comparison with:

```sh
./benchmarks/Mugi.LoadBench/run.sh \
  --concurrency 128 --warmup 3 --duration 10 --iterations 3
```

## Mugi and System.Text.Json

The serializer jobs used one launch, five warmup iterations, twenty measured iterations, and a 250 ms iteration time. Mugi used codecs emitted by `Mugi.Generators` and resolved them through the codec-free `Json.Serialize` and `Json.Deserialize` overloads. No benchmark-specific codecs were used.

Both serializers wrote to reused `IBufferWriter<byte>` instances. System.Text.Json used source generation, camelCase naming, `UnsafeRelaxedJsonEscaping`, required-member checks, and nullable-annotation checks. Request JSON was prepared before the measured interval, and setup verified that both serializers rejected a missing required property and a null value for its non-nullable property. The buffer-growth case created a 16-byte buffer inside each operation.

The pass condition requires Mugi's mean and allocated bytes to be no greater than System.Text.Json in every scenario under both JIT and NativeAOT. These results passed all sixteen JIT and NativeAOT cases. Allocated bytes were no greater than System.Text.Json in every scenario.

JIT results:

| Scenario | Mugi mean | STJ mean | Mugi allocated | STJ allocated |
| --- | ---: | ---: | ---: | ---: |
| Small DTO | 57.44 ns | 66.05 ns | 0 B | 0 B |
| List of 100 DTOs | 3,199.00 ns | 5,098.00 ns | 0 B | 0 B |
| Nested DTO | 287.98 ns | 417.10 ns | 0 B | 0 B |
| Escape-heavy string | 2,426.97 ns | 2,766.74 ns | 0 B | 0 B |
| 32 KiB string | 5,785.73 ns | 7,026.45 ns | 0 B | 0 B |
| Integer-centric DTO | 2,273.23 ns | 2,936.68 ns | 0 B | 0 B |
| Request binding | 654.98 ns | 1,090.29 ns | 280 B | 872 B |
| Buffer growth | 5,780.44 ns | 16,059.42 ns | 32,880 B | 98,591 B |

NativeAOT results:

| Scenario | Mugi mean | STJ mean | Mugi allocated | STJ allocated |
| --- | ---: | ---: | ---: | ---: |
| Small DTO | 45.97 ns | 58.77 ns | 0 B | 0 B |
| List of 100 DTOs | 7,577.34 ns | 9,414.40 ns | 0 B | 0 B |
| Nested DTO | 219.01 ns | 420.45 ns | 0 B | 0 B |
| Escape-heavy string | 2,372.24 ns | 2,772.62 ns | 0 B | 0 B |
| 32 KiB string | 3,984.00 ns | 5,925.40 ns | 0 B | 0 B |
| Integer-centric DTO | 2,745.76 ns | 4,106.62 ns | 0 B | 0 B |
| Request binding | 635.99 ns | 980.47 ns | 280 B | 872 B |
| Buffer growth | 6,472.62 ns | 19,709.85 ns | 32,880 B | 98,602 B |

SpanJson 4.2.1 was measured separately under JIT because its API returns a new `byte[]` rather than writing to the same `IBufferWriter<byte>` contract. It is a reference rather than the pass/fail baseline.

| Scenario | SpanJson mean | Allocated |
| --- | ---: | ---: |
| Small DTO | 50.71 ns | 64 B |
| List of 100 DTOs | 8,266.02 ns | 4,256 B |
| Nested DTO | 228.06 ns | 168 B |
| Escape-heavy string | 5,593.79 ns | 1,568 B |
| 32 KiB string | 39,181.72 ns | 32,800 B |
| Integer-centric DTO | 6,726.19 ns | 1,032 B |
| Request binding | 194.46 ns | 280 B |

Mugi's JIT mean was lower in four of these seven reference scenarios. SpanJson's mean was lower for the small DTO, nested DTO, and request-binding cases.

## Routing and middleware pipeline

The routing benchmark registers ten routes. Its harness reuses a `Context` and a minimal in-memory HTTP feature collection, resets them for each operation, and invokes the handler returned by `Build()`. It excludes sockets and Kestrel.

| Route result | JIT mean | JIT allocated | NativeAOT mean | NativeAOT allocated |
| --- | ---: | ---: | ---: | ---: |
| Static hit | 261.8 ns | 0 B | 366.8 ns | 0 B |
| `:param` hit | 342.2 ns | 0 B | 374.8 ns | 0 B |
| Wildcard hit | 258.9 ns | 0 B | 345.7 ns | 0 B |
| 404 miss | 291.6 ns | 96 B | 394.6 ns | 96 B |
| 405 method mismatch | 412.2 ns | 320 B | 639.8 ns | 320 B |

The pipeline benchmark uses the same harness and a static route handler.

| Middleware count | JIT mean | JIT allocated | NativeAOT mean | NativeAOT allocated |
| ---: | ---: | ---: | ---: | ---: |
| 0 | 208.3 ns | 0 B | 259.8 ns | 0 B |
| 5 | 330.9 ns | 0 B | 426.8 ns | 0 B |

## Running the benchmarks

```sh
dotnet build benchmarks/Mugi.Benchmarks/Mugi.Benchmarks.csproj -c Release
MUGI_BENCHMARK_FINAL=1 dotnet run -c Release --no-build \
  --project benchmarks/Mugi.Benchmarks -- --filter '*'
MUGI_BENCHMARK_FINAL=1 dotnet run -c Release --no-build \
  --project benchmarks/Mugi.Benchmarks -- \
  --jit-only --filter '*SmallDto*' '*RequestBind*'
MUGI_BENCHMARK_FINAL=1 dotnet run -c Release --no-build \
  --project benchmarks/Mugi.Benchmarks -- \
  --jit-only --filter '*List100*'
MUGI_BENCHMARK_FINAL=1 dotnet run -c Release --no-build \
  --project benchmarks/Mugi.Benchmarks -- --routing
MUGI_BENCHMARK_FINAL=1 dotnet run -c Release --no-build \
  --project benchmarks/Mugi.Benchmarks -- --spanjson
```
