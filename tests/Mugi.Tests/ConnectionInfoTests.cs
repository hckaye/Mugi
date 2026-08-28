using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Mugi.Tests;

public sealed class ConnectionInfoTests
{
    [Fact]
    public async Task ConnectionInfoIsNullAndZeroWhenFeatureIsAbsent()
    {
        IPAddress? remoteAddress = null;
        var remotePort = -1;
        IPAddress? localAddress = null;
        var localPort = -1;
        var app = new App();
        app.Get("/", context =>
        {
            remoteAddress = context.Req.RemoteAddress;
            remotePort = context.Req.RemotePort;
            localAddress = context.Req.LocalAddress;
            localPort = context.Req.LocalPort;
            return context.Text("ok");
        });

        await using var response = await TestApp.Send(app);

        Assert.Null(remoteAddress);
        Assert.Equal(0, remotePort);
        Assert.Null(localAddress);
        Assert.Equal(0, localPort);
        Assert.Equal("ok", response.BodyText);
    }

    [Fact]
    public async Task ProtocolReflectsFeatureDefaultInProcess()
    {
        var app = new App();
        app.Get("/", context => context.Text(context.Req.Protocol));

        await using var response = await TestApp.Send(app);

        Assert.Equal(string.Empty, response.BodyText);
    }

    [Fact]
    public async Task ProtocolReflectsProvidedFeatureValue()
    {
        var app = new App();
        app.Get("/", context => context.Text(context.Req.Protocol));

        await using var response = await TestApp.Send(app, protocol: "HTTP/2");

        Assert.Equal("HTTP/2", response.BodyText);
    }

    [Fact]
    public async Task IsHttpsIsFalseForDefaultHttpScheme()
    {
        var app = new App();
        app.Get("/", context => context.Text(context.Req.IsHttps.ToString()));

        await using var response = await TestApp.Send(app);

        Assert.Equal("False", response.BodyText);
    }

    [Fact]
    public async Task IsHttpsIsTrueForHttpsScheme()
    {
        var app = new App();
        app.Get("/", context => context.Text(context.Req.IsHttps.ToString()));

        await using var response = await TestApp.Send(app, scheme: "https");

        Assert.Equal("True", response.BodyText);
    }

    [Fact]
    public async Task IsHttpsIsCaseInsensitive()
    {
        var app = new App();
        app.Get("/", context => context.Text(context.Req.IsHttps.ToString()));

        await using var response = await TestApp.Send(app, scheme: "HTTPS");

        Assert.Equal("True", response.BodyText);
    }

    [Fact]
    public async Task ConnectionFeatureValuesFlowThrough()
    {
        IPAddress? remoteAddress = null;
        var remotePort = -1;
        IPAddress? localAddress = null;
        var localPort = -1;
        var app = new App();
        app.Get("/", context =>
        {
            remoteAddress = context.Req.RemoteAddress;
            remotePort = context.Req.RemotePort;
            localAddress = context.Req.LocalAddress;
            localPort = context.Req.LocalPort;
            return context.Text("ok");
        });

        var connection = new TestConnectionFeature
        {
            RemoteIpAddress = IPAddress.Parse("198.51.100.7"),
            RemotePort = 54321,
            LocalIpAddress = IPAddress.Parse("203.0.113.4"),
            LocalPort = 8080,
        };

        await using var response = await TestApp.Send(app, connection: connection);

        Assert.Equal(IPAddress.Parse("198.51.100.7"), remoteAddress);
        Assert.Equal(54321, remotePort);
        Assert.Equal(IPAddress.Parse("203.0.113.4"), localAddress);
        Assert.Equal(8080, localPort);
        Assert.Equal("ok", response.BodyText);
    }

    [Fact]
    public async Task ConnectionFeatureWithNullAddressesReportsNullAndZero()
    {
        IPAddress? remoteAddress = null;
        var remotePort = -1;
        IPAddress? localAddress = null;
        var localPort = -1;
        var app = new App();
        app.Get("/", context =>
        {
            remoteAddress = context.Req.RemoteAddress;
            remotePort = context.Req.RemotePort;
            localAddress = context.Req.LocalAddress;
            localPort = context.Req.LocalPort;
            return context.Text("ok");
        });

        var connection = new TestConnectionFeature
        {
            RemoteIpAddress = null,
            RemotePort = 0,
            LocalIpAddress = null,
            LocalPort = 0,
        };

        await using var response = await TestApp.Send(app, connection: connection);

        Assert.Null(remoteAddress);
        Assert.Equal(0, remotePort);
        Assert.Null(localAddress);
        Assert.Equal(0, localPort);
        Assert.Equal("ok", response.BodyText);
    }

    [Fact]
    public async Task ConnectionInfoReadsFeatureLazilyEachCall()
    {
        var app = new App();
        app.Get("/", context =>
        {
            var first = context.Req.RemotePort;
            var second = context.Req.RemotePort;
            return context.Text($"{first}|{second}");
        });

        var connection = new TestConnectionFeature { RemotePort = 12345 };

        await using var response = await TestApp.Send(app, connection: connection);

        Assert.Equal("12345|12345", response.BodyText);
    }

    private sealed class TestConnectionFeature : IHttpConnectionFeature
    {
        public string ConnectionId { get; set; } = string.Empty;
        public IPAddress? RemoteIpAddress { get; set; }
        public int RemotePort { get; set; }
        public IPAddress? LocalIpAddress { get; set; }
        public int LocalPort { get; set; }
    }
}
