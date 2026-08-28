# Mugi

English | [日本語](README.ja.md)

[![Mugi](https://img.shields.io/nuget/v/Mugi?label=Mugi)](https://www.nuget.org/packages/Mugi)
[![Mugi.Json](https://img.shields.io/nuget/v/Mugi.Json?label=Mugi.Json)](https://www.nuget.org/packages/Mugi.Json)
[![Mugi.Schema](https://img.shields.io/nuget/v/Mugi.Schema?label=Mugi.Schema)](https://www.nuget.org/packages/Mugi.Schema)
[![Mugi.Reflection](https://img.shields.io/nuget/v/Mugi.Reflection?label=Mugi.Reflection)](https://www.nuget.org/packages/Mugi.Reflection)
[![Mugi.Jwt](https://img.shields.io/nuget/v/Mugi.Jwt?label=Mugi.Jwt)](https://www.nuget.org/packages/Mugi.Jwt)
[![Mugi.Generators](https://img.shields.io/nuget/v/Mugi.Generators?label=Mugi.Generators)](https://www.nuget.org/packages/Mugi.Generators)
[![Mugi.Gen](https://img.shields.io/nuget/v/Mugi.Gen?label=Mugi.Gen)](https://www.nuget.org/packages/Mugi.Gen)

Mugi is a fast, simple web application framework for .NET. Instead of a large framework stack, it gives you a lean, modern API: write handlers as lambdas, route requests, run middleware, bind and validate typed input, and read the request and write the response through one context object. It runs on Kestrel without `WebApplication`, the Generic Host, or a dependency injection container.

Mugi is built for NativeAOT. At runtime it uses no reflection, no assembly scanning, and no runtime code generation, so a published app starts in a few milliseconds and ships as a single small binary. Routing, JSON, and typed input binders are prepared at compile time by a source generator; you never call the generator yourself, and referencing the package is enough.

## Install

Mugi's packages target `net9.0` and run on .NET 9 or later. Building an app needs the .NET 9 SDK or newer, because the generator uses stable C# interceptors that shipped in that release.

The quickest start is the project template. It creates a small app with the packages and settings below already in place:

```sh
dotnet new install Mugi.Templates
dotnet new mugi -n HelloMugi
cd HelloMugi
dotnet run
```

To set up a project by hand instead, add the runtime package and the generator package. Add `Mugi.Schema` when the app uses typed input. The generator runs during the build and produces routing, JSON, and typed input code.

```xml
<PropertyGroup>
  <TargetFramework>net9.0</TargetFramework>
  <PublishAot>true</PublishAot>
  <InterceptorsNamespaces>$(InterceptorsNamespaces);Mugi.Generated</InterceptorsNamespaces>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="Mugi" Version="0.1.2" />
  <PackageReference Include="Mugi.Schema" Version="0.1.2" />
  <PackageReference Include="Mugi.Generators" Version="0.1.2" />
</ItemGroup>
```

The `InterceptorsNamespaces` line is required. It lets the generator replace the calls it recognizes with faster direct calls. The `Mugi.Generators` package carries the generator as an analyzer and a `buildTransitive` props file that sets this up automatically, including when the package arrives through another project reference.

`Mugi.Schema` is a separate package. Keep it only when the application uses typed input and validation.

When you reference the projects directly in a repository, pass the generator to the compiler as an analyzer:

```xml
<ItemGroup>
  <ProjectReference Include="../Mugi/src/Mugi/Mugi.csproj" />
  <ProjectReference Include="../Mugi/src/Mugi.Schema/Mugi.Schema.csproj" />
  <ProjectReference Include="../Mugi/src/Mugi.Generators/Mugi.Generators.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />
</ItemGroup>
```

## Quick start

```csharp
using Mugi;

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
| `c.Req.QueryAll("tag")` | every decoded value for a query parameter |
| `c.Req.Header("X-User")` | a request header, or null |
| `c.Req.Cookie("session")` | the first request cookie with that name, or null |
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

### Cookies

`c.Req.Cookie(name)` reads the first cookie with that name. `c.SetCookie` adds a `Set-Cookie` response header, and `c.DeleteCookie` expires a cookie. The default attributes are `Path=/` and `SameSite=Lax`; set `HttpOnly`, `Secure`, `Domain`, `MaxAge`, `Expires`, or `SameSite` through `CookieOptions` when needed. `SameSite=None` requires `Secure`.

Signed cookies use HMAC-SHA256 and are written as `value.base64url(HMAC-SHA256(UTF-8(value)))`. The application supplies the same non-empty key to `SetSignedCookie` and to every `SignedCookie` read. Mugi does not store or rotate that key for you. A missing or invalid signature returns null.

```csharp
app.Get("/session", c =>
{
    c.SetSignedCookie("account", "alice", "01234567890123456789012345678901"u8);
    var account = c.Req.SignedCookie(
        "account",
        "01234567890123456789012345678901"u8);
    return c.Text(account ?? "anonymous");
});
```

### Connection information and repeated query values

`c.Req.RemoteAddress`, `RemotePort`, `LocalAddress`, and `LocalPort` expose the connection endpoints when the transport provides them. `RemoteAddress` and `LocalAddress` are null, and the ports are 0, when the transport has no connection information, including in-process tests. `c.Req.Protocol` reports values such as `HTTP/1.1` and `HTTP/2`; `c.Req.IsHttps` reports whether the request scheme is HTTPS.

`c.Req.QueryAll(name)` returns all matching values in request order. It decodes `+` as a space and percent escapes using the same rules as `c.Query`; an invalid escape produces 400, and a missing name produces an empty array.

### Form data

`await c.Req.Form()` parses `application/x-www-form-urlencoded` and `multipart/form-data` bodies into `FormData`. `FormData.Fields` and `Files` preserve field and file order. `FormData.Get` returns the first field value, `GetAll` returns all values, and `File` returns the first buffered `FormFile`. `FormFile` exposes `Name`, the path-free `FileName`, `ContentType`, and `Content`.

```csharp
app.Post("/profile", async c =>
{
    var form = await c.Req.Form();
    var name = form.Get("name") ?? "anonymous";
    var avatar = form.File("avatar");
    return c.Text($"{name}:{avatar?.FileName ?? "none"}");
});
```

`await c.Req.Multipart()` opens a sequential `MultipartReader` without buffering the complete form. Call `ReadNextAsync` for each `MultipartPart`, then consume or complete its `Body` before reading the next part. A part exposes its field `Name`, `FileName`, `ContentType`, headers, and streaming `PipeReader Body`.

The `System.IO.Stream.Null` destination in the example stands in for a destination chosen and opened by the application.

```csharp
app.Post("/upload", async c =>
{
    var multipart = await c.Req.Multipart();
    while (await multipart.ReadNextAsync(c.Aborted) is { } part)
    {
        if (part.FileName.Length != 0)
        {
            await part.Body.CopyToAsync(System.IO.Stream.Null, c.Aborted);
        }
        else
        {
            await part.Body.CompleteAsync();
        }
    }

    return c.Text("uploaded");
});
```

`AppOptions.MaxFormBodyBytes` (10 MiB) limits the body buffered by `Form`, `MaxFormFields` (1,024) limits fields and uploaded files separately, and `MaxMultipartParts` (1,024) limits parts. All request bodies still use `MaxRequestBodyBytes` (30 MiB). `Multipart` does not use `MaxFormBodyBytes`, but it still uses `MaxRequestBodyBytes` and `MaxMultipartParts`. Direct form parsing reports unsupported media types as 415, malformed input as 400, and size-limit failures as 413.

### Server-sent events

`c.EventStream` sets `Content-Type: text/event-stream`, `Cache-Control: no-cache`, and `X-Accel-Buffering: no`, then flushes each event written by `SseWriter`.

```csharp
app.Get("/events", c => c.EventStream(async (events, cancellationToken) =>
{
    await events.Send("connected", eventName: "status", id: "1");
    await events.Retry(TimeSpan.FromSeconds(5));
    await events.Comment("keep-alive");
}));
```

`Send` writes one `data` line for each line in the payload and can include an event name and ID. `Comment` writes an SSE comment, and `Retry` writes a positive retry interval in milliseconds. Use the callback cancellation token to stop work when the connection closes.

### WebSockets

`c.WebSocket` accepts both an HTTP/1.1 GET upgrade and an HTTP/2 extended CONNECT request. Register the endpoint with `app.Get`. `WebSocketOptions.SubProtocols` lists server-preference order; Mugi selects a protocol requested by the client when one matches, and declines the subprotocol when none matches. `KeepAliveInterval` defaults to 30 seconds.

```csharp
using System.Net.WebSockets;

app.Get("/echo", c => c.WebSocket(async (socket, cancellationToken) =>
{
    var buffer = new byte[1024];
    var received = await socket.ReceiveAsync(buffer, cancellationToken);
    if (received.MessageType != WebSocketMessageType.Close)
    {
        await socket.SendAsync(
            buffer.AsMemory(0, received.Count),
            received.MessageType,
            received.EndOfMessage,
            cancellationToken);
    }
}, new WebSocketOptions
{
    SubProtocols = ["chat"]
}));
```

The handler receives the connected `System.Net.WebSockets.WebSocket` and the request-abort token. If the handler returns while the socket is still open, Mugi closes it normally. A handler exception attempts a 1011 close and then aborts the connection.

### HTML interpolation

The interpolated-string overload of `c.Html($"...")` writes literals as authored and HTML-escapes interpolated values, including `&`, `<`, `>`, `"`, and `'`. The explicit opt-out API is `RawHtml.From(markup)`, which writes a trusted value verbatim. `Html.Raw` is not part of the public API.

```csharp
app.Get("/hello", c =>
{
    var name = c.Query("name") ?? "guest";
    return c.Html($"<p>Hello, {name}</p>");
});
```

The `Html(string)` overload is raw. If an interpolated string is first assigned to a `string` variable and that variable is passed to `c.Html`, its contents are not escaped. Keep untrusted values in interpolation holes, or call `RawHtml.From` only for markup that has already been made safe.

## Static files

`app.Static` registers GET routes for a filesystem directory or an embedded-resource prefix. Set exactly one of `StaticOptions.Root` and `StaticOptions.Source`. `Index` defaults to `index.html`; set it to an empty string to disable directory indexes. `CacheControl` adds the same cache policy to static responses, and `Precompressed` enables sibling `.br` and `.gz` files for filesystem sources by default.

```csharp
app.Static("/assets", new StaticOptions
{
    Root = "wwwroot",
    CacheControl = "public, max-age=3600",
    Precompressed = true
});
```

Filesystem paths are checked lexically under the configured root. Backslashes, rooted or drive-qualified paths, and `.` or `..` segments are rejected; symlinks inside the root are allowed. A directory is served only through its index file when the request path ends in `/` or names the static root. Missing or rejected paths use the app's `NotFound` handler.

Filesystem responses support `Last-Modified`, ETags, conditional requests with `If-None-Match` and `If-Modified-Since`, and advertise `Accept-Ranges: bytes`. One satisfiable byte range returns 206 with `Content-Range`; an unsatisfiable range returns 416. Multiple ranges or an unrecognized range falls back to the full response. `If-Range` controls whether a range is used. When accepted by `Accept-Encoding`, `.br` is preferred over `.gz`; the response keeps the original file's content type and adds `Content-Encoding` and `Vary: Accept-Encoding`.

Embedded resources use ETags and conditional requests but do not provide `Last-Modified`, advertise or process ranges, or use precompressed siblings. Resource names containing `/` map verbatim after the configured prefix. Default dotted MSBuild names use dots as directory separators while retaining the final extension separator. MSBuild can replace `-` with `_`; use an explicit `LogicalName` when the URL must preserve the hyphen:

```xml
<ItemGroup>
  <EmbeddedResource Include="wwwroot/index.html"
                    LogicalName="MyAssets/index.html" />
  <EmbeddedResource Include="wwwroot/app.js"
                    LogicalName="MyAssets/app.js" />
</ItemGroup>
```

```csharp
app.Static("/assets", new StaticOptions
{
    Source = StaticSource.Embedded(typeof(Program).Assembly, "MyAssets")
});
```

## Testing with the in-process client

`app.Request(method, target, options)` runs the complete application pipeline without starting a server. The method is normalized to uppercase, the target may include a query string, and streamed response bodies are collected in `TestResponse`. `TestRequestOptions` supplies a byte `Body` or UTF-8 `TextBody`; a non-empty `Body` and `TextBody` cannot be used together. It also supplies repeated `Headers`. `TestResponse` exposes `Status`, ordered repeated `Headers`, `Body`, `Header`, `HeaderValues`, `Text`, and `Json<T>` when a codec for `T` is registered. Kestrel transport headers such as `Date` and `Server` are not included.

```csharp
using Mugi;
using Xunit;

public sealed class UserTests
{
    [Fact]
    public async Task GetsUser()
    {
        var app = new App();
        app.Get("/users/:id", static c => c.Text(c.Param("id")));

        var response = await app.Request("GET", "/users/42");

        Assert.Equal(200, response.Status);
        Assert.Equal("42", response.Text());
    }
}
```

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

`Mugi.Schema` combines route parameters, query values, headers, form fields, and JSON body fields in one input record. The handler runs only after parsing and validation succeed.

```csharp
using Mugi.Schema;

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

Text values support primitives, `string`, `Guid`, Boolean values, enum names or numbers, `DateTime`, and `DateTimeOffset`. Body fields use Mugi's generated JSON codecs. The generator reads the field selectors and rule declarations at build time; the runtime does not invoke those selectors or compile expression trees.

Rules can be chained. Numeric fields support `Min`, `Max`, `Range`, `Positive`, and `NonNegative`. Strings support `NotEmpty`, `Length`, `MinLength`, `MaxLength`, and `Pattern`. Every field supports `Optional`, `Default`, and `Must`.

A missing required value, parse failure, invalid JSON body, or failed rule returns 400 without calling the handler. The response has `Content-Type: application/json` and this shape:

```json
{
  "errors": [
    { "field": "age", "message": "must be between 0 and 120" }
  ]
}
```

### Form binding

Use `.Form(input => input.Field)` to read a field from `await c.Req.Form()`. The generated binder calls `FormData.Get` with the property name, so it uses the first value for that field. It supports URL-encoded and multipart form fields. `Mugi.FormFile` is not a supported generated field type; use `FormData.File` or the streaming `MultipartReader` API for uploads.

```csharp
var formSchema = Schemas.For<CreatePersonInput>()
    .Form(input => input.Name, rules => rules.NotEmpty().MaxLength(80))
    .Form(input => input.Age, rules => rules.Range(0, 120));

app.Post("/people", formSchema,
    static (c, input) => c.Json(input));

public sealed record CreatePersonInput(string Name, int Age);
```

`.Form` and `.Body` cannot be used in the same schema. The generator reports MUGI016 for that combination. Form parsing errors in a generated typed endpoint are returned as the endpoint's structured validation response with status 400. Direct calls to `c.Req.Form()` retain their input status: unsupported media type is 415, malformed input is 400, and a form limit is 413.

### Text parsing rules

Generated text binding uses invariant culture and strict formats. Integer types accept an optional leading sign only. `float` and `double` accept a leading sign, decimal point, and exponent. `decimal` accepts a leading sign and decimal point, but not an exponent or thousands separators. Boolean values use `Boolean.TryParse`, enum names are case-sensitive and numeric enum values are accepted, and `char` requires one character.

`DateTime` accepts `yyyy-MM-dd`, `yyyy-MM-ddK`, `yyyy-MM-dd'T'HH:mm:ss`, `yyyy-MM-dd'T'HH:mm:ssK`, `yyyy-MM-dd'T'HH:mm:ss.FFFFFFF`, `yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK`, and the round-trip `O` format. `DateTimeOffset` accepts `yyyy-MM-dd`, `yyyy-MM-dd'T'HH:mm:ss`, and `yyyy-MM-dd'T'HH:mm:ss.FFFFFFF` without an offset, with those values treated as UTC. It also accepts `yyyy-MM-ddK`, `yyyy-MM-dd'T'HH:mm:ssK`, `yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK`, and `O` with an offset or zone.

`Pattern(regex)` first creates a culture-invariant regular expression with `RegexOptions.NonBacktracking`. If that option does not support the expression, it falls back to a culture-invariant regular expression with a one-second match timeout. A match timeout is a failed validation.

### Sharing schema parts

Define a reusable `Schemas.Part<TPart>()` for fields declared by an interface or base type, then apply it with `.Use(part)`. The `Use<T, TPart>` extension has `where T : TPart`, so a part can only be applied to a concrete input that implements or derives from its declaration. Direct mappings on the concrete schema override a part mapping with the same member name. Two parts contributing the same member are a conflict and produce MUGI024. A part type must have one definition in the same compilation, and its members must be implemented implicitly. Violations produce MUGI017 through MUGI019.

```csharp
public interface IPageQuery
{
    int Page { get; }
}

public sealed record SearchOptions(string Query);
public sealed record SearchInput(int Page, SearchOptions Options) : IPageQuery;

var pagePart = Schemas.Part<IPageQuery>()
    .Query(input => input.Page, rules => rules.Default(1).Range(1, 50));

var searchSchema = Schemas.For<SearchInput>()
    .Query(input => input.Page, rules => rules.Range(1, 10))
    .Body(input => input.Options)
    .Use(pagePart);
```

### Sharing rule methods

For a rule set that applies to a nested record, pass a static method containing one rule chain rooted at its `Rule<T>` parameter. The method can be passed as a method group or through a forwarding lambda. Every predicate referenced from generated code must be `internal` or `public`, along with its containing type and any required members. A private predicate produces MUGI026.

```csharp
public sealed record Address(string City);
public sealed record Profile(string Name, Address Address);
public sealed record CreateProfileInput(Profile Profile);

public static class ProfileRules
{
    public static void Apply(Rule<Profile> rule) =>
        rule.Must(ProfileRules.HasName, "name must not be empty")
            .Must(ProfileRules.HasCity, "city must not be empty");

    public static bool HasName(Profile value) => value.Name.Length != 0;
    public static bool HasCity(Profile value) => value.Address.City.Length != 0;
}

var profileSchema = Schemas.For<CreateProfileInput>()
    .Body(input => input.Profile, ProfileRules.Apply);
```

The method itself must be a static method in the same compilation and contain a single chain. A multi-statement method, an instance method, a chain rooted at another `Rule<T>`, or a method from another assembly produces MUGI025.

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

### Middleware factories and typed apps

The built-in factories are in the `Mugi.Middleware` namespace and return `Middleware<Context>`. Register the returned delegate with `app.Use(...)`:

```csharp
using Mugi.Middleware;

app.Use(RequestId.Middleware());
```

For `App<TContext>`, the `App<TContext>` adapter extension also accepts a `Middleware<Context>` and passes the typed context through it. The adapter requires middleware to call `next` with the same context instance; passing another instance throws. A typed `Middleware<TContext>` uses the instance `Use` overload directly, while a `Middleware<Context>` uses the adapter.

### RequestLogger

`RequestLogger.Middleware()` writes `METHOD PATH STATUS elapsedms` after the request completes. The default writer is `Console.Out`; set `RequestLoggerOptions.Writer` to another `TextWriter`. If the pipeline throws, it logs status 500 and rethrows.

```csharp
app.Use(RequestLogger.Middleware());
```

### RequestId

`RequestId.Middleware()` uses `X-Request-Id` and trusts an incoming value by default. A trusted value is 1 to 128 characters containing only letters, digits, `.`, `_`, or `-`. Other values are replaced with a new 32-character lowercase hexadecimal identifier. Set `RequestIdOptions.HeaderName` or `TrustIncoming` to change this. The generic factory stores the selected value in `IRequestIdContext.RequestId`.

```csharp
app.Use(RequestId.Middleware(new RequestIdOptions { TrustIncoming = false }));
```

### SecureHeaders

`SecureHeaders.Middleware()` fills headers that the handler has not already set, and handler values win. Its defaults are `X-Content-Type-Options: nosniff`, `X-Frame-Options: SAMEORIGIN`, `Referrer-Policy: no-referrer`, `Strict-Transport-Security: max-age=15552000; includeSubDomains`, `X-XSS-Protection: 0`, `Cross-Origin-Opener-Policy: same-origin`, `Cross-Origin-Resource-Policy: same-origin`, `X-Permitted-Cross-Domain-Policies: none`, and `X-Download-Options: noopen`. Content Security Policy is omitted by default. Set any option to null to omit that header. Headers are not added after streaming starts.

```csharp
app.Use(SecureHeaders.Middleware(new SecureHeadersOptions
{
    ContentSecurityPolicy = "default-src 'self'"
}));
```

### Cors

`Cors.Middleware` matches exact, case-sensitive origins. Its default origin list is empty, its default methods are `GET`, `POST`, `PUT`, `DELETE`, `PATCH`, `HEAD`, and `OPTIONS`, and its default header list is empty. An empty header list echoes a safe `Access-Control-Request-Headers` value on preflight. `Credentials` defaults to false and `MaxAge` is unset. An origins list containing `*` allows any origin and sends `*`; it cannot be combined with credentials.

```csharp
app.Use(Cors.Middleware(new CorsOptions
{
    Origins = ["https://app.example"],
    Methods = ["GET", "POST"],
    Headers = ["Content-Type", "X-Request-Id"],
    ExposeHeaders = ["X-Request-Id"],
    Credentials = true,
    MaxAge = TimeSpan.FromMinutes(10)
}));
```

For a matching preflight, the middleware returns 204 and does not call the next handler. Register it in the middleware pipeline so it handles preflight before the router's automatic `OPTIONS` response. Matching actual requests run the next handler and receive the CORS response headers afterward. Non-matching or missing origins continue without CORS headers.

### BasicAuth

`BasicAuth.Middleware` requires exactly one fixed `Username` and `Password` pair or a `Validate` callback. The default realm is `Restricted`. Invalid or missing credentials return 401 with a UTF-8 Basic challenge and do not call the next handler. Fixed credentials use fixed-time comparison, and passwords may contain colons. The generic factory stores the decoded username in `IAuthContext.AuthUser`.

```csharp
app.Use(BasicAuth.Middleware(new BasicAuthOptions
{
    Username = "admin",
    Password = "s3cret"
}));
```

### BearerAuth

`BearerAuth.Middleware` requires exactly one fixed `Token` or `Validate` callback. The default realm is `Restricted`, and the token uses the RFC 6750 `b64token` character set. Missing authorization or a different scheme returns 401. A malformed Bearer header returns 400 with `error="invalid_request"`; a token rejected by validation returns 401 with `error="invalid_token"`. The generic factory stores the validated token string in `IAuthContext.AuthUser`. This middleware compares bearer tokens; it does not verify JWTs. Use `Mugi.Jwt` for JWTs.

```csharp
app.Use(BearerAuth.Middleware(new BearerAuthOptions
{
    Token = "demo-token"
}));
```

### Csrf

`Csrf.Middleware()` checks the `Origin` header for non-safe methods with a form-like content type: an empty or missing type, `application/x-www-form-urlencoded`, `multipart/form-data`, or `text/plain`. GET, HEAD, and OPTIONS are not checked, and JSON requests pass. The default requires an Origin other than `null` and compares its HTTP or HTTPS authority with the request `Host` header, ignoring case but not comparing schemes. Set `CsrfOptions.Origins` for exact, case-sensitive allowed origins, or `ValidateOrigin` for a callback. A rejected request returns 403 and does not call the next handler.

```csharp
app.Use(Csrf.Middleware(new CsrfOptions
{
    Origins = ["https://app.example", "https://admin.example"]
}));
```

### Compression

`Compression.Middleware()` compresses buffered responses of at least 1,024 bytes with Brotli or gzip, using `CompressionLevel.Fastest` by default. It considers text, JSON, JavaScript, SVG, XML, and WebAssembly content types, honors `Accept-Encoding` quality values, and prefers Brotli on a tie. It only replaces the response when the compressed bytes are smaller, then adds `Content-Encoding` and `Vary: Accept-Encoding`. It skips streamed or promoted responses, bodyless statuses, an existing `Content-Encoding`, `Content-Range`, or `ETag`.

```csharp
app.Use(ETag.Middleware());
app.Use(Compression.Middleware(new CompressionOptions { MinBytes = 512 }));
```

Register ETag before Compression as shown. Compression then chooses the representation first, and ETag observes the compressed bytes.

### ETag

`ETag.Middleware()` adds a strong entity tag to non-empty buffered 200 responses for GET and HEAD. A handler-supplied `ETag` is preserved. A matching `If-None-Match` changes the response to 304 with an empty body. Set `ETagOptions.Weak = true` for weak generated tags. Streaming and promoted responses are skipped.

```csharp
app.Use(ETag.Middleware(new ETagOptions { Weak = true }));
```

### RequestTimeout

`RequestTimeout.Middleware(timeout)` has no default timeout. When a positive deadline expires while the response is still buffered, it replaces the response with status 504 and the `text/plain; charset=utf-8` body `Gateway Timeout`. If streaming has started, the status cannot be changed, so Mugi aborts the connection.

```csharp
app.Use(RequestTimeout.Middleware(TimeSpan.FromSeconds(2)));
```

### Buffered response hooks

Middleware authors can inspect a non-empty buffered response with `TryGetBufferedResponse(out var body)` and replace it with `ReplaceBufferedResponse(body, contentType)`. Both are available only before the response is sent or starts streaming. The memory returned by `TryGetBufferedResponse` is valid until the response is replaced or the request finishes. It returns false after automatic promotion past `AppOptions.MaxBufferedResponseBytes`, and it also returns false when no body was written. A replacement for a body-forbidden status is discarded; on HEAD, the replacement is retained for middleware inspection while only its length is sent.

```csharp
app.Use(async (c, next) =>
{
    await next(c);
    if (c.TryGetBufferedResponse(out var body))
    {
        var replacement = body.ToArray();
        c.ReplaceBufferedResponse(replacement);
    }
});
```

## Returning and reading JSON

Mugi reads and writes JSON with its own serializer. In the common case you configure nothing: return an object and Mugi writes it as JSON.

```csharp
app.Get("/users/:id", c => c.Json(new User(c.Param("id"), "Ada")));

app.Post("/users", async c =>
{
    var user = await c.Req.Json<User>();   // parse the request body
    await c.Json(user);                    // write it back as JSON
});

public sealed record User(string Id, string Name);
```

At build time the generator reads each `c.Json(...)` and `c.Req.Json<T>()` call, collects the types you serialize, and generates the code that reads and writes them. Nothing is discovered at runtime, which is why this works under NativeAOT. Property names are `camelCase` by default; set `<MugiJsonNaming>PascalCase</MugiJsonNaming>` to keep the C# casing.

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
using Mugi.Json;

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

Request parsing uses limits so that malformed or hostile JSON cannot exhaust memory or the stack. The defaults are intended for input from the network and are set on `AppOptions` and `JsonOptions`. Response serialization is single-pass: input document, string, collection, and number limits do not cap a response. Response JSON keeps the configured maximum depth, non-finite-number policy, pooled-buffer retention setting, and cancellation token.

| Setting | Default |
| --- | ---: |
| JSON request body, `AppOptions.MaxJsonBodyBytes` | 1 MiB |
| Complete input JSON document, `MaxDocumentByteLength` | 1 MiB |
| Object and array depth, `MaxDepth` | 64 |
| One input string token, `MaxStringByteLength` | 1 MiB |
| Input members in one object or elements in one array, `MaxCollectionSize` | 1,048,576 |
| Input digits in one number, `MaxNumberDigits` | 128 |
| Retained JSON temporary buffer, `MaxPooledBufferByteLength` | 64 KiB |
| Response buffer before chunked promotion, `AppOptions.MaxBufferedResponseBytes` | 1 MiB |
| Request body, `AppOptions.MaxRequestBodyBytes` | 30 MiB |

NaN and Infinity are rejected by default. `JsonOptions` also carries a cancellation token for long serialization and parsing.

`AppOptions.MaxBufferedResponseBytes` is a response-buffer threshold, not an output JSON limit. Mugi writes the response serializer once; when the buffer crosses the threshold, the bytes already written are promoted to chunked streaming and the remaining JSON is written there. The response is not rejected because it is larger than the threshold.

As a build-time optimization, Mugi replaces the `c.Json` and route calls it recognizes with direct calls into the generated code, using a C# feature called interceptors. This changes nothing you observe: serialization and routing behave the same whether or not a call was replaced, and a call that the generator cannot see still works as long as a codec is registered.

## Generating source without the compiler generator

Some build setups cannot run compiler-integrated source generators. `mugi-gen` produces the same JSON and routing code as ordinary `.cs` files that you generate as a build step. It does not emit the interceptor optimization, so only the direct-call speedup is absent; behavior is the same.

```sh
dotnet tool install --global Mugi.Gen --version 0.1.2
dotnet build MyApp.csproj
mugi-gen --project MyApp.csproj --output Generated
dotnet build MyApp.csproj
```

The SDK compiles `Generated/*.cs` automatically when the directory is under the project root; a directory elsewhere must be added with a `Compile` item. The project must compile before generation, and existing `Mugi.*.g.cs` files in the output directory are replaced. From this repository the equivalent command is:

```sh
dotnet run --project src/Mugi.Gen -- \
  --project samples/Hello/Hello.csproj \
  --output samples/Hello/Generated
```

## Zero-generation runtime (Mugi.Reflection)

Routing and text responses already work without generated source. When neither the source generator nor `mugi-gen` is available, add the opt-in `Mugi.Reflection` package to create JSON codecs from public properties and constructors at runtime.

```xml
<PackageReference Include="Mugi.Reflection" Version="0.1.2" />
```

Enable the fallback once during startup:

```csharp
using Mugi.Reflection;

ReflectionCodecs.Enable();
```

The fallback is disabled by default. It supports the same primitive values, arrays, `List<T>`, `Dictionary<string, T>`, nullable values, enums, POCOs, and records with camel-case property names. `Mugi.Reflection` does not support NativeAOT; use generated codecs when publishing with AOT.
## OpenAPI

`mugi-gen openapi` reads the routes in a compiled project and writes an OpenAPI 3.1 document:

```sh
mugi-gen openapi --project MyApp.csproj --output openapi.json
```

Route parameters are emitted as required path parameters. A route that uses `Mugi.Schema` also includes the source, type, default, and supported validation constraints for its path, query, header, and JSON body fields. Referenced JSON DTOs are placed under `components/schemas`.

Response detection is best effort and examines the handler lambda at the registration site. A `c.Json(value)` call produces an `application/json` response schema, and `c.Text(value)` produces `text/plain`. When neither call can be identified, the operation has a 200 response without declared content. Typed routes also declare the validation-error 400 response.

## Importing an OpenAPI document

`Mugi.Generators` can read an existing OpenAPI document during compilation. The `mugi-gen openapi` command above goes from C# routes to an OpenAPI document; this setting goes from an OpenAPI document to generated C#.

Add the JSON file as an `AdditionalFiles` item and mark it with `MugiOpenApi`:

```xml
<ItemGroup>
  <AdditionalFiles Include="api/openapi.json"
                   MugiOpenApi="true"
                   MugiOpenApiNamespace="MyApp.Api" />
</ItemGroup>
```

`MugiOpenApiNamespace` sets the namespace for the generated types. When it is omitted, the project root namespace is used.

The generator produces public DTO records and string enums from `components/schemas`, a `Paths` class with one constant per operation, and an input record with an `ApiSchemas` field for each operation. OpenAPI path parameters such as `/users/{id}` become Mugi patterns such as `/users/:id`. Each build recreates the generator-owned `.g.cs` file, so changes belong in the OpenAPI document rather than the generated source.

The importer accepts OpenAPI 3.0 and 3.1 JSON. It supports object schemas, string enums, strings, Boolean values, `int32`, `int64`, `float`, `double`, `decimal`, arrays, local `components/schemas` references, nullable fields, and required fields. Operation input supports path, query, and header parameters plus JSON object request bodies. It maps numeric bounds, exclusive integer bounds, string lengths, patterns, defaults, and optional fields to `Mugi.Schema` rules.

Composed schemas (`oneOf`, `anyOf`, and `allOf`), `additionalProperties`, external references, cookie parameters, non-JSON request bodies, and validation constraints without a `Mugi.Schema` equivalent are skipped with MUGI020 through MUGI023 diagnostics. Path and query parameter names must also be valid C# identifiers because their generated schema mappings use the same names.

### Generating an HTTP client

The `mugi-gen client` mode generates a typed `HttpClient` wrapper from an OpenAPI document. `--namespace` defaults to `Generated`, and `--class-name` defaults to a name derived from the document title:

```sh
mugi-gen client --input api/openapi.json --output Generated \
  --namespace MyApp.Api --class-name CatalogClient
```

The generated `CatalogClient` is a public sealed class with a `CatalogClient(HttpClient http)` constructor. Each supported operation becomes an async method with path, required and optional query or header parameters, a JSON request body when defined, and a final optional `CancellationToken`. A JSON success response body returns `Task<T>`; an operation with no response body returns `Task`. A non-JSON success response body is not represented. Non-success HTTP responses throw the generated `ApiException`, which exposes `Status` and a UTF-8 `Body` truncated to 4,096 bytes.

The compiler generator uses separate `AdditionalFiles` metadata for client generation:

```xml
<ItemGroup>
  <AdditionalFiles Include="api/openapi.json"
                   MugiOpenApiClient="true"
                   MugiOpenApiNamespace="MyApp.Api"
                   MugiOpenApiClientName="CatalogClient" />
</ItemGroup>
```

`MugiOpenApiClient` enables the client independently of server import. `MugiOpenApiNamespace` sets the target namespace and `MugiOpenApiClientName` sets the class name; when omitted, the project root namespace and a name derived from the OpenAPI title are used. Set both `MugiOpenApi="true"` and `MugiOpenApiClient="true"` on one file when the server import and client should share generated component declarations. Only JSON success response bodies can be represented by the generated client; unsupported operations are skipped with a generator diagnostic.

`mugi-gen import` runs the same import as a manual step for build setups that cannot use the source generator. It writes the generated `.g.cs` to disk instead of into the compilation:

```sh
mugi-gen import --input api/openapi.json --output Generated --namespace MyApp.Api
```

## Mugi.Jwt

The `Mugi.Jwt` package signs and verifies compact JWTs without reflection. It supports HS256, RS256, and ES256. `Jwt.Sign` creates a token with the algorithm selected by a `JwtKey`, and `Jwt.Verify` checks the signature and registered claims, then returns a `JwtResult` rather than throwing for an invalid token.

```xml
<PackageReference Include="Mugi.Jwt" Version="0.1.2" />
```

```csharp
using Mugi.Jwt;

var key = JwtKey.HS256("01234567890123456789012345678901"u8);
var token = Jwt.Sign(
    new JwtPayload
    {
        Subject = "alice",
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
    },
    key);

var result = Jwt.Verify(token, key);
if (result.IsValid)
{
    Console.WriteLine(result.Payload!.Subject);
}
```

`JwtKey.HS256` copies a secret of at least 32 bytes. `JwtKey.RS256` accepts an RSA key of at least 2048 bits, and `JwtKey.ES256` accepts an ECDSA key on NIST P-256. `JwtPayload` contains registered claims and can add scalar string, integer, and Boolean claims with `WithClaim`.

`JwtValidation` can require an exact `Issuer`, require an `Audience`, set `ClockSkew` (60 seconds by default), control `RequireExpiration` (true by default), and supply a `Clock`. Verification fixes the accepted algorithm to the supplied key. It rejects `none`, unknown algorithms, and tokens whose algorithm does not match the key before signature validation.

`JwtAuth.Middleware` validates a bearer token before calling the next handler. Missing or invalid tokens return 401 with a Bearer challenge. `JwtAuthOptions.Key` is required, `Validation` is optional, and `Realm` defaults to `Restricted`. The generic overload requires a context implementing `IJwtContext` and stores the verified `JwtPayload` on its `Jwt` property.

```csharp
using Mugi;
using Mugi.Jwt;

public sealed class ApiContext : Context, IJwtContext
{
    public JwtPayload? Jwt { get; set; }
}

var api = new App<ApiContext>();
api.Use(JwtAuth.Middleware<ApiContext>(new JwtAuthOptions { Key = key }));
```

## Typed contexts

By default a handler's context carries only request and response data. To pass your own values from middleware to a handler with full type safety, derive from `Context` and use `App<TContext>`. There are no string keys and no casts.

```csharp
using Mugi;

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

`Run(int? port = null)` starts an HTTP/1.1 listener and blocks until cancellation or a termination signal. With no address configuration it binds loopback. `Run()` leaves the port unspecified so the `PORT` environment variable applies; `Run(8080)` chooses the port explicitly. `RunAsync(options, ct)` and `StartAsync(options, ct)` host asynchronously; `StartAsync` returns a `Server` with the bound addresses and a `StopAsync` method. Port 0 asks the operating system for a free port.

Port selection uses the explicit `Run(port)` value first, then `AppOptions.Port`, then a valid integer in `PORT`, then 3000. A value outside 0 through 65535 supplied explicitly or through options is rejected; an invalid `PORT` value is ignored.

`AppOptions.Address` selects the bind address and takes precedence over `HOST`. When it is omitted, `HOST` is used when it contains an IP address; otherwise Mugi uses loopback. A container that must accept traffic from outside the container should set `Address = IPAddress.Any` or `HOST=0.0.0.0`. `IPAddress.IPv6Any` binds a dual-stack listener.

```csharp
using System.Net;

await app.RunAsync(new AppOptions
{
    Address = IPAddress.Any,
    Port = 8080
});
```

SIGINT, SIGTERM, and cancellation stop accepting new requests and wait for the ones in flight, with a 30 second shutdown timeout by default. A second signal ends the process immediately.

The same graceful signal registration is used on Windows. Ctrl+C and termination requests stop accepting new work and wait for active requests within the configured timeout.

### HTTP/2 and HTTP/3

Without a certificate the default is HTTP/1.1. Select `Protocols.Http2` for cleartext HTTP/2:

```csharp
await app.RunAsync(new AppOptions
{
    Protocols = Protocols.Http2,
});
```

A cleartext listener cannot serve HTTP/1.1 and HTTP/2 at once, because it has no ALPN negotiation, and Mugi rejects that combination at startup.

Pass an `X509Certificate2` to terminate TLS inside Mugi. With a certificate the default is HTTP/1.1 and HTTP/2, chosen per connection through ALPN:

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

`ConfigureKestrel` reaches other supported Kestrel settings. Certificate selection stays in `AppOptions.Certificate`; Mugi does not search for a development certificate or read Kestrel endpoint configuration files.

`AppOptions.ConfigureServices` registers extra services in the internal Kestrel host. Mugi never requires dependency injection; this hook exists only for advanced Kestrel customization. Setting it uses the service-backed hosting path even for cleartext endpoints, and the registered services stay inside the server rather than reaching handlers or middleware.

## Performance

Mugi is built to be fast and allocation-light. In the measured scenarios:

- The generated JSON serialization matches or beats System.Text.Json source generation on both mean time and allocated bytes, under JIT and NativeAOT.
- Routing and the middleware pipeline allocate nothing on the synchronous hot path (a 404 miss and a 405 mismatch allocate only their small response state).
- The `samples/Hello` NativeAOT binary is about 6.8 MiB and answers its first request within a few milliseconds of process start.

Mugi runs on Kestrel, the same HTTP server ASP.NET Core uses, so the raw request throughput is the same as an ASP.NET Core app doing the same work. The server is the shared bottleneck; Mugi's difference is the thin layer above it, which shows up as lower per-request memory rather than higher throughput.

Numbers, scenarios, the measurement environment, and how to reproduce them are in [docs/benchmarks.md](docs/benchmarks.md).

## v0 limitations

Mugi v0 does not provide templates, development-certificate discovery, or configuration-file integration. HTTP/3 depends on `QuicListener.IsSupported` and a supplied certificate. A reverse proxy remains an option for TLS termination.

The route generator validates and parses literal patterns at compile time and embeds the parsed templates. At startup the runtime builds a segment trie from them and matches against it; the generator does not emit route-specific matching code.

Diagnostics MUGI001 through MUGI004 cover JSON and route generation. MUGI006 checks literal `c.Param` calls against their handler's route. MUGI010 through MUGI015 cover typed-input route mappings, supported field types, schema declarations, rules, and conflicting binding shapes. MUGI016 covers combining form and JSON body mappings. MUGI017 through MUGI019 cover duplicate or undeclared schema parts and explicit interface implementations. MUGI020 through MUGI023 cover invalid OpenAPI imports, unsupported schema structures, values that cannot be mapped to Mugi, and generated-name collisions. MUGI024 through MUGI026 cover conflicting schema-part members, invalid shared rule declarations, and inaccessible predicates. The planned MUGI005 diagnostic for fields left uncleared by a pooled derived context is not implemented, so clearing them in `IPoolableContext.OnReturn()` remains the caller's responsibility.

## Acknowledgments

Mugi's design borrows from other frameworks and libraries.

- [Hono](https://hono.dev) shaped the surface API: the context object (`c.Text`, `c.Json`, `c.Param`), the `:name` and `*name` route syntax, onion-order middleware, and the typed `App<TContext>` that mirrors Hono's `Hono<Env>`.
- [zod](https://zod.dev) inspired the code-defined validation for typed input.
- The JSON serializer follows ideas from [MessagePack-CSharp](https://github.com/MessagePack-CSharp/MessagePack-CSharp) and [MemoryPack](https://github.com/Cysharp/MemoryPack): a `ref struct` writer over `IBufferWriter<byte>`, source-generated codecs, and module-initializer registration instead of runtime dispatch.
- Mugi runs on [Kestrel](https://learn.microsoft.com/aspnet/core/fundamentals/servers/kestrel) from ASP.NET Core.

## License

Mugi is licensed under the MIT License. See [LICENSE](LICENSE).

## Third-party notices

Third-party acknowledgements are recorded in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
