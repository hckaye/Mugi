using System.Net;

namespace Miya.Tests;

public sealed class AddressResolutionTests
{
    [Fact]
    public void OptionsAddressTakesPrecedenceOverEnvironment()
    {
        var address = App<Context>.ResolveAddress(
            configuredAddress: IPAddress.Any,
            environmentHost: "127.0.0.1");

        Assert.Equal(IPAddress.Any, address);
    }

    [Fact]
    public void ValidEnvironmentHostIsUsedWhenOptionIsOmitted()
    {
        var address = App<Context>.ResolveAddress(
            configuredAddress: null,
            environmentHost: "0.0.0.0");

        Assert.Equal(IPAddress.Any, address);
    }

    [Fact]
    public void ValidIPv6EnvironmentHostIsUsedWhenOptionIsOmitted()
    {
        var address = App<Context>.ResolveAddress(
            configuredAddress: null,
            environmentHost: "::1");

        Assert.Equal(IPAddress.IPv6Loopback, address);
    }

    [Fact]
    public void IPv6AnyEnvironmentHostIsUsedWhenOptionIsOmitted()
    {
        var address = App<Context>.ResolveAddress(
            configuredAddress: null,
            environmentHost: "::");

        Assert.Equal(IPAddress.IPv6Any, address);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("localhost")]
    [InlineData("not-an-ip")]
    [InlineData("127.0.0.1:8080")]
    [InlineData("*")]
    public void MissingOrInvalidEnvironmentHostFallsBackToLoopback(string? environmentHost)
    {
        var address = App<Context>.ResolveAddress(
            configuredAddress: null,
            environmentHost);

        Assert.Equal(IPAddress.Loopback, address);
    }

    [Fact]
    public void ExplicitLoopbackIgnoresEnvironmentHost()
    {
        var address = App<Context>.ResolveAddress(
            configuredAddress: IPAddress.Loopback,
            environmentHost: "0.0.0.0");

        Assert.Equal(IPAddress.Loopback, address);
    }
}
