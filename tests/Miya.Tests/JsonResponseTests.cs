using System.Buffers;
using System.Text;
using Microsoft.AspNetCore.Http;
using Miya.Json;

namespace Miya.Tests;

public sealed class JsonResponseTests
{
    [Fact]
    public async Task RegisteredCodecWritesOnceForGetHeadAndNoContent()
    {
        var codec = new CountingPayloadCodec();
        global::Miya.Json.Json.Register(codec);
        var app = new App();
        app.Get("/value", context => context.Json(new CountingPayload("value")));
        app.Get("/no-content", context =>
        {
            context.Status(StatusCodes.Status204NoContent);
            return context.Json(new CountingPayload("value"));
        });

        await using var get = await TestApp.Send(app, path: "/value");

        Assert.Equal(1, codec.WriteCount);
        Assert.Equal("{\"value\":\"value\"}", get.BodyText);
        var getContentLength = get.Response.Headers.ContentLength.ToString();

        await using var head = await TestApp.Send(app, method: "HEAD", path: "/value");

        Assert.Equal(2, codec.WriteCount);
        Assert.Equal(getContentLength, head.Response.Headers.ContentLength.ToString());
        Assert.Empty(head.BodyText);

        await using var noContent = await TestApp.Send(app, path: "/no-content");

        Assert.Equal(3, codec.WriteCount);
        Assert.Equal(StatusCodes.Status204NoContent, noContent.Response.StatusCode);
        Assert.False(noContent.Response.Headers.ContainsKey("Content-Length"));
        Assert.Empty(noContent.BodyText);
    }

    [Fact]
    public async Task ExplicitCodecWritesOnce()
    {
        var codec = new CountingPayloadCodec();
        var app = new App();
        app.Get("/", context => context.Json(new CountingPayload("value"), codec));

        await using var response = await TestApp.Send(app);

        Assert.Equal(1, codec.WriteCount);
        Assert.Equal("{\"value\":\"value\"}", response.BodyText);
    }

    [Theory]
    [InlineData(StatusCodes.Status204NoContent)]
    [InlineData(StatusCodes.Status304NotModified)]
    public async Task BodylessStatusKeepsJsonContentTypeWithoutBodyOrContentLength(int status)
    {
        var app = new App();
        app.Get("/", context =>
        {
            context.Status(status);
            return context.Json(new TestPayload("value"), TestPayloadCodec.Instance);
        });

        await using var response = await TestApp.Send(app);

        Assert.Equal(status, response.Response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Response.Headers.ContentType.ToString());
        Assert.False(response.Response.Headers.ContainsKey("Content-Length"));
        Assert.Empty(response.BodyText);
    }

    [Fact]
    public async Task ResponseCanExceedConfiguredJsonInputLengthsWithoutMutatingOptions()
    {
        const int valueLength = 2 * 1024 * 1024;
        var payload = new TestPayload(new string('x', valueLength));
        var jsonOptions = new JsonOptions();
        var options = new AppOptions
        {
            MaxBufferedResponseBytes = 3 * 1024 * 1024,
            Json = jsonOptions,
        };
        var app = new App();
        app.Get("/", context => context.Json(payload, TestPayloadCodec.Instance));

        await using var response = await TestApp.Send(app, options: options);

        Assert.Equal(valueLength + 12, response.ResponseBody.Body.Length);
        Assert.StartsWith("{\"value\":\"", response.BodyText, StringComparison.Ordinal);
        Assert.EndsWith("\"}", response.BodyText, StringComparison.Ordinal);
        Assert.Equal(JsonOptions.Default.MaxDocumentByteLength, jsonOptions.MaxDocumentByteLength);
        Assert.Equal(JsonOptions.Default.MaxStringByteLength, jsonOptions.MaxStringByteLength);

        var destination = new ArrayBufferWriter<byte>();
        Assert.Throws<JsonException>(() => global::Miya.Json.Json.Serialize(
            destination,
            payload,
            TestPayloadCodec.Instance,
            jsonOptions));
    }

    [Fact]
    public async Task LargeHeadJsonUsesSerializedContentLengthWithoutBody()
    {
        const int valueLength = 2 * 1024 * 1024;
        var bufferedLength = -1;
        var app = new App();
        app.Use(async (context, next) =>
        {
            await next(context);
            bufferedLength = context.TryGetBufferedResponse(out var body) ? body.Length : 0;
        });
        app.Get("/", context => context.Json(
            new TestPayload(new string('x', valueLength)),
            TestPayloadCodec.Instance));

        await using var response = await TestApp.Send(
            app,
            method: "HEAD",
            options: new AppOptions { MaxBufferedResponseBytes = 32 * 1024 });

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal((valueLength + 12).ToString(), response.Response.Headers.ContentLength.ToString());
        Assert.Empty(response.BodyText);
        Assert.Equal(0, bufferedLength);
    }

    [Theory]
    [InlineData(StatusCodes.Status204NoContent)]
    [InlineData(StatusCodes.Status304NotModified)]
    public async Task LargeBodylessJsonIsNotRetained(int status)
    {
        const int valueLength = 2 * 1024 * 1024;
        var bufferedLength = -1;
        var app = new App();
        app.Use(async (context, next) =>
        {
            await next(context);
            bufferedLength = context.TryGetBufferedResponse(out var body) ? body.Length : 0;
        });
        app.Get("/", context =>
        {
            context.Status(status);
            return context.Json(
                new TestPayload(new string('x', valueLength)),
                TestPayloadCodec.Instance);
        });

        await using var response = await TestApp.Send(
            app,
            options: new AppOptions { MaxBufferedResponseBytes = 32 * 1024 });

        Assert.Equal(status, response.Response.StatusCode);
        Assert.False(response.Response.Headers.ContainsKey("Content-Length"));
        Assert.Empty(response.BodyText);
        Assert.Equal(0, bufferedLength);
    }

    [Fact]
    public async Task ResponseIgnoresConfiguredJsonInputCollectionLimit()
    {
        var app = new App();
        app.Get("/", context => context.Json(
            new CollectionPayload(),
            CollectionPayloadCodec.Instance));

        await using var response = await TestApp.Send(
            app,
            options: new AppOptions
            {
                Json = new JsonOptions { MaxCollectionSize = 1 },
            });

        Assert.Equal("[null,null]", response.BodyText);
    }

    [Fact]
    public async Task RequestJsonBodyStillUsesInputLimit()
    {
        var value = new string('x', 2 * 1024 * 1024);
        var body = Encoding.UTF8.GetBytes(string.Concat("{\"value\":\"", value, "\"}"));
        global::Miya.Json.Json.Register(TestPayloadCodec.Instance);
        var app = new App();
        app.Post("/", async context =>
        {
            await context.Req.Json<TestPayload>();
            await context.Text("unreachable");
        });

        await using var response = await TestApp.Send(app, method: "POST", body: body);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, response.Response.StatusCode);
        Assert.Equal("Payload Too Large", response.BodyText);
    }

    [Fact]
    public async Task ResponseSerializationKeepsConfiguredCancellationToken()
    {
        using var cancellation = new CancellationTokenSource();
        var codec = new CancelingPayloadCodec(cancellation);
        Exception? observed = null;
        var app = new App();
        app.Get("/", context => context.Json(new CancelingPayload(), codec));
        app.OnError((context, exception) =>
        {
            observed = exception;
            return context.Text("cancelled");
        });

        await using var response = await TestApp.Send(
            app,
            options: new AppOptions
            {
                Json = new JsonOptions { CancellationToken = cancellation.Token },
            });

        Assert.IsType<OperationCanceledException>(observed);
        Assert.Equal("cancelled", response.BodyText);
    }

    [Fact]
    public async Task ResponseSerializationKeepsConfiguredDepthLimit()
    {
        Exception? observed = null;
        var app = new App();
        app.Get("/", context => context.Json(new DepthPayload(), DepthPayloadCodec.Instance));
        app.OnError((context, exception) =>
        {
            observed = exception;
            return context.Text("depth rejected");
        });

        await using var response = await TestApp.Send(
            app,
            options: new AppOptions
            {
                Json = new JsonOptions { MaxDepth = 1 },
            });

        Assert.IsType<JsonException>(observed);
        Assert.Equal("depth rejected", response.BodyText);
    }

    [Fact]
    public async Task ResponseOptionsCacheRefreshesWhenJsonOptionsChange()
    {
        Exception? observed = null;
        var app = new App();
        app.Get("/", context => context.Json(new DepthPayload(), DepthPayloadCodec.Instance));
        app.OnError((context, exception) =>
        {
            observed = exception;
            return context.Text("depth rejected");
        });

        await using var allowed = await TestApp.Send(
            app,
            options: new AppOptions
            {
                Json = new JsonOptions { MaxDepth = 2 },
            });

        Assert.Equal("[[null]]", allowed.BodyText);
        Assert.Null(observed);

        await using var rejected = await TestApp.Send(
            app,
            options: new AppOptions
            {
                Json = new JsonOptions { MaxDepth = 1 },
            });

        Assert.IsType<JsonException>(observed);
        Assert.Equal("depth rejected", rejected.BodyText);
    }
}

internal sealed record TestPayload(string Value);

internal sealed record CountingPayload(string Value);

internal sealed class CollectionPayload;

internal sealed class CancelingPayload;

internal sealed class DepthPayload;

internal sealed class TestPayloadCodec : IJsonCodec<TestPayload>
{
    public static TestPayloadCodec Instance { get; } = new();

    public void Write(ref JsonWriter writer, TestPayload? value)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteRaw("{\"value\":"u8);
        writer.WriteString(value.Value);
        writer.WriteRaw("}"u8);
    }

    public TestPayload? Read(ref JsonReader reader) => throw new NotSupportedException();
}

internal sealed class CountingPayloadCodec : IJsonCodec<CountingPayload>
{
    public int WriteCount { get; private set; }

    public void Write(ref JsonWriter writer, CountingPayload? value)
    {
        WriteCount++;
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteRaw("{\"value\":"u8);
        writer.WriteString(value.Value);
        writer.WriteRaw("}"u8);
    }

    public CountingPayload? Read(ref JsonReader reader) => throw new NotSupportedException();
}

internal sealed class CollectionPayloadCodec : IJsonCodec<CollectionPayload>
{
    public static CollectionPayloadCodec Instance { get; } = new();

    public void Write(ref JsonWriter writer, CollectionPayload? value)
    {
        writer.EnterContainer(2);
        writer.WriteRaw("[null,null]"u8);
        writer.ExitContainer();
    }

    public CollectionPayload? Read(ref JsonReader reader) => throw new NotSupportedException();
}

internal sealed class CancelingPayloadCodec : IJsonCodec<CancelingPayload>
{
    private readonly CancellationTokenSource _cancellation;

    public CancelingPayloadCodec(CancellationTokenSource cancellation)
    {
        _cancellation = cancellation;
    }

    public void Write(ref JsonWriter writer, CancelingPayload? value)
    {
        writer.WriteRaw("["u8);
        _cancellation.Cancel();
        writer.ThrowIfCancellationRequested();
        writer.WriteRaw("]"u8);
    }

    public CancelingPayload? Read(ref JsonReader reader) => throw new NotSupportedException();
}

internal sealed class DepthPayloadCodec : IJsonCodec<DepthPayload>
{
    public static DepthPayloadCodec Instance { get; } = new();

    public void Write(ref JsonWriter writer, DepthPayload? value)
    {
        writer.EnterContainer(1);
        writer.WriteRaw("["u8);
        writer.EnterContainer(1);
        writer.WriteRaw("[null]]"u8);
        writer.ExitContainer();
        writer.ExitContainer();
    }

    public DepthPayload? Read(ref JsonReader reader) => throw new NotSupportedException();
}
