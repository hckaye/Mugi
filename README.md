# Miya

English | [日本語](README.ja.md)

Miya is a fast, simple HTTP framework for .NET. Instead of a large framework stack, it gives you a lean, modern API: write handlers as lambdas, read the request and write the response through one context object, and run on Kestrel without `WebApplication`, the Generic Host, or a dependency injection container.

Miya is built for NativeAOT. At runtime it uses no reflection, no assembly scanning, and no runtime code generation, so a published app starts in a few milliseconds and ships as a single small binary. Routing, JSON, and typed input binders are prepared at compile time by a source generator; you never call the generator yourself, and referencing the package is enough.

## Install

Add the runtime package and the generator package. Add `Miya.Schema` when the app uses typed input. The generator runs during the build and produces routing, JSON, and typed input code.

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <PublishAot>true</PublishAot>
  <InterceptorsNamespaces>$(InterceptorsNamespaces);Miya.Generated</InterceptorsNamespaces>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="Miya" Version="0.1.0" />
  <PackageReference Include="Miya.Schema" Version="0.1.0" />
  <PackageReference Include="Miya.Generators" Version="0.1.0" />
</ItemGroup>
```

The `InterceptorsNamespaces` line is required. It lets the generator replace the calls it recognizes with faster direct calls. The `Miya.Generators` package carries the generator as an analyzer and a `buildTransitive` props file that sets this up automatically, including when the package arrives through another project reference.

`Miya.Schema` is a separate package. Keep it only when the application uses typed input and validation.

When you reference the projects directly in a repository, pass the generator to the compiler as an analyzer:

```xml
<ItemGroup>
  <ProjectReference Include="../Miya/src/Miya/Miya.csproj" />
  <ProjectReference Include="../Miya/src/Miya.Schema/Miya.Schema.csproj" />
  <ProjectReference Include="../Miya/src/Miya.Generators/Miya.Generators.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />
</ItemGroup>
```

## Quick start

```csharp
using Miya;

var app = new App();

app.Get("/", static c => c.Text("Hello"));
app.Get("/users/:id", static c => c.Json(new User(c.Param("id"), "Ada")));

app.Run();

public sealed record User(string Id, string Name);
```

Run it and send a request:

```sh
dotnet run
curl -i http://127.0.0.1:3000/users/42
```

`GET /users/42` returns `{"id":"42","name":"Ada"}`. The default port is 3000; `PORT=8080 dotnet run` changes the listener without touching the code.

## The context object

Every handler receives one context, named `c` here. It reads the incoming request and builds the response.

Read the request:

| Call | Returns |
| --- | --- |
| `c.Req.Method`, `c.Req.Path` | the HTTP method and path |
| `c.Param("id")` | a route parameter, such as the `:id` segment |
| `c.Query("q")` | a query-string value, or null |
| `c.Req.Header("X-User")` | a request header, or null |
| `await c.Req.Text()` | the request body as text |
| `await c.Req.Json<T>()` | the request body parsed into `T` |

Write the response:

| Call | Effect |
| --- | --- |
| `c.Text(string)` | writes a `text/plain` body |
| `c.Json(value)` | writes `value` as JSON |
| `c.Html(string)` | writes a `text/html` body |
| `c.Bytes(data, contentType)` | writes raw bytes |
| `c.Stream(contentType, write)` | streams a response through a callback |
| `c.Status(code)` | sets the status code |
| `c.Header(name, value)` | sets a response header; `c.AppendHeader` adds another value |
| `c.Redirect(location)` | sends a redirect, 302 by default |
| `c.NotFound()` | sends a 404 |

`c.Aborted` is a `CancellationToken` that fires when the client disconnects. The response stays buffered until your handler and middleware finish, so you can still change the status or headers after writing a body, as long as the response has not started streaming.

## Routing

Register a handler for an HTTP method and a path pattern. A pattern is made of static segments, `:name` for a single segment, and `*name` for the rest of the path (only as the last segment).

```csharp
app.Get("/users", ListUsers);
app.Get("/users/:id", GetUser);      // c.Param("id")
app.Get("/files/*path", GetFile);    // c.Param("path") captures the remaining path
app.Post("/users", CreateUser);
```

`Get`, `Post`, `Put`, `Delete`, `Patch`, `Head`, `Options`, `All`, and `On(method, ...)` register handlers. `app.Route(prefix, subApp)` mounts another `App` under a path prefix.

When two patterns can match the same path, the more specific one wins: at each segment a static text beats `:name`, and `:name` beats `*name`. Patterns of equal specificity are tried in registration order.

Method and path handling follows HTTP:

- A known path with the wrong method returns 405 with an `Allow` header.
- A `GET` route also answers `HEAD` for the same path, with the same headers and `Content-Length` but no body.
- `OPTIONS` returns 204 with an `Allow` header when no explicit `OPTIONS` route handles the path.
- Any unmatched path returns 404. Register your own with `app.NotFound(handler)`.

Matching uses the path Kestrel already decoded, compared by ordinal. An encoded slash (`%2F`) stays encoded during matching, so `/items/a%2Fb` matches `/items/:id`; `c.Param("id")` then decodes it to `a/b`. An invalid percent escape returns 400. `/users` and `/users/` are different routes, and v0 does not redirect between them.

## Typed input and validation

`Miya.Schema` combines route parameters, query values, headers, and JSON body fields in one input record. The handler runs only after parsing and validation succeed.

```csharp
using Miya.Schema;

var searchSchema = Schemas.For<SearchInput>()
    .Query(input => input.Limit, rules => rules.Default(20).Range(1, 100));

app.Get("/search/:Page", searchSchema,
    static (c, input) => c.Json(input));

var personSchema = Schemas.For<CreatePersonInput>()
    .Body(input => input.Name, rules => rules.NotEmpty().MaxLength(80))
    .Body(input => input.Age, rules => rules.Range(0, 120))
    .Body(input => input.Note, rules => rules.Optional());

app.Post("/people", personSchema,
    static (c, input) => c.Json(input));

public sealed record SearchInput(int Page, string Query, int Limit);
public sealed record CreatePersonInput(string Name, int Age, string? Note);
```

An explicit `Route`, `Query`, `Body`, or `Header` mapping takes precedence. An unmapped field whose name exactly matches a `:parameter` name comes from the route. Other unmapped fields come from the JSON body for `POST`, `PUT`, and `PATCH`, and from the query string for other methods. Name matching is ordinal and case-sensitive. `Header` also takes the HTTP header name, for example `.Header(input => input.RequestId, "X-Request-Id")`.

Text values support primitives, `string`, `Guid`, Boolean values, enum names or numbers, `DateTime`, and `DateTimeOffset`. Body fields use Miya's generated JSON codecs. The generator reads the field selectors and rule declarations at build time; the runtime does not invoke those selectors or compile expression trees.

Rules can be chained. Numeric fields support `Min`, `Max`, `Range`, `Positive`, and `NonNegative`. Strings support `NotEmpty`, `Length`, `MinLength`, `MaxLength`, and `Pattern`. Every field supports `Optional`, `Default`, and `Must`.

A missing required value, parse failure, invalid JSON body, or failed rule returns 400 without calling the handler. The response has `Content-Type: application/json` and this shape:

```json
{
  "errors": [
    { "field": "age", "message": "must be between 0 and 120" }
  ]
}
```

## Middleware

`app.Use` wraps every request. Middleware runs in registration order before the route and unwinds in reverse order after `next` returns, so you can act on both the request and the finished response. Calling `next` more than once is rejected.

```csharp
app.Use(static async (c, next) =>
{
    var stopwatch = Stopwatch.StartNew();
    await next(c);
    c.Header("Server-Timing", $"app;dur={stopwatch.Elapsed.TotalMilliseconds}");
});
```

`app.Use("/admin", middleware)` limits a middleware to paths under a prefix. `app.OnError(handler)` handles exceptions thrown while processing a request.

## Returning and reading JSON

Miya reads and writes JSON with its own serializer. In the common case you configure nothing: return an object and Miya writes it as JSON.

```csharp
app.Get("/users/:id", c => c.Json(new User(c.Param("id"), "Ada")));

app.Post("/users", async c =>
{
    var user = await c.Req.Json<User>();   // parse the request body
    await c.Json(user);                    // write it back as JSON
});

public sealed record User(string Id, string Name);
```

At build time the generator reads each `c.Json(...)` and `c.Req.Json<T>()` call, collects the types you serialize, and generates the code that reads and writes them. Nothing is discovered at runtime, which is why this works under NativeAOT. Property names are `camelCase` by default; set `<MiyaJsonNaming>PascalCase</MiyaJsonNaming>` to keep the C# casing.

### Supported types

Generated serialization covers Boolean and numeric primitives, `char`, `string`, `Guid`, `DateTime`, `DateTimeOffset`, `decimal`, numeric enums, nullable values, one-dimensional arrays, `List<T>`, and `Dictionary<string, T>`. Your own `public` or `internal` classes, records, and structs may combine those types, including recursively. A record needs a primary constructor; a plain class needs a parameterless constructor and properties with accessible `get` and `set`/`init`.

Interfaces, `object`, polymorphic types, class inheritance, anonymous types, private members, ref-like types, open generic types, multidimensional arrays, and dictionaries with non-string keys are not supported. Using one produces a compile-time error that names the type.

### Types reached only through generics

The generator finds types at the call sites it can read. If a type is serialized only through generic code, no call site names it directly, so the generator cannot see it. Mark such a type once with `Json.Include<T>()`:

```csharp
Json.Include<User>();
```

### Writing a codec by hand

A codec is the small class that reads and writes one type as JSON. The generator writes one codec per supported type for you. When you need a type the generator does not support, or a specific JSON shape, write a codec by implementing `IJsonCodec<T>` and register it with `Json.Register`. A registered codec is used everywhere that type is serialized, including direct `c.Json` calls.

```csharp
using Miya.Json;

Json.Register(UserCodec.Instance);

internal sealed record User(int Id, string Name);

internal sealed class UserCodec : IJsonCodec<User>
{
    public static UserCodec Instance { get; } = new();

    public void Write(ref JsonWriter writer, User? value)
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

    public User? Read(ref JsonReader reader)
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
                    ?? throw new JsonException("The name cannot be null.");
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

### Limits for untrusted input

The serializer enforces limits so that malformed or hostile JSON cannot exhaust memory or the stack. The defaults are safe for input from the network and are set on `AppOptions` and `JsonOptions`.

| Setting | Default |
| --- | ---: |
| JSON request body, `AppOptions.MaxJsonBodyBytes` | 1 MiB |
| Complete JSON document, `MaxDocumentByteLength` | 1 MiB |
| Object and array depth, `MaxDepth` | 64 |
| One string token, `MaxStringByteLength` | 1 MiB |
| Members in one object or elements in one array, `MaxCollectionSize` | 1,048,576 |
| Digits in one number, `MaxNumberDigits` | 128 |
| Retained JSON temporary buffer, `MaxPooledBufferByteLength` | 64 KiB |
| Buffered response, `AppOptions.MaxBufferedResponseBytes` | 1 MiB |
| Request body, `AppOptions.MaxRequestBodyBytes` | 30 MiB |

NaN and Infinity are rejected by default. `JsonOptions` also carries a cancellation token for long serialization and parsing.

As a build-time optimization, Miya replaces the `c.Json` and route calls it recognizes with direct calls into the generated code, using a C# feature called interceptors. This changes nothing you observe: serialization and routing behave the same whether or not a call was replaced, and a call that the generator cannot see still works as long as a codec is registered.

## Generating source without the compiler generator

Some build setups cannot run compiler-integrated source generators. `miya-gen` produces the same JSON and routing code as ordinary `.cs` files that you generate as a build step. It does not emit the interceptor optimization, so only the direct-call speedup is absent; behavior is the same.

```sh
dotnet tool install --global Miya.Gen --version 0.1.0
dotnet build MyApp.csproj
miya-gen --project MyApp.csproj --output Generated
dotnet build MyApp.csproj
```

The SDK compiles `Generated/*.cs` automatically when the directory is under the project root; a directory elsewhere must be added with a `Compile` item. The project must compile before generation, and existing `Miya.*.g.cs` files in the output directory are replaced. From this repository the equivalent command is:

```sh
dotnet run --project src/Miya.Gen -- \
  --project samples/Hello/Hello.csproj \
  --output samples/Hello/Generated
```

## Typed contexts

By default a handler's context carries only request and response data. To pass your own values from middleware to a handler with full type safety, derive from `Context` and use `App<TContext>`. There are no string keys and no casts.

```csharp
using Miya;

var app = new App<MyContext>();

app.Use(static async (c, next) =>
{
    c.CurrentUser = new User(c.Req.Header("X-User") ?? "anonymous");
    await next(c);
});

app.Get("/me", static c => c.Json(c.CurrentUser));

public sealed class MyContext : Context
{
    public User? CurrentUser { get; set; }
}

public sealed record User(string Id);
```

A derived context is created fresh for each request. If you want it pooled and reused, implement `IPoolableContext` and clear your own fields in `OnReturn()`.

## Hosting

`Run(int? port = null)` starts a loopback HTTP/1.1 listener and blocks until cancellation or a termination signal. `Run()` leaves the port unspecified so the `PORT` environment variable applies; `Run(8080)` chooses the port explicitly. `RunAsync(options, ct)` and `StartAsync(options, ct)` host asynchronously; `StartAsync` returns a `Server` with the bound addresses and a `StopAsync` method. Port 0 asks the operating system for a free port.

Port selection uses the explicit `Run(port)` value first, then `AppOptions.Port`, then a valid integer in `PORT`, then 3000. A value outside 0 through 65535 supplied explicitly or through options is rejected; an invalid `PORT` value is ignored.

SIGINT, SIGTERM, and cancellation stop accepting new requests and wait for the ones in flight, with a 30 second shutdown timeout by default. A second signal ends the process immediately.

### HTTP/2 and HTTP/3

Without a certificate the default is HTTP/1.1. Select `Protocols.Http2` for cleartext HTTP/2:

```csharp
await app.RunAsync(new AppOptions
{
    Protocols = Protocols.Http2,
});
```

A cleartext listener cannot serve HTTP/1.1 and HTTP/2 at once, because it has no ALPN negotiation, and Miya rejects that combination at startup.

Pass an `X509Certificate2` to terminate TLS inside Miya. With a certificate the default is HTTP/1.1 and HTTP/2, chosen per connection through ALPN:

```csharp
using System.Security.Cryptography.X509Certificates;

using var certificate = X509CertificateLoader.LoadPkcs12FromFile("server.pfx", "certificate-password");

await app.RunAsync(new AppOptions
{
    Certificate = certificate,
});
```

HTTP/3 is opt-in and needs a certificate. Add the `Http3` flag while keeping HTTP/1.1 and HTTP/2 so clients can discover HTTP/3 from Kestrel's `Alt-Svc` response header:

```csharp
await app.RunAsync(new AppOptions
{
    Certificate = certificate,
    Protocols = Protocols.Http1AndHttp2AndHttp3,
});
```

Startup throws `PlatformNotSupportedException` when HTTP/3 is requested and `QuicListener.IsSupported` is false. On the macOS arm64 system used for the measurements below it returned false, so the HTTP/3 integration test was skipped there.

### Advanced Kestrel settings

`ConfigureKestrel` reaches other supported Kestrel settings. Certificate selection stays in `AppOptions.Certificate`; Miya does not search for a development certificate or read Kestrel endpoint configuration files.

`AppOptions.ConfigureServices` registers extra services in the internal Kestrel host. Miya never requires dependency injection; this hook exists only for advanced Kestrel customization. Setting it uses the service-backed hosting path even for cleartext endpoints, and the registered services stay inside the server rather than reaching handlers or middleware.

## Measured results

Measurements were taken on 2026-08-27 with an Apple M5 CPU, 10 physical cores, macOS Tahoe 26.5.2, .NET SDK 10.0.203, and .NET runtime 10.0.7 arm64. BenchmarkDotNet 0.15.8 used concurrent workstation GC, one launch, five warmup iterations, and ten measured iterations. The host was not otherwise isolated, so consider the error and standard-deviation columns in the BenchmarkDotNet reports when comparing close results.

### NativeAOT sample

`dotnet publish samples/Hello/Hello.csproj -c Release` completed with no IL or AOT warnings. The published executable ran without `dotnet` and passed the Text, JSON, 404, 405, and HEAD requests.

| Metric | Result |
| --- | ---: |
| `Hello` executable size | 7,128,392 bytes (6.80 MiB) |
| Process start through completed `GET /`, 10-run median | 8.443 ms |

The startup samples were 21.598, 9.278, 8.435, 8.452, 8.848, 8.710, 7.641, 8.389, 7.446, and 8.114 ms. Each run started a new NativeAOT process on a new loopback port and stopped it after receiving the complete HTTP response.

### Miya and System.Text.Json

The serializer measurements were repeated on 2026-08-28 using the Apple M5, macOS arm64, and .NET 10 environment listed above. The serializer jobs used one launch, five warmup iterations, twenty measured iterations, and a 250 ms iteration time. Miya used codecs emitted by `Miya.Generators` and resolved them through the codec-free `Json.Serialize` and `Json.Deserialize` overloads. No benchmark-specific codecs were used.

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

Miya v0 does not support WebSocket upgrades, serve static files, generate OpenAPI documents, or provide authentication, templates, development-certificate discovery, or configuration-file integration. HTTP/3 depends on `QuicListener.IsSupported` and a supplied certificate. A reverse proxy remains an option for TLS termination.

The route generator validates and parses literal patterns at compile time and embeds the parsed templates; the runtime matcher does the matching. It does not yet emit route-specific matching code or a combined trie.

Diagnostics MIYA001 through MIYA004 cover JSON and route generation. MIYA006 checks literal `c.Param` calls against their handler's route. MIYA010 through MIYA015 cover typed-input route mappings, supported field types, schema declarations, rules, and conflicting binding shapes. The planned MIYA005 diagnostic for fields left uncleared by a pooled derived context is not implemented, so clearing them in `IPoolableContext.OnReturn()` remains the caller's responsibility.

## License

Miya is licensed under the MIT License. See [LICENSE](LICENSE).

## Third-party notices

Third-party acknowledgements are recorded in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
