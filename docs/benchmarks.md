# Benchmarks

[English](benchmarks.md) | [日本語](benchmarks.ja.md)

The measurements ran on an Apple M5 CPU with 10 physical cores, macOS Tahoe 26.5.2, .NET SDK 10.0.203, and .NET runtime 10.0.7 arm64. BenchmarkDotNet 0.15.8 used concurrent workstation GC. The host was not otherwise isolated, so consider the error and standard-deviation columns in the BenchmarkDotNet reports when comparing close results.

## NativeAOT sample

`dotnet publish samples/Hello/Hello.csproj -c Release` completed with no IL or AOT warnings. The published executable ran without `dotnet` and passed the Text, JSON, 404, 405, and HEAD requests.

| Metric | Result |
| --- | ---: |
| `Hello` executable size | 7,128,392 bytes (6.80 MiB) |
| Process start through completed `GET /`, 10-run median | 8.443 ms |

The startup samples were 21.598, 9.278, 8.435, 8.452, 8.848, 8.710, 7.641, 8.389, 7.446, and 8.114 ms. Each run started a new NativeAOT process on a new loopback port and stopped it after receiving the complete HTTP response.

## Throughput and memory vs ASP.NET Core

Release JIT servers ran one at a time on HTTP/1.1 loopback. Each endpoint used a fresh server process. The load client used `HttpClient` with `SocketsHttpHandler`, 128 concurrent workers, a 3-second warmup, and a 10-second measurement. The table contains the median of three repetitions. The framework order was reversed on the second repetition.

Both servers returned the same bodies from `GET /`, `GET /users/123`, and `POST /echo`. The ASP.NET Core server used `WebApplication.CreateSlimBuilder`, Minimal API routing, and a source-generated System.Text.Json context with camelCase names. Console logging providers were removed from the measured server. No application services were registered.

| Endpoint | Framework | Throughput (req/s) | p50 | p99 | Peak working set | Allocated/request |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| plaintext | Miya | 51,708 | 1.688 ms | 12.839 ms | 73.0 MiB | 104.0 B |
| plaintext | ASP.NET Core | 72,353 | 1.332 ms | 8.453 ms | 99.0 MiB | 1,016.0 B |
| JSON GET | Miya | 53,574 | 1.689 ms | 13.314 ms | 74.5 MiB | 168.0 B |
| JSON GET | ASP.NET Core | 48,570 | 1.865 ms | 12.043 ms | 98.5 MiB | 384.1 B |
| JSON POST | Miya | 26,382 | 3.751 ms | 16.937 ms | 78.1 MiB | 400.1 B |
| JSON POST | ASP.NET Core | 23,792 | 3.696 ms | 24.295 ms | 100.8 MiB | 648.3 B |

Other CPU-intensive work ran on the host. The three throughput samples ranged from 42,591 to 69,403 req/s for Miya plaintext and 38,113 to 75,125 req/s for ASP.NET Core plaintext. The JSON GET ranges were 40,561 to 56,863 and 36,336 to 60,696 req/s. The JSON POST ranges were 22,533 to 83,724 and 21,543 to 24,854 req/s. These overlapping and wide ranges do not establish a tighter throughput-equivalence claim on this host. Miya's median peak working set was 22.7 to 26.0 MiB lower, and its server allocation per request was 38% to 90% lower in all three cases.

The server reads `GC.GetTotalAllocatedBytes` before and after each measured interval and writes the byte difference and request count to standard error. The client divides that difference by the matching request count. It also reads `Process.PeakWorkingSet64` from the server PID. That property returned zero on this macOS runtime, so the reported value is the higher of it and `WorkingSet64` samples taken every 10 ms while the server was running.

Run the same JIT comparison with:

```sh
./benchmarks/Miya.LoadBench/run.sh \
  --concurrency 128 --warmup 3 --duration 10 --iterations 3
```

## Miya and System.Text.Json

The serializer jobs used one launch, five warmup iterations, twenty measured iterations, and a 250 ms iteration time. Miya used codecs emitted by `Miya.Generators` and resolved them through the codec-free `Json.Serialize` and `Json.Deserialize` overloads. No benchmark-specific codecs were used.

Both serializers wrote to reused `IBufferWriter<byte>` instances. System.Text.Json used source generation, camelCase naming, `UnsafeRelaxedJsonEscaping`, required-member checks, and nullable-annotation checks. Request JSON was prepared before the measured interval, and setup verified that both serializers rejected a missing required property and a null value for its non-nullable property. The buffer-growth case created a 16-byte buffer inside each operation.

Other CPU-intensive processes ran on the host during the combined serializer run. The JIT small DTO, list of 100 DTOs, and request-binding cases were repeated with category filters so each pair ran closer together. The JIT table uses those focused results for the three named cases and the combined run for the other five. The NativeAOT table uses the combined run.

The pass condition requires Miya's mean and allocated bytes to be no greater than System.Text.Json in every scenario under both JIT and NativeAOT. These results passed all sixteen JIT and NativeAOT cases. Allocated bytes were no greater than System.Text.Json in every scenario.

JIT results:

| Scenario | Miya mean | STJ mean | Miya allocated | STJ allocated |
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

| Scenario | Miya mean | STJ mean | Miya allocated | STJ allocated |
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

Miya's JIT mean was lower in four of these seven reference scenarios. SpanJson's mean was lower for the small DTO, nested DTO, and request-binding cases.

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
dotnet build benchmarks/Miya.Benchmarks/Miya.Benchmarks.csproj -c Release
MIYA_BENCHMARK_FINAL=1 dotnet run -c Release --no-build \
  --project benchmarks/Miya.Benchmarks -- --filter '*'
MIYA_BENCHMARK_FINAL=1 dotnet run -c Release --no-build \
  --project benchmarks/Miya.Benchmarks -- \
  --jit-only --filter '*SmallDto*' '*RequestBind*'
MIYA_BENCHMARK_FINAL=1 dotnet run -c Release --no-build \
  --project benchmarks/Miya.Benchmarks -- \
  --jit-only --filter '*List100*'
MIYA_BENCHMARK_FINAL=1 dotnet run -c Release --no-build \
  --project benchmarks/Miya.Benchmarks -- --routing
MIYA_BENCHMARK_FINAL=1 dotnet run -c Release --no-build \
  --project benchmarks/Miya.Benchmarks -- --spanjson
```
