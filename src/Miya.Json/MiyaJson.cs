using System.Buffers;

namespace Miya.Json;

/// <summary>Entry points for MiyaJson serialization and codec registration.</summary>
public static class MiyaJson
{
    /// <summary>Registers the codec used for <typeparamref name="T"/>. Last registration wins.</summary>
    public static void Register<T>(IMiyaJsonCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        Registry<T>.Instance = codec;
    }

    /// <summary>Returns the registered codec for <typeparamref name="T"/>, or null.</summary>
    public static IMiyaJsonCodec<T>? TryGetCodec<T>() => Registry<T>.Instance;

    /// <summary>Returns the registered codec for <typeparamref name="T"/>, or throws.</summary>
    public static IMiyaJsonCodec<T> GetCodec<T>() => Registry<T>.Instance ?? throw MissingCodec<T>();

    /// <summary>
    /// Marks <typeparamref name="T"/> for codec generation. Needed only for types that never
    /// appear as a concrete type argument at a Json call site (for example, types used solely
    /// through generic helpers). Has no effect at runtime.
    /// </summary>
    public static void Include<T>()
    {
    }

    /// <summary>Serializes <paramref name="value"/> as UTF-8 JSON into <paramref name="destination"/>.</summary>
    public static void Serialize<T>(IBufferWriter<byte> destination, T value, MiyaJsonOptions? options = null)
    {
        var writer = new MiyaJsonWriter(destination, options ?? MiyaJsonOptions.Default);
        GetCodec<T>().Write(ref writer, value);
        writer.Flush();
    }

    /// <summary>Serializes with an explicitly supplied codec.</summary>
    public static void Serialize<T>(
        IBufferWriter<byte> destination,
        T value,
        IMiyaJsonCodec<T> codec,
        MiyaJsonOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(codec);
        var writer = new MiyaJsonWriter(destination, options ?? MiyaJsonOptions.Default);
        codec.Write(ref writer, value);
        writer.Flush();
    }

    /// <summary>Deserializes <typeparamref name="T"/> from a complete UTF-8 JSON document.</summary>
    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8Json, MiyaJsonOptions? options = null)
    {
        var reader = new MiyaJsonReader(utf8Json, options ?? MiyaJsonOptions.Default);
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
        IMiyaJsonCodec<T> codec,
        MiyaJsonOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(codec);
        var reader = new MiyaJsonReader(utf8Json, options ?? MiyaJsonOptions.Default);
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

    private static MiyaJsonException MissingCodec<T>() => new(
        $"No MiyaJson codec is registered for '{typeof(T)}'. Reference the Miya source generator " +
        $"(or run miya-gen) so a codec is generated, add MiyaJson.Include<{typeof(T).Name}>() if the " +
        "type only appears through generic code, or register a hand-written codec with MiyaJson.Register.");

    private static class Registry<T>
    {
        public static IMiyaJsonCodec<T>? Instance;
    }
}
