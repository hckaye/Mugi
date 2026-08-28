# Miya.Gen

`miya-gen` is the command-line companion tool for the [Miya](https://www.nuget.org/packages/Miya) web framework. It generates the same source code as the `Miya.Generators` compiler package as ordinary `.cs` files, and converts between Miya routes and OpenAPI documents.

## Install

```sh
dotnet tool install --global Miya.Gen --version 0.1.1
```

## Generate source without the compiler generator

Some build setups cannot run compiler-integrated source generators. `miya-gen` produces the same JSON and routing code as `.cs` files that you generate as a build step:

```sh
dotnet build MyApp.csproj
miya-gen --project MyApp.csproj --output Generated
dotnet build MyApp.csproj
```

It does not emit the interceptor optimization, so only the direct-call speedup is absent; behavior is the same.

## Export an OpenAPI document

`miya-gen openapi` reads the routes in a compiled project and writes an OpenAPI 3.1 document. Routes that use `Miya.Schema` include their parameter sources, types, defaults, and validation constraints:

```sh
miya-gen openapi --project MyApp.csproj --output openapi.json
```

## Generate a typed HTTP client

`miya-gen client` generates a typed `HttpClient` wrapper from an OpenAPI document. Each supported operation becomes an async method with typed parameters and a JSON request or response body; non-success responses throw the generated `ApiException`:

```sh
miya-gen client --input api/openapi.json --output Generated \
  --namespace MyApp.Api --class-name CatalogClient
```

## Import an OpenAPI document as a build step

`miya-gen import` runs the same OpenAPI-to-C# import as the compiler generator, writing DTO records, route constants, and input schemas to disk:

```sh
miya-gen import --input api/openapi.json --output Generated --namespace MyApp.Api
```

Full documentation is at [github.com/hckaye/Miya](https://github.com/hckaye/Miya).
