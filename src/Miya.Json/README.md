# Miya.Json

Miya.Json is the NativeAOT-compatible JSON serializer used by the [Miya](https://www.nuget.org/packages/Miya) web framework. It reads and writes JSON through per-type codecs registered ahead of time, so serialization works without reflection or runtime code generation.

In a Miya application you normally never call this package directly: the `Miya.Generators` package writes a codec for every type your handlers serialize, and `c.Json(...)` and `c.Req.Json<T>()` use them. Reference this package on its own when you need the serializer outside a handler or want to write a codec by hand.

## Serialize and deserialize

```csharp
using System.Buffers;
using Miya.Json;

var buffer = new ArrayBufferWriter<byte>();
Json.Serialize(buffer, new User("42", "Ada"));

var user = Json.Deserialize<User>(buffer.WrittenSpan);

public sealed record User(string Id, string Name);
```

`Json.Serialize<T>` writes UTF-8 JSON to any `IBufferWriter<byte>`, and `Json.Deserialize<T>` parses a `ReadOnlySpan<byte>`. Both take an optional `JsonOptions`. A codec for `T` must be registered first; in a Miya app the generator does this for the types it sees at call sites, and `Json.Include<T>()` covers a type reached only through generics.

## Writing a codec by hand

Implement `IJsonCodec<T>` and register it with `Json.Register`. A registered codec is used everywhere that type is serialized. `JsonWriter` and `JsonReader` are `ref struct` types over the output buffer and input span, with methods such as `WriteString`, `WriteNumber`, `WriteRaw`, `ReadBeginObject`, `ReadPropertyName`, and `SkipValue`.

```csharp
using Miya.Json;

Json.Register(UserCodec.Instance);
```

## Limits for untrusted input

Parsing enforces limits so hostile JSON cannot exhaust memory or the stack: document size, nesting depth, string length, collection size, and number length are all capped through `JsonOptions`. NaN and Infinity are rejected by default.

Full documentation is at [github.com/hckaye/Miya](https://github.com/hckaye/Miya).
