using Miya.Json;

namespace Miya.Reflection;

/// <summary>Controls the opt-in reflection fallback for JSON codecs.</summary>
public static class ReflectionCodecs
{
    /// <summary>Enables reflection codecs for types without a registered codec.</summary>
    public static void Enable() => global::Miya.Json.Json.Fallback = ReflectionCodecResolver.Instance;

    /// <summary>Disables the reflection fallback when it is currently enabled.</summary>
    public static void Disable()
    {
        if (ReferenceEquals(global::Miya.Json.Json.Fallback, ReflectionCodecResolver.Instance))
        {
            global::Miya.Json.Json.Fallback = null;
        }
    }
}

internal sealed class ReflectionCodecResolver : ICodecResolver
{
    internal static ReflectionCodecResolver Instance { get; } = new();

    private ReflectionCodecResolver()
    {
    }

    public IJsonCodec<T>? TryResolve<T>() => ResolvedCodec<T>.Instance;

    private static class ResolvedCodec<T>
    {
        internal static IJsonCodec<T>? Instance { get; } = Create();

        private static IJsonCodec<T>? Create()
        {
            try
            {
                return new ReflectionJsonCodec<T>(ReflectionTypeCodecCache.Get(typeof(T)));
            }
            catch (UnsupportedReflectionTypeException)
            {
                return null;
            }
        }
    }
}

internal sealed class ReflectionJsonCodec<T> : IJsonCodec<T>
{
    private readonly ReflectionTypeCodec _codec;

    internal ReflectionJsonCodec(ReflectionTypeCodec codec)
    {
        _codec = codec;
    }

    public void Write(ref JsonWriter writer, T? value) => _codec.Write(ref writer, value);

    public T? Read(ref JsonReader reader) => (T?)_codec.Read(ref reader);
}
