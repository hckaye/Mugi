# Mugi.Templates

Project templates for the [Mugi](https://www.nuget.org/packages/Mugi) web framework. Installing this package adds the `mugi` template to `dotnet new`.

## Usage

```sh
dotnet new install Mugi.Templates
dotnet new mugi -n MyApp
cd MyApp
dotnet run
```

`dotnet run` starts the app on port 3000. Try it:

```sh
curl http://127.0.0.1:3000/
curl http://127.0.0.1:3000/users/42
curl "http://127.0.0.1:3000/search/1?Query=mugi&Limit=5"
```

## What the template creates

A single-file Mugi application with three routes: a text response, a JSON response with a route parameter, and a typed input route validated by `Mugi.Schema`. The project references `Mugi`, `Mugi.Schema`, and `Mugi.Generators`, targets `net10.0`, and has `PublishAot` enabled, so `dotnet publish -c Release` produces a self-contained NativeAOT binary.

Building needs the .NET 10 SDK or newer. Full documentation is at [github.com/hckaye/Mugi](https://github.com/hckaye/Mugi).
