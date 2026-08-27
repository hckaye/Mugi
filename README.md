# Miya

English | [日本語](README.ja.md)

Miya is a small HTTP framework for .NET 10 designed around NativeAOT. Applications do not require `WebApplication`, the Generic Host, or a DI container. Miya constructs Kestrel directly for cleartext HTTP/1.1 and HTTP/2, and uses Kestrel's built-in service registration internally for TLS and HTTP/3. The runtime performs no reflection, assembly scanning, or runtime code generation. Handlers are lambdas rather than attributed controller methods.

Route templates and JSON codecs are generated at compile time. The routing generator validates literal patterns and embeds parsed templates; v0 still uses the shared runtime matcher. Generated JSON codecs register themselves through module initializers. The request API follows a context model with `Text`, `Json`, `Param`, and `Query` methods. Middleware is composed in onion order around the selected route.

## Requirements and project setup

Miya targets `net10.0`. The measurements below used .NET SDK 10.0.203.

### NuGet package references

An application using locally packed or published packages needs the runtime and generator packages:

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <PublishAot>true</PublishAot>
  <InterceptorsNamespaces>$(InterceptorsNamespaces);Miya.Generated</InterceptorsNamespaces>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="Miya" Version="1.0.0" />
  <PackageReference Include="Miya.Generators" Version="1.0.0" />
</ItemGroup>
```

The `Miya.Generators` package contains the analyzer assembly and a `buildTransitive` props file. The props file adds the analyzer and `Miya.Generated` interceptor namespace, including when the package arrives through a project reference. The explicit `InterceptorsNamespaces` property above also works when switching between package and source references.

### Project references

Repository projects expose `Miya.Generators` to the compiler as an analyzer:

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <PublishAot>true</PublishAot>
  <InterceptorsNamespaces>$(InterceptorsNamespaces);Miya.Generated</InterceptorsNamespaces>
</PropertyGroup>

<ItemGroup>
  <ProjectReference Include="../Miya/src/Miya/Miya.csproj" />
  <ProjectReference Include="../Miya/src/Miya.Generators/Miya.Generators.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />
</ItemGroup>
```

`MiyaJsonNaming` selects generated property names. Its default is `camelCase`; set `<MiyaJsonNaming>PascalCase</MiyaJsonNaming>` to preserve C# property casing.

## Quick start

```csharp
using System.Diagnostics;
using System.Globalization;
using Miya;

var app = new App();

app.Use(static async (context, next) =>
{
    var stopwatch = Stopwatch.StartNew();
    await next(context);
    context.Header(
        "Server-Timing",
        $"app;dur={stopwatch.Elapsed.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)}");
});

app.Get("/", static context => context.Text("Hello"));
app.Get("/users/:id", static context => context.Json(new User(context.Param("id"))));

app.Run();

internal sealed record User(string Id);
```

Run the application on the default port:

```sh
dotnet run
curl -i http://127.0.0.1:3000/users/42
```

`PORT=8080 dotnet run` changes the listener without changing the program.

## Typed contexts

`App<TContext>` carries application-specific request state without string keys or casts. Derived contexts are created per request unless they implement `IPoolableContext`. A pooled derived context must clear its own fields in `OnReturn()`.

```csharp
using Miya;

var app = new App<MyContext>();

app.Use(static async (context, next) =>
{
    context.CurrentUser = new User(context.Req.Header("X-User") ?? "anonymous");
    await next(context);
});

app.Get("/me", static context => context.Json(context.CurrentUser));

public sealed class MyContext : Context
{
    public User? CurrentUser { get; set; }
}

public sealed record User(string Id);
```

Middleware runs in registration order before the route and unwinds in reverse order after `next(context)` completes. Calling `next` more than once is rejected.

## Routing behavior

Route patterns contain static segments, `:name` for one segment, and `*name` for the remaining path. A wildcard must be the final segment. At each segment, static text has priority over a parameter, and a parameter has priority over a wildcard. Routes with equal priority use registration order.

`Get`, `Post`, `Put`, `Delete`, `Patch`, `Head`, `Options`, `All`, and `On` register handlers. `Route(prefix, subApp)` mounts another application and normalizes the join between the prefix and child route.

A matching path with the wrong method returns 405 and an `Allow` header. A GET route also handles HEAD when no explicit HEAD route exists, preserving the GET headers and `Content-Length` while suppressing the body. OPTIONS returns 204 with `Allow` when the path exists and no explicit OPTIONS route handles it. An unmatched path returns 404.

Matching uses Kestrel's decoded `Path` with ordinal comparisons and no Unicode normalization. Kestrel leaves an encoded slash such as `%2F` in the path. `Param()` decodes it after matching, so `/items/a%2Fb` matches `/items/:id` and returns `a/b` for `id`. Invalid percent escapes return 400. `/users` and `/users/` are distinct routes, and v0 does not redirect between them.

Literal patterns are parsed and validated by the generator. A route built from a dynamic string is parsed once when it is registered and has the same matching behavior.

## MiyaJson codecs

The MiyaJson contract is a generated or hand-written `IMiyaJsonCodec<T>`. Generated codecs are registered in generic static storage by a module initializer. `context.Json(value)`, `context.Req.Json<T>()`, and the `MiyaJson` entry points use that registry. No assembly scan is involved.

Compiler interceptors replace known call sites with direct generated calls as an optimization. Serialization still works when a call is not intercepted, including generic helpers and calls compiled in another assembly, as long as a codec has been registered. `MiyaJson.Include<T>()` requests generation when a concrete type appears only through generic code:

```csharp
MiyaJson.Include<User>();
```

If no codec is registered, MiyaJson reports how to add the generator, use `miya-gen`, call `Include<T>()`, or register a codec by hand.

### Hand-written codec registration

```csharp
using Miya.Json;

MiyaJson.Register(UserCodec.Instance);

internal sealed record User(int Id, string Name);

internal sealed class UserCodec : IMiyaJsonCodec<User>
{
    public static UserCodec Instance { get; } = new();

    public void Write(ref MiyaJsonWriter writer, User? value)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteRaw("{\"id\":"u8);
        writer.WriteNumber(value.Id);
        writer.WriteRaw(",\"name\":"u8);
        writer.WriteString(value.Name);
        writer.WriteRaw("}"u8);
    }

    public User? Read(ref MiyaJsonReader reader)
    {
        if (reader.TryReadNull())
        {
            return null;
        }

        var id = 0;
        var name = string.Empty;
        reader.ReadBeginObject();
        while (!reader.TryReadEndObject())
        {
            var property = reader.ReadPropertyName();
            if (property.SequenceEqual("id"u8))
            {
                id = reader.ReadInt32();
            }
            else if (property.SequenceEqual("name"u8))
            {
                name = reader.ReadString()
                    ?? throw new MiyaJsonException("The name cannot be null.");
            }
            else
            {
                reader.SkipValue();
            }
        }

        return new User(id, name);
    }
}
```

The generated type model supports Boolean and numeric primitives, `char`, `string`, `Guid`, `DateTime`, `DateTimeOffset`, `decimal`, numeric enums, nullable values, one-dimensional arrays, `List<T>`, and `Dictionary<string, T>`. Public or internal classes, records, and structs may compose those types recursively. Records require a primary constructor. POCO classes require a public or internal parameterless constructor, and serialized properties require accessible get and set/init accessors.

Interfaces, `object`, polymorphic contracts, class inheritance, anonymous types, private members, ref-like types, open generic types, multidimensional arrays, and dictionaries with non-string keys are not supported by generated codecs.

### Default limits

| Setting | Default |
| --- | ---: |
| JSON request body, `MiyaOptions.MaxJsonBodyBytes` | 1 MiB |
| Complete JSON document, `MaxDocumentByteLength` | 1 MiB |
| Object and array depth, `MaxDepth` | 64 |
| One string token, `MaxStringByteLength` | 1 MiB |
| Members in one object or elements in one array, `MaxCollectionSize` | 1,048,576 |
| Digits in one number, `MaxNumberDigits` | 128 |
| Retained MiyaJson temporary buffer, `MaxPooledBufferByteLength` | 64 KiB |
| Buffered response, `MiyaOptions.MaxBufferedResponseBytes` | 1 MiB |
| Request body, `MiyaOptions.MaxRequestBodyBytes` | 30 MiB |

NaN and Infinity are rejected by default. `MiyaJsonOptions` also carries a cancellation token for long serialization and parsing operations.

## Generating source with miya-gen

`miya-gen` uses the same generation core when compiler-integrated source generators are unavailable. It writes JSON codecs, module initializer registration, and parsed route templates as ordinary `.cs` files. It does not emit interceptors, so only the direct-call optimization is absent.

Install or update the tool from a package feed, then generate into a directory included by the project:

```sh
dotnet tool install --global Miya.Gen --version 1.0.0
dotnet build MyApp.csproj
miya-gen --project MyApp.csproj --output Generated
dotnet build MyApp.csproj
```

The SDK includes `Generated/*.cs` automatically when the directory is under the project root. A directory outside the project must be added with a `Compile` item. From this repository, the equivalent generator command is:

```sh
dotnet run --project src/Miya.Gen -- \
  --project samples/Hello/Hello.csproj \
  --output samples/Hello/Generated
```

The project must compile before generation. Existing `Miya.*.g.cs` files in the selected output directory are replaced.

## Kestrel hosting

`Run(int? port = null)` starts a loopback HTTP/1.1 listener and blocks until cancellation or a termination signal. `Run()` leaves the port unspecified, allowing the `PORT` environment variable to take effect. `Run(8080)` is an explicit selection. `MiyaOptions.Protocols` and `MiyaOptions.Certificate` configure other protocols through `RunAsync` or `StartAsync`.

`RunAsync(MiyaOptions?, CancellationToken)` and `StartAsync(MiyaOptions?, CancellationToken)` provide asynchronous hosting. `StartAsync` returns a `MiyaServer` with the bound addresses and `StopAsync`. Port 0 requests an operating-system-assigned port.

Port selection uses an explicit `Run(port)` value first, then `MiyaOptions.Port`, then a valid integer in `PORT`, then 3000. Values outside 0 through 65535 are rejected when supplied explicitly or through options. An invalid `PORT` value is ignored and falls back to 3000.

SIGINT, SIGTERM, and cancellation stop new requests and wait for active requests. The default shutdown timeout is 30 seconds. A second signal terminates the process immediately. Response bodies remain buffered until middleware returns unless they exceed the 1 MiB default or `Stream` is used; headers can be changed after `next` only while the response remains buffered.

Without a certificate, the default protocol is HTTP/1.1. Select `MiyaProtocols.Http2` for cleartext HTTP/2 prior knowledge:

```csharp
await app.RunAsync(new MiyaOptions
{
    Protocols = MiyaProtocols.Http2,
});
```

A cleartext listener cannot serve HTTP/1.1 and HTTP/2 together because it has no ALPN negotiation. Miya rejects that combination at startup.

Pass an `X509Certificate2` to terminate TLS in Miya. The default with a certificate is HTTP/1.1 and HTTP/2, selected through ALPN:

```csharp
using System.Security.Cryptography.X509Certificates;

using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
    "server.pfx",
    "certificate-password");

await app.RunAsync(new MiyaOptions
{
    Certificate = certificate,
});
```

HTTP/3 is opt-in and requires a certificate. Add the `Http3` flag while retaining HTTP/1.1 and HTTP/2 so clients can discover HTTP/3 through Kestrel's `Alt-Svc` response header:

```csharp
await app.RunAsync(new MiyaOptions
{
    Certificate = certificate,
    Protocols = MiyaProtocols.Http1AndHttp2AndHttp3,
});
```

Startup throws `PlatformNotSupportedException` when HTTP/3 is requested and `QuicListener.IsSupported` is false. On the macOS arm64 system used for the measurements below, `QuicListener.IsSupported` returned false. The HTTP/3 integration test was skipped on that condition. Kestrel enables `Alt-Svc` automatically when HTTP/1.1 or HTTP/2 shares an endpoint with HTTP/3.

`ConfigureKestrel` remains available for other supported Kestrel settings. Certificate selection belongs in `MiyaOptions.Certificate`; Miya does not search for a development certificate or load Kestrel endpoint configuration files.

`MiyaOptions.ConfigureServices` registers additional services in the internal Kestrel host. Miya never requires dependency injection; this hook exists for advanced Kestrel customization. Setting it selects the service-backed hosting path even for cleartext endpoints, and the registered services stay inside the server rather than reaching handlers or middleware.

## Measured results

Measurements were taken on 2026-08-27 with an Apple M5 CPU, 10 physical cores, macOS Tahoe 26.5.2, .NET SDK 10.0.203, and .NET runtime 10.0.7 arm64. BenchmarkDotNet 0.15.8 used concurrent workstation GC, one launch, five warmup iterations, and ten measured iterations. The host was not otherwise isolated, so the error and standard-deviation columns in the generated BenchmarkDotNet reports should be considered when comparing close results.

### NativeAOT sample

`dotnet publish samples/Hello/Hello.csproj -c Release` completed with no IL or AOT warnings. The published executable ran without `dotnet` and passed the Text, JSON, 404, 405, and HEAD requests.

| Metric | Result |
| --- | ---: |
| `Hello` executable size | 7,128,392 bytes (6.80 MiB) |
| Process start through completed `GET /`, 10-run median | 8.443 ms |

The startup samples were 21.598, 9.278, 8.435, 8.452, 8.848, 8.710, 7.641, 8.389, 7.446, and 8.114 ms. Each run started a new NativeAOT process on a new loopback port and stopped it after receiving the complete HTTP response.

### MiyaJson and System.Text.Json

The serializer measurements were repeated on 2026-08-28 using the Apple M5, macOS arm64, and .NET 10 environment listed above. The serializer jobs used one launch, five warmup iterations, twenty measured iterations, and a 250 ms iteration time. Miya used codecs emitted by `Miya.Generators` and resolved them through the codec-free `MiyaJson.Serialize` and `MiyaJson.Deserialize` overloads. No benchmark-specific codecs were used.

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

### Routing and middleware pipeline

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

The benchmark commands were:

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

## v0 limitations

Miya v0 does not support WebSocket upgrades, serve static files, generate OpenAPI documents, or provide authentication, validation, templates, development-certificate discovery, or configuration-file integration. HTTP/3 depends on `QuicListener.IsSupported` and a supplied certificate. A reverse proxy remains optional for TLS termination.

The route generator does not emit route-specific matching code or a combined trie in v0. It validates and parses literal patterns at compile time, then embeds the parsed templates for the runtime matcher.

Diagnostics MIYA001 through MIYA004 cover anonymous JSON types, invalid routes, limited duplicate-route detection, and unsupported JSON types. The planned MIYA005 diagnostic for fields left uncleared by pooled derived contexts is not implemented. `IPoolableContext.OnReturn()` remains the caller's responsibility.

## Third-party notices

Third-party acknowledgements are recorded in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
