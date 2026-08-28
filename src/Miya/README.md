# Miya

Miya is a fast, simple web application framework for .NET. Write handlers as lambdas, route requests, run middleware, bind and validate typed input, and read the request and write the response through one context object. It runs on Kestrel without `WebApplication`, the Generic Host, or a dependency injection container.

Miya is built for NativeAOT. At runtime it uses no reflection, no assembly scanning, and no runtime code generation, so a published app starts in a few milliseconds and ships as a single small binary. Routing, JSON, and typed input binders are prepared at compile time by the `Miya.Generators` package; referencing that package is enough.

## Install

```xml
<ItemGroup>
  <PackageReference Include="Miya" Version="0.1.1" />
  <PackageReference Include="Miya.Generators" Version="0.1.1" />
</ItemGroup>
```

Add `Miya.Schema` when the app uses typed input and validation. The packages target `net9.0`; building needs the .NET 9 SDK or newer.

## Quick start

```csharp
using Miya;

var app = new App();

app.Get("/", static c => c.Text("Hello"));
app.Get("/users/:id", static c => c.Json(new User(c.Param("id"), "Ada")));

app.Run();

public sealed record User(string Id, string Name);
```

`GET /users/42` returns `{"id":"42","name":"Ada"}`. The default port is 3000; the `PORT` environment variable changes the listener without touching the code.

## What the package covers

- Routing with `:name` and `*name` patterns, correct 404, 405, `HEAD`, and `OPTIONS` handling.
- One context object for the request and the response: `c.Param`, `c.Query`, `c.Req.Header`, `c.Req.Json<T>`, `c.Text`, `c.Json`, `c.Html`, `c.Stream`, and more.
- Onion-order middleware with `app.Use`, plus built-in factories: request logging, request IDs, secure headers, CORS, CSRF, Basic and Bearer auth, compression, ETag, and request timeouts.
- Cookies with optional HMAC signing, form and multipart parsing, server-sent events, and WebSockets.
- Static file serving with conditional requests, byte ranges, and precompressed siblings.
- An in-process test client: `app.Request("GET", "/users/42")` runs the full pipeline without a server.
- Typed contexts: derive from `Context` and use `App<TContext>` to pass values from middleware to handlers without string keys or casts.

Full documentation, benchmarks, and samples are at [github.com/hckaye/Miya](https://github.com/hckaye/Miya).
