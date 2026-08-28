using System.Collections.Immutable;

namespace Mugi.Generators.Core;

internal sealed class OpenApiJsonSourceEmitter
{
    private readonly ImmutableArray<OpenApiJsonCodecModel> _models;

    internal OpenApiJsonSourceEmitter(ImmutableArray<OpenApiJsonCodecModel> models)
    {
        _models = models;
    }

    internal void Emit(CodeWriter writer, string registrationName)
    {
        var models = OpenApiJsonCodecModelAdapter.Create(_models);
        var emitter = new JsonCodecSourceEmitter(models);
        emitter.EmitCodecs(writer);
        writer.Line();
        emitter.EmitDeferredRegistration(writer, registrationName);
    }
}
