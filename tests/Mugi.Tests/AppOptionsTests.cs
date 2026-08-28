namespace Mugi.Tests;

public sealed class AppOptionsTests
{
    [Fact]
    public void CleartextDefaultsToHttp1()
    {
        var options = new AppOptions();

        Assert.Equal(Protocols.Http1, options.Protocols);
        Assert.Equal(10 * 1024 * 1024, options.MaxFormBodyBytes);
        Assert.Equal(1024, options.MaxFormFields);
        Assert.Equal(1024, options.MaxMultipartParts);
        options.Validate();
    }

    [Fact]
    public void FormLimitsMustBePositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            new AppOptions { MaxFormBodyBytes = 0 }.Validate);
        Assert.Throws<ArgumentOutOfRangeException>(
            new AppOptions { MaxFormFields = 0 }.Validate);
        Assert.Throws<ArgumentOutOfRangeException>(
            new AppOptions { MaxMultipartParts = 0 }.Validate);
    }

    [Fact]
    public void CleartextHttp2IsValid()
    {
        var options = new AppOptions
        {
            Protocols = Protocols.Http2,
        };

        options.Validate();
    }

    [Fact]
    public void CleartextHttp1AndHttp2IsRejected()
    {
        var options = new AppOptions
        {
            Protocols = Protocols.Http1AndHttp2,
        };

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("exactly HTTP/1.1 or HTTP/2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Http3WithoutCertificateIsRejected()
    {
        var options = new AppOptions
        {
            Protocols = Protocols.Http1AndHttp2AndHttp3,
        };

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("requires a TLS certificate", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData((Protocols)0)]
    [InlineData((Protocols)8)]
    public void UndefinedProtocolValuesAreRejected(Protocols protocols)
    {
        var options = new AppOptions
        {
            Protocols = protocols,
        };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void RunRejectsAnInvalidPortBeforeStarting()
    {
        var app = new App();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => app.Run(-1));

        Assert.Equal("explicitPort", exception.ParamName);
        Assert.Equal("Port must be between 0 and 65535. (Parameter 'explicitPort')", exception.Message);
    }

    [Fact]
    public async Task RunAsyncRejectsInvalidOptionsBeforeStarting()
    {
        var app = new App();
        var options = new AppOptions { MaxBufferedResponseBytes = 0 };

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await app.RunAsync(options));

        Assert.Equal("MaxBufferedResponseBytes", exception.ParamName);
    }

    [Fact]
    public async Task StartAsyncRejectsInvalidOptionsBeforeStarting()
    {
        var app = new App();
        var options = new AppOptions { MaxBufferedResponseBytes = 0 };

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await app.StartAsync(options));

        Assert.Equal("MaxBufferedResponseBytes", exception.ParamName);
    }

    [Fact]
    public async Task InProcessEntryRejectsInvalidOptionsBeforeCreatingAContext()
    {
        var app = new App();
        var options = new AppOptions { MaxBufferedResponseBytes = 0 };

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await TestApp.Send(app, options: options));

        Assert.Equal("MaxBufferedResponseBytes", exception.ParamName);
    }
}
