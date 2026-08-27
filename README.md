# Miya

English | [日本語](README.ja.md)

Miya is a fast, simple web application framework for .NET. Instead of a large framework stack, it gives you a lean, modern API: write handlers as lambdas, route requests, run middleware, bind and validate typed input, and read the request and write the response through one context object. It runs on Kestrel without `WebApplication`, the Generic Host, or a dependency injection container.

Miya is built for NativeAOT. At runtime it uses no reflection, no assembly scanning, and no runtime code generation, so a published app starts in a few milliseconds and ships as a single small binary. Routing, JSON, and typed input binders are prepared at compile time by a source generator; you never call the generator yourself, and referencing the package is enough.

## Install

Miya's packages target `net9.0` and run on .NET 9 or later. Building an app needs the .NET 9 SDK or newer, because the generator uses stable C# interceptors that shipped in that release.

Add the runtime package and the generator package. Add `Miya.Schema` when the app uses typed input. The generator runs during the build and produces routing, JSON, and typed input code.

```xml
<PropertyGroup>
  <TargetFramework>net9.0</TargetFramework>
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

## Zero-generation runtime (Miya.Reflection)

Routing and text responses already work without generated source. When neither the source generator nor `miya-gen` is available, add the opt-in `Miya.Reflection` package to create JSON codecs from public properties and constructors at runtime.

```xml
<PackageReference Include="Miya.Reflection" Version="0.1.0" />
```

Enable the fallback once during startup:

```csharp
using Miya.Reflection;

ReflectionCodecs.Enable();
```

The fallback is disabled by default. It supports the same primitive values, arrays, `List<T>`, `Dictionary<string, T>`, nullable values, enums, POCOs, and records with camel-case property names. `Miya.Reflection` does not support NativeAOT; use generated codecs when publishing with AOT.
## OpenAPI

`miya-gen openapi` reads the routes in a compiled project and writes an OpenAPI 3.1 document:

```sh
miya-gen openapi --project MyApp.csproj --output openapi.json
```

Route parameters are emitted as required path parameters. A route that uses `Miya.Schema` also includes the source, type, default, and supported validation constraints for its path, query, header, and JSON body fields. Referenced JSON DTOs are placed under `components/schemas`.

Response detection is best effort and examines the handler lambda at the registration site. A `c.Json(value)` call produces an `application/json` response schema, and `c.Text(value)` produces `text/plain`. When neither call can be identified, the operation has a 200 response without declared content. Typed routes also declare the validation-error 400 response.

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

## Performance

Miya is built to be fast and allocation-light. In the measured scenarios:

- The generated JSON serialization matches or beats System.Text.Json source generation on both mean time and allocated bytes, under JIT and NativeAOT.
- Routing and the middleware pipeline allocate nothing on the synchronous hot path (a 404 miss and a 405 mismatch allocate only their small response state).
- The `samples/Hello` NativeAOT binary is about 6.8 MiB and answers its first request within a few milliseconds of process start.

Numbers, scenarios, the measurement environment, and how to reproduce them are in [docs/benchmarks.md](docs/benchmarks.md).

## v0 limitations

Miya v0 does not support WebSocket upgrades, serve static files, or provide authentication, templates, development-certificate discovery, or configuration-file integration. HTTP/3 depends on `QuicListener.IsSupported` and a supplied certificate. A reverse proxy remains an option for TLS termination.

The route generator validates and parses literal patterns at compile time and embeds the parsed templates; the runtime matcher does the matching. It does not yet emit route-specific matching code or a combined trie.

Diagnostics MIYA001 through MIYA004 cover JSON and route generation. MIYA006 checks literal `c.Param` calls against their handler's route. MIYA010 through MIYA015 cover typed-input route mappings, supported field types, schema declarations, rules, and conflicting binding shapes. The planned MIYA005 diagnostic for fields left uncleared by a pooled derived context is not implemented, so clearing them in `IPoolableContext.OnReturn()` remains the caller's responsibility.

## Acknowledgments

Miya's design borrows from other frameworks and libraries.

- [Hono](https://hono.dev) shaped the surface API: the context object (`c.Text`, `c.Json`, `c.Param`), the `:name` and `*name` route syntax, onion-order middleware, and the typed `App<TContext>` that mirrors Hono's `Hono<Env>`.
- [zod](https://zod.dev) inspired the code-defined validation for typed input.
- The JSON serializer follows ideas from [MessagePack-CSharp](https://github.com/MessagePack-CSharp/MessagePack-CSharp) and [MemoryPack](https://github.com/Cysharp/MemoryPack): a `ref struct` writer over `IBufferWriter<byte>`, source-generated codecs, and module-initializer registration instead of runtime dispatch.
- Miya runs on [Kestrel](https://learn.microsoft.com/aspnet/core/fundamentals/servers/kestrel) from ASP.NET Core.

## License

Miya is licensed under the MIT License. See [LICENSE](LICENSE).

## Third-party notices

Third-party acknowledgements are recorded in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
