using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Mugi.IntegrationTests;

public sealed class MultipartKestrelIntegrationTests
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);

    [Fact(Timeout = 20_000)]
    public async Task BufferedFormReceivesBinaryFileLargerThanSixtyFourKiB()
    {
        var payload = new byte[128 * 1024 + 37];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        FormFile? captured = null;
        var app = new App();
        app.Post("/upload", async context =>
        {
            var form = await context.Req.Form();
            captured = form.File("upload");
            await context.Text(captured?.Content.Length.ToString(CultureInfo.InvariantCulture) ?? "missing");
        });

        await using var server = await StartAsync(app);
        using var client = CreateClient(server);
        using var content = new MultipartFormDataContent("kestrel-boundary");
        using var file = new ByteArrayContent(payload);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "upload", "data.bin");
        using var response = await client.PostAsync("/upload", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(payload.Length.ToString(CultureInfo.InvariantCulture), await response.Content.ReadAsStringAsync());
        Assert.NotNull(captured);
        Assert.Equal("data.bin", captured.FileName);
        Assert.Equal("application/octet-stream", captured.ContentType);
        Assert.Equal(payload, captured.Content.ToArray());
    }

    [Fact(Timeout = 20_000)]
    public async Task ChunkedMultipartUploadIsParsedWithoutContentLength()
    {
        var app = new App();
        app.Post("/upload", async context =>
        {
            var form = await context.Req.Form();
            var file = form.File("upload");
            await context.Text(string.Concat(
                form.Get("field"),
                ":",
                file?.Content.Length.ToString(CultureInfo.InvariantCulture)));
        });

        await using var server = await StartAsync(app);
        await using var connection = await RawHttpConnection.ConnectAsync(server.Addresses[0]);
        await connection.WriteAsync(
            "POST /upload HTTP/1.1\r\n" +
            $"Host: {connection.HostHeader}\r\n" +
            "Content-Type: multipart/form-data; boundary=chunked-boundary\r\n" +
            "Transfer-Encoding: chunked\r\n" +
            "Connection: close\r\n\r\n");

        var body =
            "--chunked-boundary\r\n" +
            "Content-Disposition: form-data; name=\"field\"\r\n\r\n" +
            "hello\r\n" +
            "--chunked-boundary\r\n" +
            "Content-Disposition: form-data; name=\"upload\"; filename=\"x.bin\"\r\n" +
            "Content-Type: application/octet-stream\r\n\r\n" +
            new string('x', 70_000) +
            "\r\n--chunked-boundary--\r\n";
        var bytes = Encoding.ASCII.GetBytes(body);
        var offset = 0;
        var chunkSizes = new[] { 1, 2, 3, 5, 7, 1024, 8192 };
        var chunkIndex = 0;
        while (offset < bytes.Length)
        {
            var count = Math.Min(chunkSizes[chunkIndex++ % chunkSizes.Length], bytes.Length - offset);
            var chunk = Encoding.ASCII.GetString(bytes, offset, count);
            await connection.WriteAsync(string.Concat(
                count.ToString("X", CultureInfo.InvariantCulture),
                "\r\n",
                chunk,
                "\r\n"));
            offset += count;
        }

        await connection.WriteAsync("0\r\n\r\n");
        var response = await connection.ReadResponseAsync();

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("hello:70000", response.Body);
    }

    [Fact(Timeout = 20_000)]
    public async Task StreamingMultipartReadsLargeFileThroughKestrel()
    {
        const int payloadLength = 256 * 1024 + 13;
        var payload = new byte[payloadLength];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 239);
        }

        var app = new App();
        app.Post("/upload", async context =>
        {
            var multipart = await context.Req.Multipart();
            var part = await multipart.ReadNextAsync(context.Aborted);
            var count = 0L;
            if (part is not null)
            {
                while (true)
                {
                    var result = await part.Body.ReadAsync(context.Aborted);
                    count += result.Buffer.Length;
                    part.Body.AdvanceTo(result.Buffer.End);
                    if (result.IsCompleted)
                    {
                        break;
                    }
                }
            }

            await context.Text(count.ToString(CultureInfo.InvariantCulture));
        });

        await using var server = await StartAsync(app);
        using var client = CreateClient(server);
        using var content = new MultipartFormDataContent("stream-boundary");
        using var file = new ByteArrayContent(payload);
        content.Add(file, "upload", "large.bin");
        using var response = await client.PostAsync("/upload", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(payloadLength.ToString(CultureInfo.InvariantCulture), await response.Content.ReadAsStringAsync());
    }

    private static Task<Server> StartAsync(App app) => app.StartAsync(new AppOptions
    {
        Port = 0,
        ShutdownTimeout = TimeSpan.FromSeconds(2),
    });

    private static HttpClient CreateClient(Server server) => new()
    {
        BaseAddress = new Uri(server.Addresses[0]),
        Timeout = OperationTimeout,
    };
}
