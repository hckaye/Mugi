using System.Buffers;
using System.Text;
using Miya;
using Miya.Json;

Json.Include<Payload>();
var app = new App();
app.Get("/cli/:id", context => context.Text("ok"));
app.Build();
var buffer = new ArrayBufferWriter<byte>();
Json.Serialize(buffer, new Payload { Name = "cli", Count = 4 });
var copy = Json.Deserialize<Payload>(buffer.WrittenSpan)!;
Console.WriteLine(Encoding.UTF8.GetString(buffer.WrittenSpan));
Console.WriteLine($"{copy.Name}:{copy.Count}");

internal sealed class Payload
{
    public required string Name { get; init; }
    public int Count { get; init; }
}
