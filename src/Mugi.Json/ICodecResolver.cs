namespace Mugi.Json;

/// <summary>Resolves a JSON codec for a type that has no registered codec.</summary>
public interface ICodecResolver
{
    /// <summary>Returns a codec for <typeparamref name="T"/>, or null when the type is unsupported.</summary>
    IJsonCodec<T>? TryResolve<T>();
}
