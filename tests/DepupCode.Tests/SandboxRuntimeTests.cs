using Xunit;
using Xunit.Sdk;

namespace DepupCode.Tests;

public sealed class SandboxRuntimeTests
{
    [Fact]
    [Trait("Category", "NativeIntegration")]
    public async Task Desktop_process_can_launch_isolated_child_and_protect_workspace()
    {
        EnsureDesktopWindowsHost();
        var completed = await SandboxRuntimeScenarios.VerifyAppContainerAsync();
        Assert.Contains("AppContainer launch, workspace write, and sibling-file denial", completed);
    }

    private static void EnsureDesktopWindowsHost()
    {
        if (!OperatingSystem.IsWindows()) throw SkipException.ForSkip("Windows AppContainer and ConPTY are Windows-only.");
        if (DepupCode.Services.WindowsExtensionSandbox.IsCurrentProcessAppContainer())
            throw SkipException.ForSkip("Run this integration test from an ordinary desktop process; nested AppContainer launch is not a valid smoke-test host.");
    }
}
