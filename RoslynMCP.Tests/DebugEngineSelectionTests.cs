using System.Diagnostics;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Covers automatic debug-engine selection. The caller never picks: netcoredbg cannot attach to
/// .NET Framework and ICorDebug is the only thing that can, so choosing wrong simply fails.
/// </summary>
[Collection(DebuggerCollection.Name)]
public class DebugEngineSelectionTests
{
    [Fact]
    public void WhenProjectIsLegacyThenNetFrameworkEngineIsSelected()
    {
        Assert.Equal(DebugRuntime.NetFramework, DebugRuntimeDetector.ForProject(FixturePaths.LegacyProjectFile));
        Assert.Equal(DebugRuntime.NetFramework, DebugRuntimeDetector.ForProject(FixturePaths.WebFormsSiteFile));
    }

    [Fact]
    public void WhenProjectIsSdkStyleThenCoreClrEngineIsSelected()
    {
        Assert.Equal(DebugRuntime.CoreClr, DebugRuntimeDetector.ForProject(FixturePaths.SampleProjectFile));
        Assert.Equal(DebugRuntime.CoreClr, DebugRuntimeDetector.ForProject(FixturePaths.DebugTestProjectFile));
    }

    [Fact]
    public void WhenSessionCreatedForProjectThenTheMatchingBackendIsUsed()
    {
        try
        {
            var legacy = DebugSessionManager.CreateSessionForProject(FixturePaths.LegacyProjectFile);
            Assert.IsType<IcorDebugBackend>(legacy);

            var modern = DebugSessionManager.CreateSessionForProject(FixturePaths.SampleProjectFile);
            Assert.IsType<DebuggerService>(modern);
        }
        finally
        {
            DebugSessionManager.DisposeSession();
        }
    }

    [Fact]
    public void WhenInspectingThisProcessThenCoreClrIsDetected()
    {
        // The test host itself runs on CoreCLR, so module-based detection must say so.
        Assert.Equal(
            DebugRuntime.CoreClr,
            DebugRuntimeDetector.ForProcess(Environment.ProcessId));
    }

    [Fact]
    public void WhenProcessIsGoneThenDetectionFallsBackInsteadOfThrowing()
    {
        // A PID that cannot be opened must degrade, never throw, or attach would crash the tool.
        var runtime = DebugRuntimeDetector.ForProcess(int.MaxValue - 1);
        Assert.Equal(DebugRuntime.CoreClr, runtime);
    }

    [RequiresNetFrameworkFact]
    public void WhenAttachedToNetFrameworkProcessThenNetFrameworkEngineIsSelected()
    {
        using var target = FxTargetProcess.Launch();

        var runtime = DebugRuntimeDetector.ForProcess(target.Process.Id);

        Assert.Equal(DebugRuntime.NetFramework, runtime);
        Assert.IsType<IcorDebugBackend>(DebugSessionManager.CreateSession(runtime));
        DebugSessionManager.DisposeSession();
    }

    [RequiresNetFrameworkFact]
    public async Task WhenDebuggingNetFrameworkProcessThenBreakpointIsHitThroughTheToolPath()
    {
        using var target = FxTargetProcess.Launch();
        var session = DebugSessionManager.CreateSessionForProcess(target.Process.Id);

        try
        {
            Assert.IsType<IcorDebugBackend>(session);

            var attached = await session.AttachToProcessAsync(
                target.Process.Id, [(FxTargetProcess.SourcePath, FxTargetProcess.BreakpointLine)]);
            Assert.DoesNotContain("Error:", attached);

            // The target loops, so the breakpoint is reached without needing to resume first.
            var stopped = await WaitForStopAsync(session);
            Assert.True(stopped, "The breakpoint was never hit.");

            var stack = await session.GetStackTraceAsync();
            Assert.Contains("Compute", stack);

            var locals = await session.GetLocalsAsync();
            Assert.Contains("input", locals);
        }
        finally
        {
            DebugSessionManager.DisposeSession();
        }
    }

    private static async Task<bool> WaitForStopAsync(IDebugBackend session)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (session.CurrentFrame is not null)
                return true;
            await Task.Delay(100);
        }

        return false;
    }
}

/// <summary>
/// Compiles and runs a small .NET Framework console app to debug. Built at test time with the
/// framework's own csc so it carries a Windows PDB — the format that exercises the diasymreader
/// path, which is what .NET Framework debugging actually depends on.
/// </summary>
internal sealed class FxTargetProcess : IDisposable
{
    /// <summary>The statement to break on. Located in the source rather than hardcoded, so it
    /// cannot silently drift when the target program changes.</summary>
    private const string BreakpointStatement = "int result = input * 2;";

    public static int BreakpointLine => Array.FindIndex(
        TargetSource.ReplaceLineEndings("\n").Split('\n'),
        line => line.Contains(BreakpointStatement, StringComparison.Ordinal)) + 1;

    private static readonly Lazy<string?> s_compiled = new(Compile);

    public required Process Process { get; init; }

    public static string SourcePath => Path.Combine(
        Path.GetDirectoryName(s_compiled.Value!)!, "Program.cs");

    public static bool IsAvailable => s_compiled.Value is not null;

    public static FxTargetProcess Launch()
    {
        var exe = s_compiled.Value
            ?? throw new InvalidOperationException("The .NET Framework target could not be compiled.");

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;

        // Wait until the target says it is up. Attaching microseconds after spawn would race the
        // CLR's own load, which no real attach target (w3wp, iisexpress) does.
        var ready = process.StandardOutput.ReadLine();
        if (ready is null)
            throw new InvalidOperationException("The .NET Framework target exited before signalling readiness.");

        return new FxTargetProcess { Process = process };
    }

    /// <summary>
    /// A long-running target that signals readiness first, then repeatedly calls a method with a
    /// local worth inspecting.
    /// </summary>
    private const string TargetSource =
        """
        using System;

        namespace FxTarget
        {
            public class Counter
            {
                private int _count;

                // Deliberately not an auto-property: there is no <Count>k__BackingField, so
                // reading this requires calling the getter.
                public int Count { get { return _count; } }

                // Computed: no backing field exists at all.
                public int Doubled { get { return _count * 2; } }

                public void Bump() { _count++; }

                public string Describe() { return "count=" + _count; }
            }

            public static class Program
            {
                public static void Main()
                {
                    Console.WriteLine("ready");
                    Console.Out.Flush();
                    Counter counter = new Counter();
                    for (int i = 0; i < 100000; i++)
                    {
                        counter.Bump();
                        Compute(i, counter);
                        System.Threading.Thread.Sleep(20);
                    }
                }

                private static int Compute(int input, Counter counter)
                {
                    int result = input * 2;
                    return result;
                }
            }
        }
        """;

    private static string? Compile()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var csc = new[] { "Framework64", "Framework" }
            .Select(d => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "Microsoft.NET", d, "v4.0.30319", "csc.exe"))
            .FirstOrDefault(File.Exists);

        if (csc is null)
            return null;

        var directory = Path.Combine(Path.GetTempPath(), "roslynsense-fxtarget-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);

        var source = Path.Combine(directory, "Program.cs");
        var exe = Path.Combine(directory, "FxTarget.exe");

        File.WriteAllText(source, TargetSource);

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = csc,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = directory,
            ArgumentList = { "-nologo", "-debug:full", "-out:" + exe, source },
        })!;

        process.WaitForExit(120_000);
        return process.ExitCode == 0 && File.Exists(exe) ? exe : null;
    }

    public void Dispose()
    {
        try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); } catch { }
        try { Process.Dispose(); } catch { }
    }
}
