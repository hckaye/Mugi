using System.Buffers;
using System.IO.Pipelines;
using System.Text;

namespace Mugi.Tests;

public sealed class MultipartStreamingTests
{
    [Fact]
    public async Task ReadsPartsHeadersAndBodiesInOrder()
    {
        var input = MultipartTestData.Build(
            "b",
            (
                "Content-Disposition: form-data; name=\"field\"\r\n" +
                "X-Custom: first",
                "value"u8.ToArray()),
            (
                "Content-Disposition: form-data; name=\"file\"; filename=\"data.bin\"\r\n" +
                "Content-Type: application/custom",
                new byte[] { 0, 1, 255 }));
        var reader = CreateReader(input, "b");

        var field = Assert.IsType<MultipartPart>(await reader.ReadNextAsync());
        Assert.Equal("field", field.Name);
        Assert.Equal(string.Empty, field.FileName);
        Assert.Equal("application/octet-stream", field.ContentType);
        Assert.Equal("first", field.Header("x-custom"));
        Assert.Equal("value"u8.ToArray(), await ReadAll(field.Body));

        var file = Assert.IsType<MultipartPart>(await reader.ReadNextAsync());
        Assert.Equal("file", file.Name);
        Assert.Equal("data.bin", file.FileName);
        Assert.Equal("application/custom", file.ContentType);
        Assert.Equal(new byte[] { 0, 1, 255 }, await ReadAll(file.Body));
        Assert.Null(await reader.ReadNextAsync());
        Assert.Null(await reader.ReadNextAsync());
    }

    [Fact]
    public async Task ReadNextAutoDrainsUnreadPart()
    {
        var input = MultipartTestData.Build(
            "b",
            ("Content-Disposition: form-data; name=\"first\"", new byte[256 * 1024]),
            ("Content-Disposition: form-data; name=\"second\"", "kept"u8.ToArray()));
        var reader = CreateReader(input, "b");

        var first = Assert.IsType<MultipartPart>(await reader.ReadNextAsync());
        var firstRead = await first.Body.ReadAsync();
        Assert.NotEmpty(firstRead.Buffer.ToArray());

        var second = Assert.IsType<MultipartPart>(await reader.ReadNextAsync());

        Assert.Equal("second", second.Name);
        Assert.Equal("kept"u8.ToArray(), await ReadAll(second.Body));
        Assert.Null(await reader.ReadNextAsync());
    }

    [Fact]
    public async Task ExplicitlyCompletingPartSkipsItsRemainingBody()
    {
        var input = MultipartTestData.Build(
            "b",
            ("Content-Disposition: form-data; name=\"first\"", new byte[256 * 1024]),
            ("Content-Disposition: form-data; name=\"second\"", "kept"u8.ToArray()));
        var reader = CreateReader(input, "b");

        var first = Assert.IsType<MultipartPart>(await reader.ReadNextAsync());
        await first.Body.CompleteAsync();
        var second = Assert.IsType<MultipartPart>(await reader.ReadNextAsync());

        Assert.Equal("second", second.Name);
        Assert.Equal("kept"u8.ToArray(), await ReadAll(second.Body));
        Assert.Null(await reader.ReadNextAsync());
    }

    [Fact(Timeout = 10_000)]
    public async Task DisposingReaderCompletesAndStopsLargeUnreadPart()
    {
        var input = MultipartTestData.Build(
            "b",
            ("Content-Disposition: form-data; name=\"field\"", new byte[256 * 1024]));
        var source = new DribblePipeReader(input, 8 * 1024);
        var reader = new MultipartReader(source, "b", 1024, CancellationToken.None);
        var part = Assert.IsType<MultipartPart>(await reader.ReadNextAsync());

        Assert.True(source.ConsumedBytes < input.Length);
        await reader.DisposeAsync();

        Assert.Equal(input.Length, source.ConsumedBytes);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await part.Body.ReadAsync());
        await source.CompleteAsync();
    }

    [Fact]
    public async Task BoundaryLikeBytesAreStreamedAsBodyContent()
    {
        var content = "a\r\n--boundaryX\r\nb\r\n--boundary--X\r\nc"u8.ToArray();
        var input = MultipartTestData.Build(
            "boundary",
            ("Content-Disposition: form-data; name=\"file\"; filename=\"x.bin\"", content));
        var reader = CreateReader(input, "boundary");

        var part = Assert.IsType<MultipartPart>(await reader.ReadNextAsync());

        Assert.Equal(content, await ReadAll(part.Body));
        Assert.Null(await reader.ReadNextAsync());
    }

    [Fact]
    public async Task BoundariesSplitAcrossReadsFromOneToSevenBytes()
    {
        var content = Encoding.ASCII.GetBytes("body content");
        var input = MultipartTestData.Build(
            "split-boundary",
            preamble: "preamble\r\n",
            epilogue: "epilogue",
            ("Content-Disposition: form-data; name=\"field\"", content));

        for (var segmentSize = 1; segmentSize <= 7; segmentSize++)
        {
            var source = new DribblePipeReader(input, segmentSize);
            var reader = new MultipartReader(source, "split-boundary", 1024, CancellationToken.None);

            var part = Assert.IsType<MultipartPart>(await reader.ReadNextAsync());
            Assert.Equal(content, await ReadAll(part.Body));
            Assert.Null(await reader.ReadNextAsync());
            await source.CompleteAsync();
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task StreamsTenMiBWithBoundedPipes()
    {
        const int length = 10 * 1024 * 1024;
        var input = new Pipe(new PipeOptions(
            pauseWriterThreshold: 32 * 1024,
            resumeWriterThreshold: 16 * 1024,
            useSynchronizationContext: false));
        var producer = ProduceLargeMultipart(input.Writer, length);
        var reader = new MultipartReader(input.Reader, "large", 1024, CancellationToken.None);

        var part = Assert.IsType<MultipartPart>(await reader.ReadNextAsync());
        var readBytes = 0;
        while (true)
        {
            var result = await part.Body.ReadAsync();
            var buffer = result.Buffer;
            foreach (var segment in buffer)
            {
                for (var i = 0; i < segment.Length; i++)
                {
                    Assert.Equal((byte)((readBytes + i) % 251), segment.Span[i]);
                }

                readBytes += segment.Length;
            }

            part.Body.AdvanceTo(buffer.End);
            if (result.IsCompleted)
            {
                break;
            }
        }

        Assert.Equal(length, readBytes);
        Assert.Null(await reader.ReadNextAsync());
        await producer;
        await input.Reader.CompleteAsync();
    }

    [Fact]
    public async Task MissingClosingBoundaryThrowsFormException()
    {
        var input = "--b\r\nContent-Disposition: form-data; name=\"field\"\r\n\r\nvalue"u8.ToArray();
        var reader = CreateReader(input, "b");
        var part = Assert.IsType<MultipartPart>(await reader.ReadNextAsync());

        await Assert.ThrowsAsync<FormException>(async () => await ReadAll(part.Body));
    }

    [Fact]
    public async Task PartLimitIsEnforcedWhileStreaming()
    {
        var input = MultipartTestData.Build(
            "b",
            ("Content-Disposition: form-data; name=\"one\"", []),
            ("Content-Disposition: form-data; name=\"two\"", []));
        var reader = new MultipartReader(
            PipeReader.Create(new MemoryStream(input, writable: false)),
            "b",
            maximumParts: 1,
            CancellationToken.None);

        Assert.NotNull(await reader.ReadNextAsync());
        await Assert.ThrowsAsync<FormException>(async () => await reader.ReadNextAsync());
    }

    [Fact]
    public async Task ReadNextHonorsCancellation()
    {
        var pipe = new Pipe();
        var reader = new MultipartReader(pipe.Reader, "b", 1024, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await reader.ReadNextAsync(cancellation.Token));

        await pipe.Writer.CompleteAsync();
        await pipe.Reader.CompleteAsync();
    }

    [Fact]
    public async Task MultipartClaimsTheRequestBodyOnce()
    {
        Exception? captured = null;
        var body = MultipartTestData.Build("b");
        var app = new App();
        app.Post("/", async context =>
        {
            await context.Req.Multipart();
            captured = Record.Exception(() => _ = context.Req.BodyReader);
            await context.Text("ok");
        });

        await using var response = await TestApp.Send(
            app,
            method: "POST",
            body: body,
            headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "multipart/form-data; boundary=b",
            });

        Assert.Equal("ok", response.BodyText);
        Assert.IsType<InvalidOperationException>(captured);
    }

    private static MultipartReader CreateReader(byte[] input, string boundary) => new(
        PipeReader.Create(new MemoryStream(input, writable: false)),
        boundary,
        maximumParts: 1024,
        CancellationToken.None);

    private static async Task<byte[]> ReadAll(PipeReader reader)
    {
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await reader.ReadAsync();
            var buffer = result.Buffer;
            foreach (var segment in buffer)
            {
                stream.Write(segment.Span);
            }

            reader.AdvanceTo(buffer.End);
            if (result.IsCompleted)
            {
                return stream.ToArray();
            }
        }
    }

    private static async Task ProduceLargeMultipart(PipeWriter writer, int length)
    {
        writer.Write("--large\r\nContent-Disposition: form-data; name=\"file\"; filename=\"large.bin\"\r\n\r\n"u8);
        await writer.FlushAsync();
        var offset = 0;
        while (offset < length)
        {
            var count = Math.Min(8192, length - offset);
            var destination = writer.GetSpan(count);
            for (var i = 0; i < count; i++)
            {
                destination[i] = (byte)((offset + i) % 251);
            }

            writer.Advance(count);
            offset += count;
            await writer.FlushAsync();
        }

        writer.Write("\r\n--large--\r\n"u8);
        await writer.CompleteAsync();
    }
}

internal sealed class DribblePipeReader : PipeReader
{
    private readonly byte[] _data;
    private readonly int _segmentSize;
    private ReadOnlySequence<byte> _activeBuffer;
    private int _available;
    private int _consumed;
    private bool _active;
    private bool _completed;

    public DribblePipeReader(byte[] data, int segmentSize)
    {
        _data = data;
        _segmentSize = segmentSize;
    }

    public int ConsumedBytes => _consumed;

    public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        if (!_active)
        {
            throw new InvalidOperationException();
        }

        _consumed += checked((int)_activeBuffer.Slice(0, consumed).Length);
        _activeBuffer = default;
        _active = false;
    }

    public override void CancelPendingRead()
    {
    }

    public override void Complete(Exception? exception = null) => _completed = true;

    public override ValueTask CompleteAsync(Exception? exception = null)
    {
        _completed = true;
        return ValueTask.CompletedTask;
    }

    public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<ReadResult>(CreateReadResult());
    }

    public override bool TryRead(out ReadResult result)
    {
        result = CreateReadResult();
        return true;
    }

    private ReadResult CreateReadResult()
    {
        if (_completed)
        {
            throw new InvalidOperationException();
        }

        if (_active)
        {
            throw new InvalidOperationException();
        }

        _available = Math.Min(_data.Length, Math.Max(_available, _consumed) + _segmentSize);
        _activeBuffer = new ReadOnlySequence<byte>(_data.AsMemory(_consumed, _available - _consumed));
        _active = true;
        return new ReadResult(_activeBuffer, isCanceled: false, isCompleted: _available == _data.Length);
    }
}
