using RoslynMCP.Config;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The opt-in that sends a .NET target to the ICorDebug engine instead of netcoredbg.
/// </summary>
/// <remarks>
/// Resolution is tested through the seam that takes the environment and the platform as arguments,
/// so the precedence and the Windows-only refusal are both exercised wherever the suite runs
/// rather than only on the host that happens to match.
/// </remarks>
public class DebugEngineOptionTests
{
    private static CoreClrDebugEngine Resolve(
        string? environment, string? configured, bool onWindows, out List<string> warnings)
    {
        warnings = [];
        return DebugEngineOptions.Resolve(environment, configured, onWindows, warnings);
    }

    [Fact]
    public void WithNothingConfiguredTheEngineIsTheOneCoreClrAlwaysUsed()
    {
        // The whole point of an opt-in: a user who never heard of this keeps what they had.
        var engine = Resolve(environment: null, configured: null, onWindows: true, out var warnings);

        Assert.Equal(CoreClrDebugEngine.NetCoreDbg, engine);
        Assert.Empty(warnings);
    }

    [Fact]
    public void TheSettingSelectsTheEngine()
    {
        var engine = Resolve(null, "icordebug", onWindows: true, out var warnings);

        Assert.Equal(CoreClrDebugEngine.IcorDebug, engine);
        Assert.Empty(warnings);
    }

    [Fact]
    public void TheEnvironmentWinsOverTheFile()
    {
        // The order every other switch in the tool uses, and the one that makes a single run
        // debuggable the other way without editing a file the whole team shares.
        var engine = Resolve("icordebug", "netcoredbg", onWindows: true, out _);
        Assert.Equal(CoreClrDebugEngine.IcorDebug, engine);

        var back = Resolve("netcoredbg", "icordebug", onWindows: true, out _);
        Assert.Equal(CoreClrDebugEngine.NetCoreDbg, back);
    }

    [Theory]
    [InlineData("ICorDebug")]
    [InlineData("  icordebug  ")]
    public void TheNameIsReadWhateverItsCaseOrSpacing(string written)
    {
        Assert.Equal(CoreClrDebugEngine.IcorDebug, Resolve(null, written, onWindows: true, out _));
    }

    [Fact]
    public void AnUnreadableNameWarnsAndLeavesTheEngineAlone()
    {
        // Not a hard failure: the rest of the debugger section is still usable, and a debugger
        // that refuses to start is a worse answer than one that starts on the default.
        var engine = Resolve(null, "vsdbg", onWindows: true, out var warnings);

        Assert.Equal(CoreClrDebugEngine.NetCoreDbg, engine);
        Assert.Contains(warnings, w => w.Contains("vsdbg") && w.Contains("debugger.coreClrEngine"));
    }

    [Fact]
    public void AnUnreadableEnvironmentValueDoesNotDiscardTheConfiguredOne()
    {
        // The typo is in the override, so what it fails to override has to survive it.
        var engine = Resolve("icordbg", "icordebug", onWindows: true, out var warnings);

        Assert.Equal(CoreClrDebugEngine.IcorDebug, engine);
        Assert.Contains(warnings, w => w.Contains(DebugEngineOptions.EnvironmentVariable));
    }

    [Fact]
    public void OffWindowsTheEngineIsRefusedRatherThanAttempted()
    {
        // The engine's CoreCLR attach throws PlatformNotSupportedException there. Honouring the
        // setting would surface as debugging being broken instead of as a setting that does not
        // apply on this machine.
        var engine = Resolve(null, "icordebug", onWindows: false, out var warnings);

        Assert.Equal(CoreClrDebugEngine.NetCoreDbg, engine);
        Assert.Contains(warnings, w => w.Contains("Windows-only"));
    }

    [Fact]
    public void AnEngineNameSurvivesTheRoundTrip()
    {
        foreach (var engine in Enum.GetValues<CoreClrDebugEngine>())
            Assert.Equal(engine, DebugEngineOptions.Parse(DebugEngineOptions.NameOf(engine)));
    }

    [Fact]
    public async Task ADotNetTargetOnThisEngineDoesNotAlsoTakeDeltasThroughTheDebugger()
    {
        // Its in-process updater has already applied the same generation through the agent, and a
        // generation applied twice fails the second time — after which every later edit diffs
        // against one the debuggee never took. The refusal has to read as a skip to the fan-out,
        // not as an error, or a working hot reload would start reporting failures.
        using var backend = new RoslynMCP.Services.IcorDebugBackend(
            RoslynMCP.Debugger.DebugRuntime.CoreClr);

        var (ok, error) = await backend.ApplyDeltaAsync("Sample", [1], [2], [3]);

        Assert.False(ok);
        Assert.Contains(
            RoslynMCP.Services.IcorDebugBackend.NotADeltaTarget, error, StringComparison.OrdinalIgnoreCase);

        // Both halves of the contract, not just the wording: the session that refuses says so, and
        // the session that applies does not claim the refusal. Asserted against the same constant
        // the fan-out matches on, so a reworded message cannot leave this green while every .NET
        // hot reload starts reporting errors for modules it applied cleanly.
        using var framework = new RoslynMCP.Services.IcorDebugBackend(
            RoslynMCP.Debugger.DebugRuntime.NetFramework);
        Assert.True(framework.AppliesDeltas);
        Assert.False(backend.AppliesDeltas);

        var (_, frameworkError) = await framework.ApplyDeltaAsync("Sample", [1], [2], [3]);
        Assert.DoesNotContain(
            RoslynMCP.Services.IcorDebugBackend.NotADeltaTarget,
            frameworkError,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0xC00000FDu, "stack overflow")]
    [InlineData(0xC0000005u, "access violation")]
    [InlineData(0xE0434352u, "an unhandled managed exception")]
    public void ADebuggeeTheSystemKilledIsNamedRatherThanCalledAnExit(uint code, string expected)
    {
        // A stack overflow cannot report itself — the runtime has no stack left to raise on, so it
        // writes one line to the debuggee's own stderr and dies. Without this the session says
        // "process exited", which is what a clean run says, and the crash is invisible.
        Assert.Equal(expected, RoslynMCP.Debugger.DebugSession.FatalExitName(code));
    }

    [Fact]
    public void AnUnrecognisedExitCodeIsNotGivenAnInventedName()
    {
        // It is still reported, in hex, which is enough to look up. A plausible-sounding guess
        // would make the report less trustworthy rather than more.
        Assert.Null(RoslynMCP.Debugger.DebugSession.FatalExitName(0x2A));
        Assert.Null(RoslynMCP.Debugger.DebugSession.FatalExitName(0));
    }

    [Fact]
    public void NothingWrittenIsNotTheSameAsSomethingUnreadable()
    {
        // Parse answers null for both, which is why Resolve consults IsNullOrWhiteSpace first —
        // otherwise an absent setting would warn on every startup.
        Assert.Null(DebugEngineOptions.Parse(null));
        Assert.Null(DebugEngineOptions.Parse(""));

        Resolve(null, "", onWindows: true, out var warnings);
        Assert.Empty(warnings);
    }
}
