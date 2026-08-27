using System.Buffers;
using System.Text;
using DirectLibrary;
using Miya;
using Miya.Json;

var buffer = new ArrayBufferWriter<byte>();
MiyaJson.Serialize(buffer, new TransitivePayload(2, "transitive"));
var copy = MiyaJson.Deserialize<TransitivePayload>(buffer.WrittenSpan)!;
var app = new App();
app.Post("/transitive", context => context.Text("ok"));
app.Build();

Console.WriteLine(DirectEntry.Run());
Console.WriteLine(Encoding.UTF8.GetString(buffer.WrittenSpan) + "|" + copy.Name);

internal sealed record TransitivePayload(int Id, string Name);
