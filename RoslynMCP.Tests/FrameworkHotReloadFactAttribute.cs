using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Opt-in gate for the .NET Framework Edit-and-Continue test.
/// </summary>
/// <remarks>
/// Ordinary skip conditions guard tests that would <em>fail</em>. This one guards a test that can
/// take the test host down: <c>ICorDebugModule2::ApplyChanges</c> faults on a delta it dislikes
/// rather than returning an error, and a crashed host aborts every other test in the run rather
/// than reporting one failure. It stays in the suite because it is the only thing that can answer
/// whether the desktop CLR accepts a Roslyn-emitted delta — but it runs when asked, not by default.
/// </remarks>
public sealed class FrameworkHotReloadFactAttribute : FactAttribute
{
    public FrameworkHotReloadFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ROSLYNSENSE_TEST_FX_HOTRELOAD") != "1")
            Skip = "Set ROSLYNSENSE_TEST_FX_HOTRELOAD=1 to run; ApplyChanges can crash the host.";
        else if (!OperatingSystem.IsWindows() || FrameworkHotReloadTests.FrameworkDirectory() is null)
            Skip = "No .NET Framework installation was found.";
    }
}
