# Mugi.Generators

Mugi.Generators is the compile-time source generator for the [Mugi](https://www.nuget.org/packages/Mugi) web framework. It is the piece that makes Mugi work under NativeAOT: everything that other frameworks discover at runtime is generated during the build instead.

The package installs as a Roslyn analyzer and needs no code of its own. During the build it:

- Reads each `c.Json(...)` and `c.Req.Json<T>()` call, collects the types you serialize, and generates a JSON codec for every one of them.
- Validates route patterns at compile time and embeds the parsed templates.
- Generates the binders for `Mugi.Schema` typed input, reading field selectors and rule declarations without runtime expression trees.
- Optionally imports an OpenAPI document marked as `AdditionalFiles` and generates DTOs, route constants, input schemas, and a typed `HttpClient` wrapper.
- Replaces recognized `c.Json` and route calls with direct calls into the generated code, using C# interceptors. A `buildTransitive` props file adds `Mugi.Generated` to `InterceptorsNamespaces` automatically.

Unsupported types and invalid patterns are reported as compile-time diagnostics (MUGI001 and up) that name the type or call site.

## Install

Reference it next to Mugi; building needs the .NET 9 SDK or newer:

```xml
<ItemGroup>
  <PackageReference Include="Mugi" Version="0.1.2" />
  <PackageReference Include="Mugi.Generators" Version="0.1.2" />
</ItemGroup>
```

For build setups that cannot run compiler-integrated source generators, the `Mugi.Gen` dotnet tool produces the same code as ordinary `.cs` files, and the `Mugi.Reflection` package offers a runtime fallback for JIT deployments.

Full documentation is at [github.com/hckaye/Mugi](https://github.com/hckaye/Mugi).
