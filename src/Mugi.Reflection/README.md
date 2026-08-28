# Mugi.Reflection

Mugi.Reflection is an opt-in fallback for the [Mugi](https://www.nuget.org/packages/Mugi) web framework that creates JSON codecs from public properties and constructors at runtime, using reflection. Use it when neither the `Mugi.Generators` source generator nor the `mugi-gen` tool fits your build setup.

## Usage

```xml
<ItemGroup>
  <PackageReference Include="Mugi.Reflection" Version="0.1.2" />
</ItemGroup>
```

Enable the fallback once during startup:

```csharp
using Mugi.Reflection;

ReflectionCodecs.Enable();
```

The fallback is disabled by default. Once enabled, `c.Json(...)` and `c.Req.Json<T>()` work for types that have no generated or registered codec. It supports the same shapes as generated codecs: primitive values, `string`, `Guid`, `DateTime`, `DateTimeOffset`, `decimal`, enums, nullable values, arrays, `List<T>`, `Dictionary<string, T>`, POCOs, and records, with camel-case property names.

## When not to use it

Mugi.Reflection does not support NativeAOT, which is the main deployment target of Mugi. When publishing with AOT, use the generated codecs from `Mugi.Generators` or `mugi-gen` instead. A generated or hand-registered codec always takes precedence over the reflection fallback, so adding this package does not change existing behavior.

Full documentation is at [github.com/hckaye/Mugi](https://github.com/hckaye/Mugi).
