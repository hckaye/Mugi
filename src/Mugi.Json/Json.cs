using System.Buffers;

namespace Mugi.Json;

/// <summary>Entry points for Json serialization and codec registration.</summary>
public static class Json
{
    private static ICodecResolver? _fallback;

    /// <summary>
    /// Gets or sets the optional resolver used when no codec has been registered for a type.
    /// </summary>
    public static ICodecResolver? Fallback
    {
        get => Volatile.Read(ref _fallback);
        set => Volatile.Write(ref _fallback, value);
    }

    /// <summary>Registers the codec used for <typeparamref name="T"/>. Last registration wins.</summary>
    public static void Register<T>(IJsonCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        Registry<T>.Instance = codec;
    }

    /// <summary>Returns the registered codec for <typeparamref name="T"/>, or null.</summary>
    public static IJsonCodec<T>? TryGetCodec<T>() => Registry<T>.Instance;

    /// <summary>Returns the registered or fallback codec for <typeparamref name="T"/>, or throws.</summary>
    public static IJsonCodec<T> GetCodec<T>()
    {
        var registered = Registry<T>.Instance;
        if (registered is not null)
        {
            return registered;
        }

        return Fallback?.TryResolve<T>() ?? throw MissingCodec<T>();
    }

    /// <summary>
    /// Returns the registered codec for <typeparamref name="T"/> when one is available;
    /// otherwise returns the generated codec supplied by an intercepted call site.
    /// </summary>
    public static IJsonCodec<T> ResolveCodec<T>(IJsonCodec<T> generated)
    {
        ArgumentNullException.ThrowIfNull(generated);
        var registered = Registry<T>.Instance;
        return registered is null || ReferenceEquals(registered, generated) ? generated : registered;
    }

    /// <summary>
    /// Marks <typeparamref name="T"/> for codec generation. Needed only for types that never
    /// appear as a concrete type argument at a Json call site (for example, types used solely
    /// through generic helpers). Has no effect at runtime.
    /// </summary>
    public static void Include<T>()
    {
    }

    /// <summary>Serializes <paramref name="value"/> as UTF-8 JSON into <paramref name="destination"/>.</summary>
    public static void Serialize<T>(IBufferWriter<byte> destination, T value, JsonOptions? options = null)
    {
        var writer = new JsonWriter(destination, options ?? JsonOptions.Default);
        GetCodec<T>().Write(ref writer, value);
        writer.Flush();
    }

    /// <summary>Serializes with an explicitly supplied codec.</summary>
    public static void Serialize<T>(
        IBufferWriter<byte> destination,
        T value,
        IJsonCodec<T> codec,
        JsonOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(codec);
        var writer = new JsonWriter(destination, options ?? JsonOptions.Default);
        codec.Write(ref writer, value);
        writer.Flush();
    }

    /// <summary>Deserializes <typeparamref name="T"/> from a complete UTF-8 JSON document.</summary>
    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8Json, JsonOptions? options = null)
    {
        var reader = new JsonReader(utf8Json, options ?? JsonOptions.Default);
        try
        {
            var value = GetCodec<T>().Read(ref reader);
            reader.ExpectEnd();
            return value;
        }
        finally
        {
            reader.Dispose();
        }
    }

    /// <summary>Deserializes with an explicitly supplied codec.</summary>
    public static T? Deserialize<T>(
        ReadOnlySpan<byte> utf8Json,
        IJsonCodec<T> codec,
        JsonOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(codec);
        var reader = new JsonReader(utf8Json, options ?? JsonOptions.Default);
        try
        {
            var value = codec.Read(ref reader);
            reader.ExpectEnd();
            return value;
        }
        finally
        {
            reader.Dispose();
        }
    }

    private static JsonException MissingCodec<T>() => new(
        $"No Json codec is registered for '{typeof(T)}'. Reference the Mugi source generator " +
        $"(or run mugi-gen) so a codec is generated, add Json.Include<{typeof(T).Name}>() if the " +
        "type only appears through generic code, or register a hand-written codec with Json.Register.");

    private static class Registry<T>
    {
        public static IJsonCodec<T>? Instance;
    }
}
