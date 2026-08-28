using System.Text.Json.Serialization;

namespace Mugi.LoadBench;

internal sealed record UserResponse(string Id, string Name);

internal sealed record EchoPayload(int Id, string Name, string Message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UserResponse))]
[JsonSerializable(typeof(EchoPayload))]
internal sealed partial class LoadBenchJsonContext : JsonSerializerContext;
