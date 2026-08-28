# Mugi.Gen

`mugi-gen` is the command-line companion tool for the [Mugi](https://www.nuget.org/packages/Mugi) web framework. It generates the same source code as the `Mugi.Generators` compiler package as ordinary `.cs` files, and converts between Mugi routes and OpenAPI documents.

## Install

```sh
dotnet tool install --global Mugi.Gen --version 0.1.2
```

## Generate source without the compiler generator

Some build setups cannot run compiler-integrated source generators. `mugi-gen` produces the same JSON and routing code as `.cs` files that you generate as a build step:

```sh
dotnet build MyApp.csproj
mugi-gen --project MyApp.csproj --output Generated
dotnet build MyApp.csproj
```

It does not emit the interceptor optimization, so only the direct-call speedup is absent; behavior is the same.

## Export an OpenAPI document

`mugi-gen openapi` reads the routes in a compiled project and writes an OpenAPI 3.1 document. Routes that use `Mugi.Schema` include their parameter sources, types, defaults, and validation constraints:

```sh
mugi-gen openapi --project MyApp.csproj --output openapi.json
```

## Generate a typed HTTP client

`mugi-gen client` generates a typed `HttpClient` wrapper from an OpenAPI document. Each supported operation becomes an async method with typed parameters and a JSON request or response body; non-success responses throw the generated `ApiException`:

```sh
mugi-gen client --input api/openapi.json --output Generated \
  --namespace MyApp.Api --class-name CatalogClient
```

## Import an OpenAPI document as a build step

`mugi-gen import` runs the same OpenAPI-to-C# import as the compiler generator, writing DTO records, route constants, and input schemas to disk:

```sh
mugi-gen import --input api/openapi.json --output Generated --namespace MyApp.Api
```

Full documentation is at [github.com/hckaye/Mugi](https://github.com/hckaye/Mugi).
