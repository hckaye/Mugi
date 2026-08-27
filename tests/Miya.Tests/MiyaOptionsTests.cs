namespace Miya.Tests;

public sealed class MiyaOptionsTests
{
    [Fact]
    public void CleartextDefaultsToHttp1()
    {
        var options = new MiyaOptions();

        Assert.Equal(MiyaProtocols.Http1, options.Protocols);
        options.Validate();
    }

    [Fact]
    public void CleartextHttp2IsValid()
    {
        var options = new MiyaOptions
        {
            Protocols = MiyaProtocols.Http2,
        };

        options.Validate();
    }

    [Fact]
    public void CleartextHttp1AndHttp2IsRejected()
    {
        var options = new MiyaOptions
        {
            Protocols = MiyaProtocols.Http1AndHttp2,
        };

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("exactly HTTP/1.1 or HTTP/2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Http3WithoutCertificateIsRejected()
    {
        var options = new MiyaOptions
        {
            Protocols = MiyaProtocols.Http1AndHttp2AndHttp3,
        };

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("requires a TLS certificate", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData((MiyaProtocols)0)]
    [InlineData((MiyaProtocols)8)]
    public void UndefinedProtocolValuesAreRejected(MiyaProtocols protocols)
    {
        var options = new MiyaOptions
        {
            Protocols = protocols,
        };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }
}
