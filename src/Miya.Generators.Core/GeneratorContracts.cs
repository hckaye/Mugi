using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Miya.Generators.Core;

public enum JsonNaming
{
    CamelCase,
    PascalCase,
}

public sealed class GeneratorSettings
{
    public GeneratorSettings(
        JsonNaming naming = JsonNaming.CamelCase,
        bool emitInterceptors = true)
    {
        Naming = naming;
        EmitInterceptors = emitInterceptors;
    }

    public JsonNaming Naming { get; }

    public bool EmitInterceptors { get; }

    public override bool Equals(object? obj) =>
        obj is GeneratorSettings other
        && Naming == other.Naming
        && EmitInterceptors == other.EmitInterceptors;

    public override int GetHashCode() => ((int)Naming * 397) ^ (EmitInterceptors ? 1 : 0);
}

public sealed class GeneratedSource
{
    public GeneratedSource(string hintName, string source)
    {
        HintName = hintName;
        Source = source;
    }

    public string HintName { get; }

    public string Source { get; }
}

public sealed class GenerationResult
{
    public GenerationResult(
        ImmutableArray<GeneratedSource> sources,
        ImmutableArray<Diagnostic> diagnostics)
    {
        Sources = sources;
        Diagnostics = diagnostics;
    }

    public ImmutableArray<GeneratedSource> Sources { get; }

    public ImmutableArray<Diagnostic> Diagnostics { get; }
}
