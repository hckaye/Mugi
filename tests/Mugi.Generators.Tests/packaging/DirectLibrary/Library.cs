using System.Buffers;
using System.Text;
using Mugi;
using Mugi.Json;

namespace DirectLibrary;

public sealed record DirectPayload(int Id, string Name);

public static class DirectEntry
{
    public static string Run()
    {
        var buffer = new ArrayBufferWriter<byte>();
        Json.Serialize(buffer, new DirectPayload(1, "direct"));
        var copy = Json.Deserialize<DirectPayload>(buffer.WrittenSpan)!;
        var app = new App();
        app.Get("/direct/:id", context => context.Text("ok"));
        app.Build();
        return Encoding.UTF8.GetString(buffer.WrittenSpan) + "|" + copy.Name;
    }
}
