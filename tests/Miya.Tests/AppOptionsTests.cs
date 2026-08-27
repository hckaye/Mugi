namespace Miya.Tests;

public sealed class AppOptionsTests
{
    [Fact]
    public void CleartextDefaultsToHttp1()
    {
        var options = new AppOptions();

        Assert.Equal(Protocols.Http1, options.Protocols);
        options.Validate();
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
}
