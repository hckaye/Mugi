using System.Runtime.InteropServices;

namespace Miya.Tests;

public sealed class SignalRegistrationTests
{
    [WindowsFact]
    public void RegisterSignalReturnsRegistrationOnWindows()
    {
        AssertRegisterSignalReturnsRegistration();
    }

    [NonWindowsFact]
    public void RegisterSignalReturnsRegistrationOnNonWindows()
    {
        AssertRegisterSignalReturnsRegistration();
    }

    private static void AssertRegisterSignalReturnsRegistration()
    {
        var shutdown = new App<Context>.ShutdownSignal();
        using var interrupt = App<Context>.RegisterSignal(PosixSignal.SIGINT, shutdown);
        using var terminate = App<Context>.RegisterSignal(PosixSignal.SIGTERM, shutdown);

        Assert.NotNull(interrupt);
        Assert.NotNull(terminate);
    }
}

internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "OperatingSystem.IsWindows() is false on this platform.";
        }
    }
}

internal sealed class NonWindowsFactAttribute : FactAttribute
{
    public NonWindowsFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "OperatingSystem.IsWindows() is true on this platform.";
        }
    }
}
