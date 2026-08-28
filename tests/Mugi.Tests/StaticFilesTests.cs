using System.Text;
using Microsoft.AspNetCore.Http;

namespace Mugi.Tests;

public sealed class StaticFilesTests
{
    [Fact]
    public async Task ServesFilesWithValidatorsContentTypeAndCacheControl()
    {
        using var root = new TemporaryDirectory();
        var file = root.Write("site.html", "hello static");
        var app = new App();
        app.Static("/assets", new StaticOptions
        {
            Root = root.Path,
            CacheControl = "public, max-age=60",
        });

        await using var response = await TestApp.Send(app, path: "/assets/site.html");

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal("hello static", response.BodyText);
        Assert.Equal("text/html; charset=utf-8", response.Response.Headers.ContentType.ToString());
        Assert.Equal(file.Length.ToString(), response.Response.Headers.ContentLength.ToString());
        Assert.Equal("public, max-age=60", response.Response.Headers.CacheControl.ToString());
        Assert.Equal("bytes", response.Response.Headers.AcceptRanges.ToString());
        Assert.Matches("^\"[0-9a-f]+-[0-9a-f]+\"$", response.Response.Headers.ETag.ToString());
        Assert.False(string.IsNullOrEmpty(response.Response.Headers.LastModified.ToString()));
    }

    [Fact]
    public async Task PrefixAndTrailingDirectoryPathsUseTheIndexFile()
    {
        using var root = new TemporaryDirectory();
        root.Write("index.html", "root index");
        root.Write("docs/index.html", "docs index");
        var app = new App();
        app.Static("/assets/", new StaticOptions { Root = root.Path });

        await using var prefix = await TestApp.Send(app, path: "/assets");
        await using var prefixSlash = await TestApp.Send(app, path: "/assets/");
        await using var directorySlash = await TestApp.Send(app, path: "/assets/docs/");
        await using var directory = await TestApp.Send(app, path: "/assets/docs");

        Assert.Equal("root index", prefix.BodyText);
        Assert.Equal("root index", prefixSlash.BodyText);
        Assert.Equal("docs index", directorySlash.BodyText);
        Assert.Equal(StatusCodes.Status404NotFound, directory.Response.StatusCode);
    }

    [Fact]
    public async Task EmptyIndexDisablesDirectoryServing()
    {
        using var root = new TemporaryDirectory();
        root.Write("index.html", "index");
        var app = new App();
        app.Static("/assets", new StaticOptions { Root = root.Path, Index = "" });

        await using var response = await TestApp.Send(app, path: "/assets");
        await using var slash = await TestApp.Send(app, path: "/assets/");

        Assert.Equal(StatusCodes.Status404NotFound, response.Response.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, slash.Response.StatusCode);
    }

    [Fact]
    public async Task MissingAndRejectedPathsUseTheConfiguredNotFoundHandler()
    {
        using var root = new TemporaryDirectory();
        root.Write("inside.txt", "inside");
        var outside = System.IO.Path.Combine(root.ParentPath, string.Concat(root.Name, "-outside-sentinel.txt"));
        var outsideName = System.IO.Path.GetFileName(outside);
        File.WriteAllText(outside, "outside");
        try
        {
            var app = new App();
            app.NotFound(static c =>
            {
                c.Status(StatusCodes.Status404NotFound);
                return c.Text("custom not found");
            });
            app.Static("/assets", new StaticOptions { Root = root.Path });

            await using var missing = await TestApp.Send(app, path: "/assets/missing.txt");
            await using var traversal = await TestApp.Send(app, path: string.Concat("/assets/..%2f", outsideName));
            await using var encodedTraversal = await TestApp.Send(app, path: string.Concat("/assets/%2e%2e/", outsideName));
            await using var doubleTraversal = await TestApp.Send(app, path: "/assets/..%2f..%2foutside-sentinel.txt");
            await using var dotOnly = await TestApp.Send(app, path: "/assets/....//outside-sentinel.txt");
            await using var rooted = await TestApp.Send(app, path: "/assets/%2Fetc/passwd");
            await using var backslash = await TestApp.Send(app, path: "/assets/..%5coutside-sentinel.txt");
            await using var drive = await TestApp.Send(app, path: "/assets/C:%5coutside-sentinel.txt");
            await using var unc = await TestApp.Send(app, path: "/assets/%5c%5cserver%5cshare%5coutside-sentinel.txt");
            await using var nul = await TestApp.Send(app, path: "/assets/inside.txt\0outside");
            await using var finalDotDot = await TestApp.Send(app, path: "/assets/..");
            await using var overlong = await TestApp.Send(app, path: string.Concat("/assets/", new string('a', 100_000)));

            foreach (var response in new[]
                     {
                         missing,
                         traversal,
                         encodedTraversal,
                         doubleTraversal,
                         dotOnly,
                         rooted,
                         backslash,
                         drive,
                         unc,
                         nul,
                         finalDotDot,
                         overlong,
                     })
            {
                Assert.Equal(StatusCodes.Status404NotFound, response.Response.StatusCode);
                Assert.Equal("custom not found", response.BodyText);
            }

            Assert.Equal("outside", File.ReadAllText(outside));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task EncodedSlashCanAddressAnInRootFileButCannotEscapeIt()
    {
        using var root = new TemporaryDirectory();
        root.Write("nested/file.txt", "safe nested");
        var app = new App();
        app.Static("/assets", new StaticOptions { Root = root.Path });

        await using var safe = await TestApp.Send(app, path: "/assets/nested%2ffile.txt");
        await using var escaped = await TestApp.Send(app, path: "/assets/nested%2f..%2f..%2foutside.txt");

        Assert.Equal(StatusCodes.Status200OK, safe.Response.StatusCode);
        Assert.Equal("safe nested", safe.BodyText);
        Assert.Equal(StatusCodes.Status404NotFound, escaped.Response.StatusCode);
    }

    [Fact]
    public async Task HeadUsesTheGetRouteAndKeepsTheFileLength()
    {
        using var root = new TemporaryDirectory();
        root.Write("hello.txt", "hello");
        var app = new App();
        app.Static("/assets", new StaticOptions { Root = root.Path });

        await using var response = await TestApp.Send(app, method: "HEAD", path: "/assets/hello.txt");

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Empty(response.BodyText);
        Assert.Equal("5", response.Response.Headers.ContentLength.ToString());
        Assert.Equal("text/plain; charset=utf-8", response.Response.Headers.ContentType.ToString());
    }

    [Fact]
    public async Task IfNoneMatchIsListAwareAndTakesPrecedenceOverIfModifiedSince()
    {
        using var root = new TemporaryDirectory();
        root.Write("hello.txt", "hello");
        var app = new App();
        app.Static("/assets", new StaticOptions { Root = root.Path, CacheControl = "max-age=10" });

        await using var first = await TestApp.Send(app, path: "/assets/hello.txt");
        var etag = first.Response.Headers.ETag.ToString();
        var lastModified = first.Response.Headers.LastModified.ToString();
        await first.DisposeAsync();

        await using var notModified = await TestApp.Send(
            app,
            path: "/assets/hello.txt",
            headers: new Dictionary<string, string>
            {
                ["If-None-Match"] = string.Concat("\"other\", W/", etag),
                ["If-Modified-Since"] = "Thu, 01 Jan 1970 00:00:00 GMT",
            });

        await using var precedence = await TestApp.Send(
            app,
            path: "/assets/hello.txt",
            headers: new Dictionary<string, string>
            {
                ["If-None-Match"] = "\"other\"",
                ["If-Modified-Since"] = lastModified,
            });

        await using var star = await TestApp.Send(
            app,
            path: "/assets/hello.txt",
            headers: new Dictionary<string, string> { ["If-None-Match"] = "*" });

        Assert.Equal(StatusCodes.Status304NotModified, notModified.Response.StatusCode);
        Assert.Empty(notModified.BodyText);
        Assert.Equal(etag, notModified.Response.Headers.ETag.ToString());
        Assert.Equal("max-age=10", notModified.Response.Headers.CacheControl.ToString());
        Assert.Equal(StatusCodes.Status200OK, precedence.Response.StatusCode);
        Assert.Equal("hello", precedence.BodyText);
        Assert.Equal(StatusCodes.Status304NotModified, star.Response.StatusCode);
    }

    [Fact]
    public async Task IfModifiedSinceReturns304AndAStaleDateReturnsTheBody()
    {
        using var root = new TemporaryDirectory();
        root.Write("hello.txt", "hello");
        var app = new App();
        app.Static("/assets", new StaticOptions { Root = root.Path });

        await using var first = await TestApp.Send(app, path: "/assets/hello.txt");
        var lastModified = first.Response.Headers.LastModified.ToString();
        await first.DisposeAsync();

        await using var current = await TestApp.Send(
            app,
            path: "/assets/hello.txt",
            headers: new Dictionary<string, string> { ["If-Modified-Since"] = lastModified });
        await using var stale = await TestApp.Send(
            app,
            path: "/assets/hello.txt",
            headers: new Dictionary<string, string> { ["If-Modified-Since"] = "Thu, 01 Jan 1970 00:00:00 GMT" });

        Assert.Equal(StatusCodes.Status304NotModified, current.Response.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, stale.Response.StatusCode);
        Assert.Equal("hello", stale.BodyText);
    }

    [Theory]
    [InlineData("bytes=0-4", "hello", "bytes 0-4/12")]
    [InlineData("bytes=6-", "static", "bytes 6-11/12")]
    [InlineData("bytes=-5", "tatic", "bytes 7-11/12")]
    public async Task ServesSingleByteRanges(string range, string expectedBody, string expectedContentRange)
    {
        using var root = new TemporaryDirectory();
        root.Write("hello.txt", "hello static");
        var app = new App();
        app.Static("/assets", new StaticOptions { Root = root.Path });

        await using var response = await TestApp.Send(
            app,
            path: "/assets/hello.txt",
            headers: new Dictionary<string, string> { ["Range"] = range });

        Assert.Equal(StatusCodes.Status206PartialContent, response.Response.StatusCode);
        Assert.Equal(expectedBody, response.BodyText);
        Assert.Equal(expectedContentRange, response.Response.Headers.ContentRange.ToString());
        Assert.Equal(expectedBody.Length.ToString(), response.Response.Headers.ContentLength.ToString());
    }

    [Fact]
    public async Task UnsatisfiableAndMultipleRangesFallBackAsSpecified()
    {
        using var root = new TemporaryDirectory();
        root.Write("hello.txt", "hello static");
        var app = new App();
        app.Static("/assets", new StaticOptions { Root = root.Path });

        await using var unsatisfiable = await TestApp.Send(
            app,
            path: "/assets/hello.txt",
            headers: new Dictionary<string, string> { ["Range"] = "bytes=99-100" });
        await using var multiple = await TestApp.Send(
            app,
            path: "/assets/hello.txt",
            headers: new Dictionary<string, string> { ["Range"] = "bytes=0-1,4-5" });
        await using var badIfRange = await TestApp.Send(
            app,
            path: "/assets/hello.txt",
            headers: new Dictionary<string, string>
            {
                ["Range"] = "bytes=0-1",
                ["If-Range"] = "\"does-not-match\"",
            });
        await using var rangeOn304 = await TestApp.Send(
            app,
            path: "/assets/hello.txt",
            headers: new Dictionary<string, string>
            {
                ["Range"] = "bytes=99-100",
                ["If-None-Match"] = "*",
            });

        Assert.Equal(StatusCodes.Status416RangeNotSatisfiable, unsatisfiable.Response.StatusCode);
        Assert.Equal("bytes */12", unsatisfiable.Response.Headers.ContentRange.ToString());
        Assert.Empty(unsatisfiable.BodyText);
        Assert.Equal(StatusCodes.Status200OK, multiple.Response.StatusCode);
        Assert.Equal("hello static", multiple.BodyText);
        Assert.Equal(StatusCodes.Status200OK, badIfRange.Response.StatusCode);
        Assert.Equal("hello static", badIfRange.BodyText);
        Assert.Equal(StatusCodes.Status304NotModified, rangeOn304.Response.StatusCode);
    }

    [Fact]
    public async Task PrecompressedFilesHonorZeroQualityValues()
    {
        using var root = new TemporaryDirectory();
        root.Write("app.js", "plain");
        root.WriteBytes("app.js.br", Encoding.UTF8.GetBytes("brotli"));
        root.WriteBytes("app.js.gz", Encoding.UTF8.GetBytes("gzip"));
        var app = new App();
        app.Static("/assets", new StaticOptions { Root = root.Path });

        await using var gzipOnly = await TestApp.Send(
            app,
            path: "/assets/app.js",
            headers: new Dictionary<string, string> { ["Accept-Encoding"] = "br;q=0, gzip;q=0.5" });
        await using var plain = await TestApp.Send(
            app,
            path: "/assets/app.js",
            headers: new Dictionary<string, string> { ["Accept-Encoding"] = "br;q=0, gzip;q=0" });

        Assert.Equal("gzip", gzipOnly.BodyText);
        Assert.Equal("gzip", gzipOnly.Response.Headers.ContentEncoding.ToString());
        Assert.Equal("Accept-Encoding", gzipOnly.Response.Headers.Vary.ToString());
        Assert.Equal("text/javascript; charset=utf-8", gzipOnly.Response.Headers.ContentType.ToString());
        Assert.Equal("plain", plain.BodyText);
        Assert.False(plain.Response.Headers.ContainsKey("Content-Encoding"));
    }

    [Theory]
    [InlineData("gzip;q=1, br;q=0.1", "gzip", "gzip")]
    [InlineData("gzip;q=0.5, br;q=0.5", "br", "brotli")]
    public async Task PrecompressedFilesUseQualityBeforeServerPreference(
        string acceptEncoding,
        string expectedEncoding,
        string expectedBody)
    {
        using var root = new TemporaryDirectory();
        root.Write("app.js", "plain");
        root.WriteBytes("app.js.br", Encoding.UTF8.GetBytes("brotli"));
        root.WriteBytes("app.js.gz", Encoding.UTF8.GetBytes("gzip"));
        var app = new App();
        app.Static("/assets", new StaticOptions { Root = root.Path });

        await using var response = await TestApp.Send(
            app,
            path: "/assets/app.js",
            headers: new Dictionary<string, string> { ["Accept-Encoding"] = acceptEncoding });

        Assert.Equal(expectedEncoding, response.Response.Headers.ContentEncoding.ToString());
        Assert.Equal(expectedBody, response.BodyText);
    }

    [Fact]
    public async Task SymlinksCannotEscapeTheStaticRoot()
    {
        using var root = new TemporaryDirectory();
        root.Write("normal.txt", "normal");
        root.Write("inside.txt", "inside");
        var outsidePath = System.IO.Path.Combine(root.ParentPath, string.Concat(root.Name, "-outside.txt"));
        File.WriteAllText(outsidePath, "outside sentinel");
        var outsideLink = System.IO.Path.Combine(root.Path, "outside-link.txt");
        var insideLink = System.IO.Path.Combine(root.Path, "inside-link.txt");
        try
        {
            File.CreateSymbolicLink(outsideLink, outsidePath);
            File.CreateSymbolicLink(insideLink, System.IO.Path.Combine(root.Path, "inside.txt"));

            var app = new App();
            app.Static("/assets", new StaticOptions { Root = root.Path });

            await using var escaped = await TestApp.Send(app, path: "/assets/outside-link.txt");
            await using var normal = await TestApp.Send(app, path: "/assets/normal.txt");
            await using var linkedInside = await TestApp.Send(app, path: "/assets/inside-link.txt");

            Assert.Equal(StatusCodes.Status404NotFound, escaped.Response.StatusCode);
            Assert.DoesNotContain("outside sentinel", escaped.BodyText, StringComparison.Ordinal);
            Assert.Equal("normal", normal.BodyText);
            Assert.Equal("inside", linkedInside.BodyText);
        }
        finally
        {
            File.Delete(outsidePath);
        }
    }

    [Theory]
    [InlineData("file.html", "text/html; charset=utf-8")]
    [InlineData("file.css", "text/css; charset=utf-8")]
    [InlineData("file.json", "application/json; charset=utf-8")]
    [InlineData("file.svg", "image/svg+xml; charset=utf-8")]
    [InlineData("file.xml", "application/xml; charset=utf-8")]
    [InlineData("file.png", "image/png")]
    [InlineData("file.unknown", "application/octet-stream")]
    public async Task UsesTheStaticContentTypeMap(string fileName, string contentType)
    {
        using var root = new TemporaryDirectory();
        root.Write(fileName, "data");
        var app = new App();
        app.Static("/assets", new StaticOptions { Root = root.Path });

        await using var response = await TestApp.Send(app, path: string.Concat("/assets/", fileName));

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal(contentType, response.Response.Headers.ContentType.ToString());
    }

    [Fact]
    public async Task UnicodeFileNamesAreServed()
    {
        using var root = new TemporaryDirectory();
        root.Write("日本語.txt", "unicode");
        var app = new App();
        app.Static("/assets", new StaticOptions { Root = root.Path });

        await using var response = await TestApp.Send(app, path: "/assets/日本語.txt");

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal("unicode", response.BodyText);
    }

    [Fact]
    public async Task FileDeletedBeforeTheOpenBecomes404()
    {
        using var root = new TemporaryDirectory();
        root.Write("gone.txt", "gone");
        var app = new App();
        app.Static("/assets", new StaticOptions { Root = root.Path });
        File.Delete(System.IO.Path.Combine(root.Path, "gone.txt"));

        await using var response = await TestApp.Send(app, path: "/assets/gone.txt");

        Assert.Equal(StatusCodes.Status404NotFound, response.Response.StatusCode);
        Assert.Equal("Not Found", response.BodyText);
    }

    [Fact]
    public void RequiresExactlyOneSource()
    {
        var app = new App();
        Assert.Throws<ArgumentException>(() => app.Static("/assets", new StaticOptions()));
        Assert.Throws<ArgumentException>(() => app.Static("/assets", new StaticOptions
        {
            Root = System.IO.Path.GetTempPath(),
            Source = StaticSource.Embedded(typeof(StaticFilesTests).Assembly, "StaticFixture"),
        }));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("mugi-static-");

        public string Name => _directory.Name;

        public string Path => _directory.FullName;

        public string ParentPath => _directory.Parent!.FullName;

        public FileInfo Write(string relativePath, string content)
        {
            var path = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return new FileInfo(path);
        }

        public void WriteBytes(string relativePath, byte[] content)
        {
            var path = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
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
