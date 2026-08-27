namespace Miya.Tests;

public sealed class PortResolutionTests
{
    [Fact]
    public void ExplicitPortTakesPrecedence()
    {
        var port = App<Context>.ResolvePort(
            explicitPort: 4100,
            configuredPort: 4200,
            environmentPort: "4300");

        Assert.Equal(4100, port);
    }

    [Fact]
    public void OptionsPortTakesPrecedenceOverEnvironment()
    {
        var port = App<Context>.ResolvePort(
            explicitPort: null,
            configuredPort: 4200,
            environmentPort: "4300");

        Assert.Equal(4200, port);
    }

    [Fact]
    public void ValidEnvironmentPortIsUsedWhenOtherValuesAreOmitted()
    {
        var port = App<Context>.ResolvePort(
            explicitPort: null,
            configuredPort: null,
            environmentPort: "4300");

        Assert.Equal(4300, port);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-port")]
    [InlineData("-1")]
    [InlineData("65536")]
    public void MissingOrInvalidEnvironmentPortFallsBackToDefault(string? environmentPort)
    {
        var port = App<Context>.ResolvePort(
            explicitPort: null,
            configuredPort: null,
            environmentPort);

        Assert.Equal(3000, port);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public void InvalidExplicitPortIsRejected(int explicitPort)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            App<Context>.ResolvePort(explicitPort, configuredPort: null, environmentPort: null));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public void InvalidOptionsPortIsRejected(int configuredPort)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            App<Context>.ResolvePort(explicitPort: null, configuredPort, environmentPort: null));
    }
}
