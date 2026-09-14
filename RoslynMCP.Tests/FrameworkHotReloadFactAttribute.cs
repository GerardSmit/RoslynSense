using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Opt-in gate for the .NET Framework Edit-and-Continue tests.
/// </summary>
/// <remarks>
/// These tests build real desktop applications and require the packaged architecture-matched
/// debug workers. ApplyChanges runs in those workers because the desktop CLR can fault on an
/// invalid delta. Enable them with ROSLYNSENSE_TEST_FX_HOTRELOAD=1 after building with
/// BuildDebugWorkers=true; missing workers then fail the tests instead of silently skipping them.
/// </remarks>
public sealed class FrameworkHotReloadFactAttribute : FactAttribute
{
    public FrameworkHotReloadFactAttribute() => Skip = SkipReason;

    internal static string? SkipReason
    {
        get
        {
            if (Environment.GetEnvironmentVariable("ROSLYNSENSE_TEST_FX_HOTRELOAD") != "1")
                return "Set ROSLYNSENSE_TEST_FX_HOTRELOAD=1 and build with BuildDebugWorkers=true to run.";
            return !OperatingSystem.IsWindows() || FrameworkHotReloadTests.FrameworkDirectory() is null
                ? "No .NET Framework installation was found."
                : null;
        }
    }
}

/// <summary>The same explicit runtime gate for the architecture and PDB-format matrix.</summary>
public sealed class FrameworkHotReloadTheoryAttribute : TheoryAttribute
{
    public FrameworkHotReloadTheoryAttribute() => Skip = FrameworkHotReloadFactAttribute.SkipReason;
}
