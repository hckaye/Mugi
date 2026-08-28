using System.Net;
using System.Text;

namespace Miya.IntegrationTests;

public sealed class StaticIntegrationTests
{
    private const int TestTimeoutMilliseconds = 10_000;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task KestrelServesFilesHeadValidatorsRangesAndMisses()
    {
        using var root = new TemporaryDirectory();
        var file = root.Write("hello.txt", "hello static");
        var app = new App();
        app.Static("/assets", new StaticOptions { Root = root.Path });

        await using var server = await StartAsync(app);
        using var client = CreateClient(server);
        using var get = await client.GetAsync("/assets/hello.txt");

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("hello static", await get.Content.ReadAsStringAsync());
        Assert.Equal(file.Length, get.Content.Headers.ContentLength);
        Assert.Equal("text/plain; charset=utf-8", get.Content.Headers.ContentType?.ToString());
        Assert.Equal("bytes", get.Headers.AcceptRanges.ToString());
        var etag = get.Headers.ETag!.Tag;
        var lastModified = get.Content.Headers.LastModified!.Value.ToString("R");

        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/assets/hello.txt"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(file.Length, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());

        using var notModifiedRequest = new HttpRequestMessage(HttpMethod.Get, "/assets/hello.txt");
        notModifiedRequest.Headers.TryAddWithoutValidation("If-None-Match", string.Concat("\"other\", ", etag));
        using var notModified = await client.SendAsync(notModifiedRequest);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Empty(await notModified.Content.ReadAsByteArrayAsync());
        Assert.Equal(etag, notModified.Headers.ETag!.Tag);
        Assert.Equal(lastModified, notModified.Content.Headers.LastModified!.Value.ToString("R"));

        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, "/assets/hello.txt");
        rangeRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(6, null);
        using var range = await client.SendAsync(rangeRequest);
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal("static", await range.Content.ReadAsStringAsync());
        Assert.Equal("bytes 6-11/12", range.Content.Headers.ContentRange!.ToString());
        Assert.Equal(6, range.Content.Headers.ContentLength);

        using var unsatisfiableRequest = new HttpRequestMessage(HttpMethod.Get, "/assets/hello.txt");
        unsatisfiableRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(99, 100);
        using var unsatisfiable = await client.SendAsync(unsatisfiableRequest);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, unsatisfiable.StatusCode);
        Assert.Equal("bytes */12", unsatisfiable.Content.Headers.ContentRange!.ToString());

        using var missing = await client.GetAsync("/assets/missing.txt");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("Not Found", await missing.Content.ReadAsStringAsync());
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task KestrelNegotiatesPrecompressedFilesAndServesUnicodeNames()
    {
        using var root = new TemporaryDirectory();
        root.Write("app.js", "plain");
        root.WriteBytes("app.js.br", Encoding.UTF8.GetBytes("brotli"));
        root.WriteBytes("app.js.gz", Encoding.UTF8.GetBytes("gzip"));
        root.Write("日本語.txt", "unicode");
        var app = new App();
        app.Static("/assets", new StaticOptions { Root = root.Path });

        await using var server = await StartAsync(app);
        using var client = CreateClient(server);

        using var preferredRequest = new HttpRequestMessage(HttpMethod.Get, "/assets/app.js");
        preferredRequest.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip;q=1, br;q=0.1");
        using var preferred = await client.SendAsync(preferredRequest);
        Assert.Equal(HttpStatusCode.OK, preferred.StatusCode);
        Assert.Equal("gzip", await preferred.Content.ReadAsStringAsync());
        Assert.Equal("gzip", preferred.Content.Headers.ContentEncoding.Single());
        Assert.Equal("Accept-Encoding", preferred.Headers.Vary.Single());
        Assert.Equal("text/javascript; charset=utf-8", preferred.Content.Headers.ContentType?.ToString());

        using var gzipRequest = new HttpRequestMessage(HttpMethod.Get, "/assets/app.js");
        gzipRequest.Headers.TryAddWithoutValidation("Accept-Encoding", "br;q=0, gzip;q=0.5");
        using var gzip = await client.SendAsync(gzipRequest);
        Assert.Equal(HttpStatusCode.OK, gzip.StatusCode);
        Assert.Equal("gzip", await gzip.Content.ReadAsStringAsync());
        Assert.Equal("gzip", gzip.Content.Headers.ContentEncoding.Single());

        using var unicode = await client.GetAsync("/assets/%E6%97%A5%E6%9C%AC%E8%AA%9E.txt");
        Assert.Equal(HttpStatusCode.OK, unicode.StatusCode);
        Assert.Equal("unicode", await unicode.Content.ReadAsStringAsync());
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task KestrelKeepsAnOpenedFileUsableWhenItsNameIsDeleted()
    {
        using var root = new TemporaryDirectory();
        var content = new byte[512 * 1024];
        for (var index = 0; index < content.Length; index++)
        {
            content[index] = (byte)(index % 251);
        }

        var filePath = root.WriteBytes("large.bin", content);
        var app = new App();
        app.Static("/assets", new StaticOptions { Root = root.Path });

        await using var server = await StartAsync(app);
        using var client = CreateClient(server);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/assets/large.bin");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        File.Delete(filePath);
        var received = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(content, received);
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

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("miya-static-");

        public string Path => _directory.FullName;

        public FileInfo Write(string relativePath, string value)
        {
            var path = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, value);
            return new FileInfo(path);
        }

        public string WriteBytes(string relativePath, byte[] value)
        {
            var path = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, value);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
