using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Miya.Tests;

public sealed class MultipartFormTests
{
    [Theory]
    [InlineData("multipart/form-data; boundary=test-boundary")]
    [InlineData("multipart/form-data; boundary=\"test-boundary\"")]
    [InlineData("Multipart/Form-Data; charset=UTF-8; boundary=\"test-boundary\"")]
    public async Task BuffersFieldsFilesAndDuplicateValues(string contentType)
    {
        var binary = new byte[] { 0, 1, 2, 255, 13, 10, 3 };
        var body = MultipartTestData.Build(
            "test-boundary",
            ("Content-Disposition: form-data; name=\"tag\"", "first"u8.ToArray()),
            ("Content-Disposition: form-data; name=\"tag\"", "second"u8.ToArray()),
            (
                "Content-Disposition: form-data; name=\"upload\"; filename=\"data.bin\"\r\n" +
                "Content-Type: application/custom",
                binary));
        FormData? captured = null;
        var app = CreateCapturingApp(form => captured = form);

        await using var response = await Send(app, body, contentType);

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.NotNull(captured);
        Assert.Equal(["first", "second"], captured.GetAll("tag"));
        var file = Assert.IsType<FormFile>(captured.File("upload"));
        Assert.Equal("data.bin", file.FileName);
        Assert.Equal("application/custom", file.ContentType);
        Assert.Equal(binary, file.Content.ToArray());
    }

    [Fact]
    public async Task ToleratesPreambleEpilogueAndEmptyParts()
    {
        var body = MultipartTestData.Build(
            "b",
            preamble: "preamble text\r\n",
            epilogue: "epilogue text",
            ("Content-Disposition: form-data; name=\"empty\"", []),
            ("Content-Disposition: form-data; name=\"file\"; filename=\"\"", []));
        FormData? captured = null;
        var app = CreateCapturingApp(form => captured = form);

        await using var response = await Send(app, body, "multipart/form-data; boundary=b");

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal(string.Empty, Assert.IsType<FormData>(captured).Get("empty"));
        var file = Assert.IsType<FormFile>(captured.File("file"));
        Assert.Equal(string.Empty, file.FileName);
        Assert.Empty(file.Content.ToArray());
        Assert.Equal("application/octet-stream", file.ContentType);
    }

    [Fact]
    public async Task EmptyMultipartContainsNoParts()
    {
        var body = MultipartTestData.Build("empty");
        FormData? captured = null;
        var app = CreateCapturingApp(form => captured = form);

        await using var response = await Send(app, body, "multipart/form-data; boundary=empty");

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Empty(Assert.IsType<FormData>(captured).Fields);
        Assert.Empty(captured.Files);
    }

    [Fact]
    public async Task BoundaryLikeBinaryBytesRemainInFile()
    {
        var binary = Encoding.ASCII.GetBytes(
            "before\r\n--boundaryX\r\nmiddle\r\n--boundary--X\r\nafter");
        var body = MultipartTestData.Build(
            "boundary",
            ("Content-Disposition: form-data; name=\"file\"; filename=\"x.bin\"", binary));
        FormData? captured = null;
        var app = CreateCapturingApp(form => captured = form);

        await using var response = await Send(app, body, "multipart/form-data; boundary=boundary");

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal(binary, Assert.IsType<FormFile>(Assert.IsType<FormData>(captured).File("file")).Content.ToArray());
    }

    [Fact]
    public async Task FilenameStarWinsAndPathComponentsAreRemoved()
    {
        var body = MultipartTestData.Build(
            "b",
            (
                "Content-Disposition: form-data; name=\"file\"; " +
                "filename=\"C:\\fakepath\\old.txt\"; " +
                "filename*=UTF-8''folder%2F%E3%81%BF%E3%82%84.txt",
                "content"u8.ToArray()),
            ("Content-Disposition: form-data; name=\"other\"; filename=\"C:\\fakepath\\name.txt\"", []),
            (
                "Content-Disposition: form-data; name=\"ordered\"; " +
                "filename*=UTF-8''new.txt; filename=\"old.txt\"",
                []));
        FormData? captured = null;
        var app = CreateCapturingApp(form => captured = form);

        await using var response = await Send(app, body, "multipart/form-data; boundary=b");

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal("みや.txt", Assert.IsType<FormFile>(Assert.IsType<FormData>(captured).File("file")).FileName);
        Assert.Equal("name.txt", Assert.IsType<FormFile>(captured.File("other")).FileName);
        Assert.Equal("new.txt", Assert.IsType<FormFile>(captured.File("ordered")).FileName);
    }

    [Fact]
    public async Task AcceptsQuotedBoundaryCharactersAndBoundaryLinePadding()
    {
        var body =
            "--a:b \t\r\n" +
            "Content-Disposition: form-data; name=\"field\"\r\n\r\n" +
            "value\r\n" +
            "--a:b-- \t\r\n" +
            "epilogue";
        FormData? captured = null;
        var app = CreateCapturingApp(form => captured = form);

        await using var response = await Send(
            app,
            Encoding.ASCII.GetBytes(body),
            "multipart/form-data; boundary=\"a:b\"");

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal("value", Assert.IsType<FormData>(captured).Get("field"));
    }

    [Theory]
    [MemberData(nameof(MalformedBodies))]
    public async Task MalformedMultipartReturnsBadRequest(byte[] body, string contentType)
    {
        var app = CreateParsingApp();

        await using var response = await Send(app, body, contentType);

        Assert.Equal(StatusCodes.Status400BadRequest, response.Response.StatusCode);
        Assert.Equal("Bad Request", response.BodyText);
    }

    [Fact]
    public async Task RejectsMoreThanSixteenHeaders()
    {
        var headers = new StringBuilder("Content-Disposition: form-data; name=\"field\"");
        for (var i = 0; i < 16; i++)
        {
            headers.Append("\r\nX-");
            headers.Append(i.ToString(CultureInfo.InvariantCulture));
            headers.Append(": value");
        }

        var body = MultipartTestData.Build("b", (headers.ToString(), "value"u8.ToArray()));
        var app = CreateParsingApp();

        await using var response = await Send(app, body, "multipart/form-data; boundary=b");

        Assert.Equal(StatusCodes.Status400BadRequest, response.Response.StatusCode);
    }

    [Fact]
    public async Task RejectsHeaderLargerThanSixteenKiB()
    {
        var headers = string.Concat(
            "Content-Disposition: form-data; name=\"field\"\r\nX-Large: ",
            new string('a', 16 * 1024));
        var body = MultipartTestData.Build("b", (headers, "value"u8.ToArray()));
        var app = CreateParsingApp();

        await using var response = await Send(app, body, "multipart/form-data; boundary=b");

        Assert.Equal(StatusCodes.Status400BadRequest, response.Response.StatusCode);
    }

    [Fact]
    public async Task InvalidFieldUtf8ReturnsBadRequest()
    {
        var body = MultipartTestData.Build(
            "b",
            ("Content-Disposition: form-data; name=\"field\"", new byte[] { 0xc3, 0x28 }));
        var app = CreateParsingApp();

        await using var response = await Send(app, body, "multipart/form-data; boundary=b");

        Assert.Equal(StatusCodes.Status400BadRequest, response.Response.StatusCode);
    }

    [Fact]
    public async Task NestedMultipartReturnsBadRequest()
    {
        var body = MultipartTestData.Build(
            "b",
            (
                "Content-Disposition: form-data; name=\"field\"\r\n" +
                "Content-Type: multipart/mixed; boundary=inner",
                []));
        var app = CreateParsingApp();

        await using var response = await Send(app, body, "multipart/form-data; boundary=b");

        Assert.Equal(StatusCodes.Status400BadRequest, response.Response.StatusCode);
    }

    [Fact]
    public async Task FieldAndFileLimitsAreSeparate()
    {
        var body = MultipartTestData.Build(
            "b",
            ("Content-Disposition: form-data; name=\"field\"", "one"u8.ToArray()),
            ("Content-Disposition: form-data; name=\"file\"; filename=\"a.txt\"", []));
        FormData? captured = null;
        var app = CreateCapturingApp(form => captured = form);

        await using var response = await Send(
            app,
            body,
            "multipart/form-data; boundary=b",
            new AppOptions { MaxFormFields = 1 });

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Single(Assert.IsType<FormData>(captured).Fields);
        Assert.Single(captured.Files);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FieldOrFileCountLimitReturnsBadRequest(bool files)
    {
        var firstDisposition = files
            ? "Content-Disposition: form-data; name=\"a\"; filename=\"a.txt\""
            : "Content-Disposition: form-data; name=\"a\"";
        var secondDisposition = files
            ? "Content-Disposition: form-data; name=\"b\"; filename=\"b.txt\""
            : "Content-Disposition: form-data; name=\"b\"";
        var body = MultipartTestData.Build(
            "b",
            (firstDisposition, []),
            (secondDisposition, []));
        var app = CreateParsingApp();

        await using var response = await Send(
            app,
            body,
            "multipart/form-data; boundary=b",
            new AppOptions { MaxFormFields = 1 });

        Assert.Equal(StatusCodes.Status400BadRequest, response.Response.StatusCode);
    }

    [Fact(Timeout = 10_000)]
    public async Task FieldCountLimitDrainsLargeRejectedPartBeforeReturningBadRequest()
    {
        var body = MultipartTestData.Build(
            "b",
            ("Content-Disposition: form-data; name=\"first\"", []),
            ("Content-Disposition: form-data; name=\"second\"", new byte[256 * 1024]));
        var source = new DribblePipeReader(body, 8 * 1024);
        var app = CreateParsingApp();
        await using var exchange = TestExchange.Create(
            method: "POST",
            body: body,
            headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "multipart/form-data; boundary=b",
                ["Content-Length"] = body.Length.ToString(CultureInfo.InvariantCulture),
            });
        exchange.Features.Set<IRequestBodyPipeFeature>(new RequestBodyPipeFeature(source));

        await app.ExecuteAsync(exchange.Features, new AppOptions { MaxFormFields = 1 });

        Assert.Equal(StatusCodes.Status400BadRequest, exchange.Response.StatusCode);
        Assert.Equal("Bad Request", exchange.BodyText);
        Assert.Equal(body.Length, source.ConsumedBytes);
        await source.CompleteAsync();
    }

    [Fact]
    public async Task PartCountLimitReturnsBadRequest()
    {
        var body = MultipartTestData.Build(
            "b",
            ("Content-Disposition: form-data; name=\"a\"", []),
            ("Content-Disposition: form-data; name=\"b\"", []));
        var app = CreateParsingApp();

        await using var response = await Send(
            app,
            body,
            "multipart/form-data; boundary=b",
            new AppOptions { MaxMultipartParts = 1 });

        Assert.Equal(StatusCodes.Status400BadRequest, response.Response.StatusCode);
    }

    [Fact]
    public async Task FormBodyLimitReturnsPayloadTooLarge()
    {
        var body = MultipartTestData.Build(
            "b",
            ("Content-Disposition: form-data; name=\"field\"", "value"u8.ToArray()));
        var app = CreateParsingApp();

        await using var response = await Send(
            app,
            body,
            "multipart/form-data; boundary=b",
            new AppOptions { MaxFormBodyBytes = body.Length - 1 });

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, response.Response.StatusCode);
    }

    [Theory]
    [InlineData("multipart/form-data")]
    [InlineData("multipart/form-data; boundary=")]
    [InlineData("multipart/form-data; boundary=\"unterminated")]
    [InlineData("multipart/form-data; boundary=bad@value")]
    [InlineData("multipart/form-data; boundary=bad:value")]
    public async Task InvalidBoundaryParameterReturnsBadRequest(string contentType)
    {
        var app = CreateParsingApp();

        await using var response = await Send(app, [], contentType);

        Assert.Equal(StatusCodes.Status400BadRequest, response.Response.StatusCode);
    }

    [Fact(Timeout = 10_000)]
    public async Task DeterministicMutationsNeverHangOrEscapeAsParserImplementationExceptions()
    {
        var valid = MultipartTestData.Build(
            "fuzz-boundary",
            ("Content-Disposition: form-data; name=\"field\"", "value"u8.ToArray()),
            ("Content-Disposition: form-data; name=\"file\"; filename=\"x.bin\"", new byte[] { 0, 1, 2, 3 }));
        Exception? captured = null;
        var app = new App();
        app.Post("/", async context =>
        {
            await context.Req.Form();
            await context.Text("ok");
        });
        app.OnError(async (context, exception) =>
        {
            captured = exception;
            context.Status(exception is FormException form ? form.StatusCode : 500);
            await context.Text("error");
        });
        var random = new Random(7319);

        for (var iteration = 0; iteration < 100; iteration++)
        {
            var mutated = valid.ToArray();
            var mutationCount = random.Next(1, 5);
            for (var mutation = 0; mutation < mutationCount; mutation++)
            {
                mutated[random.Next(mutated.Length)] = (byte)random.Next(256);
            }

            captured = null;
            await using var response = await Send(
                app,
                mutated,
                "multipart/form-data; boundary=fuzz-boundary");
            if (captured is not null)
            {
                Assert.IsType<FormException>(captured);
            }
        }
    }

    public static TheoryData<byte[], string> MalformedBodies => new()
    {
        {
            MultipartTestData.Build("b", ("Content-Disposition: form-data", [])),
            "multipart/form-data; boundary=b"
        },
        {
            MultipartTestData.Build("b", ("Content-Disposition: attachment; name=\"field\"", [])),
            "multipart/form-data; boundary=b"
        },
        {
            MultipartTestData.Build("b", ("Content-Disposition: form-data; name=\"field\"\r\n folded", [])),
            "multipart/form-data; boundary=b"
        },
        {
            MultipartTestData.Build("b", ("Content-Disposition: form-data; name=\"field\"\r\nX-Test: valué", [])),
            "multipart/form-data; boundary=b"
        },
        {
            "--b\r\nContent-Disposition: form-data; name=\"field\"\r\n\r\ntruncated"u8.ToArray(),
            "multipart/form-data; boundary=b"
        },
        {
            "--b\nContent-Disposition: form-data; name=\"field\"\n\nvalue\n--b--\n"u8.ToArray(),
            "multipart/form-data; boundary=b"
        },
        {
            MultipartTestData.Build("b", ("Content-Disposition: form-data; name=\"field\"; filename*=ISO-8859-1''x", [])),
            "multipart/form-data; boundary=b"
        },
        {
            MultipartTestData.Build("b", ("Content-Disposition: form-data; name=\"field\"; filename*=UTF-8''%GG", [])),
            "multipart/form-data; boundary=b"
        },
    };

    private static App CreateParsingApp() => CreateCapturingApp(static _ => { });

    private static App CreateCapturingApp(Action<FormData> capture)
    {
        var app = new App();
        app.Post("/", async context =>
        {
            capture(await context.Req.Form());
            await context.Text("ok");
        });
        return app;
    }

    private static Task<TestExchange> Send(
        App app,
        byte[] body,
        string contentType,
        AppOptions? options = null) =>
        TestApp.Send(
            app,
            method: "POST",
            body: body,
            headers: new Dictionary<string, string>
            {
                ["Content-Type"] = contentType,
                ["Content-Length"] = body.Length.ToString(CultureInfo.InvariantCulture),
            },
            options: options);

    private sealed class RequestBodyPipeFeature(PipeReader reader) : IRequestBodyPipeFeature
    {
        public PipeReader Reader { get; } = reader;
    }
}

internal static class MultipartTestData
{
    public static byte[] Build(
        string boundary,
        params (string Headers, byte[] Body)[] parts) =>
        Build(boundary, string.Empty, string.Empty, parts);

    public static byte[] Build(
        string boundary,
        string preamble,
        string epilogue,
        params (string Headers, byte[] Body)[] parts)
    {
        using var stream = new MemoryStream();
        WriteAscii(stream, preamble);
        for (var i = 0; i < parts.Length; i++)
        {
            WriteAscii(stream, $"--{boundary}\r\n");
            WriteAscii(stream, parts[i].Headers);
            WriteAscii(stream, "\r\n\r\n");
            stream.Write(parts[i].Body);
            WriteAscii(stream, "\r\n");
        }

        WriteAscii(stream, $"--{boundary}--\r\n");
        WriteAscii(stream, epilogue);
        return stream.ToArray();
    }

    private static void WriteAscii(Stream stream, string value) =>
        stream.Write(Encoding.UTF8.GetBytes(value));
}
