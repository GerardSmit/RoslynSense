using RoslynMCP.Config;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The choice of engine for a .NET target: the tool's own on Windows, netcoredbg elsewhere, and a
/// setting or environment variable to send it the other way.
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
    public void WithNothingConfiguredTheEngineIsTheOneTheHostCanRun()
    {
        // The tool's own engine wherever it runs, because it is the one that can hot reload a
        // debugged process; the external one where it cannot, without a word said about it.
        var onWindows = Resolve(environment: null, configured: null, onWindows: true, out var warnings);
        Assert.Equal(CoreClrDebugEngine.IcorDebug, onWindows);
        Assert.Empty(warnings);

        var elsewhere = Resolve(environment: null, configured: null, onWindows: false, out warnings);
        Assert.Equal(CoreClrDebugEngine.NetCoreDbg, elsewhere);
        Assert.Empty(warnings);
    }

    [Fact]
    public void TheSettingCanSendAWindowsHostBackToTheExternalEngine()
    {
        var engine = Resolve(null, "netcoredbg", onWindows: true, out var warnings);

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

        Assert.Equal(CoreClrDebugEngine.IcorDebug, engine);
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
    public async Task ThisEngineTakesDeltasForBothRuntimes()
    {
        // The debugger is the only way onto a .NET process while it is attached — the runtime
        // refuses the in-process updater for as long as any debugger is — just as it is the only
        // way onto the desktop runtime. So neither session may answer with the refusal the
        // fan-out reads as a skip: that is reserved for a session whose debugger has no apply
        // path at all, and a .NET session claiming it would have its edits silently dropped.
        foreach (var runtime in new[]
                 {
                     RoslynMCP.Debugger.DebugRuntime.CoreClr,
                     RoslynMCP.Debugger.DebugRuntime.NetFramework,
                 })
        {
            using var backend = new RoslynMCP.Services.IcorDebugBackend(runtime);
            Assert.True(backend.AppliesDeltas);

            // Nothing is attached, so the apply fails — but as a session problem, not a refusal.
            var (ok, error) = await backend.ApplyDeltaAsync("Sample", [1], [2], [3]);
            Assert.False(ok);
            Assert.DoesNotContain(
                RoslynMCP.Services.IcorDebugBackend.NotADeltaTarget, error, StringComparison.OrdinalIgnoreCase);
        }
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
