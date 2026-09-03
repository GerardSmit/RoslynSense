using System.Diagnostics;
using RoslynMCP.Debugger;
using RoslynMCP.Services;
using RoslynMCP.Services.HotReload;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Edit-and-Continue against a live .NET Framework process, through the product's own path.
/// </summary>
/// <remarks>
/// <para>
/// The desktop runtime has no in-process metadata updater, so the only route onto it is
/// <c>ICorDebugModule2::ApplyChanges</c> — the app has to be under the debugger for an edit to
/// land. That call does not validate what it is given: an earlier version of this test built its
/// own delta from a stubbed baseline and the CLR faulted on it, taking the whole test host down
/// with an access violation rather than returning an error.
/// </para>
/// <para>
/// Two things came out of that and both shape this test. The delta is now computed by
/// <see cref="HotReloadService"/>, so Roslyn's EnC engine builds the baseline from the project's
/// real PDB instead of a stub. And the target is built <c>x86</c> on purpose: that forces
/// <see cref="DebugEngineFactory"/> to pick a bitness-matched worker, which is the only engine
/// allowed to call <c>ApplyChanges</c> — a fault there costs a disposable process rather than the
/// language server.
/// </para>
/// </remarks>
[Collection(DebuggerCollection.Name)]
public class FrameworkHotReloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fx-hotreload-{Guid.NewGuid():N}");

    /// <summary>
    /// SDK-style but <c>net48</c>: the desktop runtime with a project format the workspace can
    /// load without Visual Studio's MSBuild. x86 so the debugger runs out of process.
    /// </summary>
    private const string Project = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net48</TargetFramework>
            <PlatformTarget>x86</PlatformTarget>
            <AssemblyName>FxHotReloadTarget</AssemblyName>
            <RootNamespace>FxHotReloadTarget</RootNamespace>
            <Optimize>false</Optimize>
            <DebugType>portable</DebugType>
          </PropertyGroup>
        </Project>
        """;

    /// <summary>
    /// The same target with a Windows (full) PDB — what a real legacy .NET Framework project
    /// emits. The EnC baseline has to be readable from this format or hot reload only works on
    /// the SDK-style fixtures.
    /// </summary>
    private const string FullPdbProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net48</TargetFramework>
            <PlatformTarget>x86</PlatformTarget>
            <AssemblyName>FxHotReloadTarget</AssemblyName>
            <RootNamespace>FxHotReloadTarget</RootNamespace>
            <Optimize>false</Optimize>
            <DebugType>full</DebugType>
          </PropertyGroup>
        </Project>
        """;

    private const string BaselineSource = """
        using System;
        using System.IO;
        using System.Threading;

        namespace FxHotReloadTarget
        {
            public static class Program
            {
                public static int Compute(int input)
                {
                    return input * 2;
                }

                public static void Main(string[] args)
                {
                    for (int i = 0; i < 100000; i++)
                    {
                        File.AppendAllText(args[0], Compute(3) + Environment.NewLine);
                        Thread.Sleep(50);
                    }
                }
            }
        }
        """;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [FrameworkHotReloadFact]
    public Task ARoslynDeltaIsAcceptedByTheDesktopClrAndChangesALiveProcess() => RunAsync();

    /// <summary>
    /// Break All, then apply — the case that settles whether the desktop CLR needs a stop that
    /// arrived through a debug event, or merely a stop with a real context behind it.
    /// </summary>
    /// <remarks>
    /// Earlier attempts applied after <c>ICorDebugProcess::Stop</c> alone and after adopting a
    /// thread ad hoc, and both faulted. This one goes through the engine's own Break All, which
    /// suspends, adopts a user-code thread as the stop context, and emits a stop the session
    /// state machine sees — the same shape a breakpoint produces.
    /// </remarks>
    [FrameworkHotReloadFact]
    public async Task AnEditAppliesAfterBreakingIntoARunningTarget()
    {
        Directory.CreateDirectory(_root);
        string csproj = Path.Combine(_root, "FxHotReloadTarget.csproj");
        string sourcePath = Path.Combine(_root, "Program.cs");
        string log = Path.Combine(_root, "values.txt");

        await File.WriteAllTextAsync(csproj, Project);
        await File.WriteAllTextAsync(sourcePath, BaselineSource);
        Assert.True(await BuildAsync(csproj), "The .NET Framework target did not build.");

        string exe = Path.Combine(_root, "bin", "Debug", "net48", "FxHotReloadTarget.exe");
        var backend = (RoslynMCP.Services.Debugging.PublishingDebugBackend)
            DebugSessionManager.CreateSession(Services.DebugRuntime.NetFramework);

        try
        {
            Assert.DoesNotContain("Error:", await backend.LaunchAsync(
                exe, [log], null, Path.GetDirectoryName(exe)));

            _ = backend.ContinueAsync();
            Assert.True(await WaitForLastLineAsync(log, "6"), "The target never started.");

            string paused = await backend.InterruptAsync();
            Assert.DoesNotContain("cannot suspend", paused);
            Assert.NotNull(backend.CurrentFrame);

            var (session, _) = await HotReloadService.StartAsync(csproj);
            await File.WriteAllTextAsync(sourcePath, BaselineSource.Replace("input * 2", "input * 10"));

            var outcome = await session!.ApplyAsync();

            Assert.True(outcome.Ok,
                $"{outcome.Summary}\n" + string.Join("\n", outcome.Errors) +
                "\n--- engine ---\n" + backend.GetStatus());

            _ = backend.ContinueAsync();
            Assert.True(await WaitForLastLineAsync(log, "30"),
                "The delta was applied from a pause but the process kept returning the old value.");
        }
        finally
        {
            HotReloadService.Get(csproj)?.Stop();
            DebugSessionManager.DisposeSession();
        }
    }

    /// <summary>
    /// A running target is broken into, edited, and resumed, without the caller pausing first.
    /// </summary>
    /// <remarks>
    /// This is the case the whole investigation was about. It faulted for as long as the apply
    /// followed a bare <c>ICorDebugProcess::Stop</c>; it works now that the engine performs a real
    /// Break All — suspend, adopt a user-code thread, report the stop — before applying. The
    /// engine still refuses an apply to a running target, as a backstop for anything reaching
    /// past the backend.
    /// </remarks>
    [FrameworkHotReloadFact]
    public async Task AnEditAppliesToARunningTargetWithoutTheCallerPausingIt()
    {
        Directory.CreateDirectory(_root);
        string csproj = Path.Combine(_root, "FxHotReloadTarget.csproj");
        string sourcePath = Path.Combine(_root, "Program.cs");
        string log = Path.Combine(_root, "values.txt");

        await File.WriteAllTextAsync(csproj, Project);
        await File.WriteAllTextAsync(sourcePath, BaselineSource);
        Assert.True(await BuildAsync(csproj), "The .NET Framework target did not build.");

        string exe = Path.Combine(_root, "bin", "Debug", "net48", "FxHotReloadTarget.exe");
        var backend = (RoslynMCP.Services.Debugging.PublishingDebugBackend)
            DebugSessionManager.CreateSession(Services.DebugRuntime.NetFramework);

        try
        {
            Assert.DoesNotContain("Error:", await backend.LaunchAsync(
                exe, [log], null, Path.GetDirectoryName(exe)));

            _ = backend.ContinueAsync();
            Assert.True(await WaitForLastLineAsync(log, "6"), "The target never started.");

            var (session, _) = await HotReloadService.StartAsync(csproj);
            await File.WriteAllTextAsync(sourcePath, BaselineSource.Replace("input * 2", "input * 10"));

            var outcome = await session!.ApplyAsync();

            Assert.True(outcome.Ok,
                $"{outcome.Summary}\n" + string.Join("\n", outcome.Errors) +
                "\n--- engine ---\n" + backend.GetStatus());

            // Resumed by the apply itself: a hot reload that leaves the app suspended looks like
            // a hot reload that hung it.
            Assert.True(await WaitForLastLineAsync(log, "30"),
                "The edit was applied but the process did not carry on with the new code.");
        }
        finally
        {
            HotReloadService.Get(csproj)?.Stop();
            DebugSessionManager.DisposeSession();
        }
    }

    /// <summary>
    /// The delta pipeline against a Windows (full) PDB, which is what every legacy .NET
    /// Framework project — WebForms under IIS Express included — actually produces.
    /// </summary>
    /// <remarks>
    /// Roslyn's EnC baseline is read from the built module's PDB. Portable PDBs are read
    /// managed; a full PDB needs a native DiaSymReader. If that reader is not available to the
    /// server, the session opens fine and the failure only surfaces at the first apply — as a
    /// diagnostic or an exception, never as working hot reload.
    /// </remarks>
    [FrameworkHotReloadFact]
    public async Task AnEditAppliesToAProjectBuiltWithAFullWindowsPdb()
    {
        Directory.CreateDirectory(_root);
        string csproj = Path.Combine(_root, "FxHotReloadTarget.csproj");
        string sourcePath = Path.Combine(_root, "Program.cs");
        string log = Path.Combine(_root, "values.txt");

        await File.WriteAllTextAsync(csproj, FullPdbProject);
        await File.WriteAllTextAsync(sourcePath, BaselineSource);
        Assert.True(await BuildAsync(csproj), "The .NET Framework target did not build.");

        string exe = Path.Combine(_root, "bin", "Debug", "net48", "FxHotReloadTarget.exe");
        var backend = (RoslynMCP.Services.Debugging.PublishingDebugBackend)
            DebugSessionManager.CreateSession(Services.DebugRuntime.NetFramework);

        try
        {
            Assert.DoesNotContain("Error:", await backend.LaunchAsync(
                exe, [log], null, Path.GetDirectoryName(exe)));

            _ = backend.ContinueAsync();
            Assert.True(await WaitForLastLineAsync(log, "6"), "The target never started.");

            var (session, _) = await HotReloadService.StartAsync(csproj);
            await File.WriteAllTextAsync(sourcePath, BaselineSource.Replace("input * 2", "input * 10"));

            var outcome = await session!.ApplyAsync();

            Assert.True(outcome.Ok,
                $"{outcome.Summary}\n" +
                string.Join("\n", outcome.Diagnostics.Select(d => $"{d.Severity} {d.Id}: {d.Message}")) +
                string.Join("\n", outcome.Errors) +
                "\n--- engine ---\n" + backend.GetStatus());

            Assert.True(await WaitForLastLineAsync(log, "30"),
                "The edit was applied but the process did not carry on with the new code.");
        }
        finally
        {
            HotReloadService.Get(csproj)?.Stop();
            DebugSessionManager.DisposeSession();
        }
    }

    /// <summary>
    /// The user's loop: stopped at a breakpoint in the method being edited, apply, continue,
    /// hit the rebound breakpoint in the new version, and step through it.
    /// </summary>
    /// <remarks>
    /// The other tests end at "the process runs the new code". This one covers what the debugger
    /// itself has to survive after an apply: the breakpoint was invalidated and rebound to the
    /// new method version, and a step in that version needs sequence points that match the IL
    /// actually executing. A stepper built against the wrong version never completes, which the
    /// backend reports as "still running" — the editor experience is a debugger that is stuck.
    /// </remarks>
    [FrameworkHotReloadFact]
    public async Task ABreakpointHitAfterAnAppliedEditCanBeSteppedThrough()
    {
        Directory.CreateDirectory(_root);
        string csproj = Path.Combine(_root, "FxHotReloadTarget.csproj");
        string sourcePath = Path.Combine(_root, "Program.cs");
        string log = Path.Combine(_root, "values.txt");

        await File.WriteAllTextAsync(csproj, Project);
        await File.WriteAllTextAsync(sourcePath, BaselineSource);
        Assert.True(await BuildAsync(csproj), "The .NET Framework target did not build.");

        string exe = Path.Combine(_root, "bin", "Debug", "net48", "FxHotReloadTarget.exe");
        var backend = (RoslynMCP.Services.Debugging.PublishingDebugBackend)
            DebugSessionManager.CreateSession(Services.DebugRuntime.NetFramework);

        try
        {
            Assert.DoesNotContain("Error:", await backend.LaunchAsync(
                exe, [log], null, Path.GetDirectoryName(exe),
                [(sourcePath, LineOf("return input * 2"))]));

            // First hit: the old version of Compute.
            string stopped = await backend.ContinueAsync();
            Assert.DoesNotContain("still running", stopped);
            Assert.NotNull(backend.CurrentFrame);

            var (session, _) = await HotReloadService.StartAsync(csproj);
            await File.WriteAllTextAsync(sourcePath, BaselineSource.Replace("input * 2", "input * 10"));

            var outcome = await session!.ApplyAsync();
            Assert.True(outcome.Ok,
                $"{outcome.Summary}\n" + string.Join("\n", outcome.Errors) +
                "\n--- engine ---\n" + backend.GetStatus());

            // Second hit: the breakpoint was rebound to the new version of the method.
            stopped = await backend.ContinueAsync();
            Assert.False(stopped.Contains("still running"),
                "After the apply, the rebound breakpoint was never hit again:\n" + stopped +
                "\n--- engine ---\n" + backend.GetStatus());
            Assert.NotNull(backend.CurrentFrame);

            // The user's "go through it": step over inside the edited method, then step again to
            // leave it. A stepper that never completes reports "still running".
            foreach (int step in new[] { 1, 2 })
            {
                string stepResult = await backend.StepOverAsync();
                Assert.False(
                    stepResult.Contains("still running") || stepResult.StartsWith("Error"),
                    $"Step {step} after the apply did not complete:\n" + stepResult +
                    "\n--- engine ---\n" + backend.GetStatus());
                Assert.NotNull(backend.CurrentFrame);
            }

            // And the process still runs the new code once released. The breakpoint stops every
            // iteration, so ride a few hits rather than waiting for a free run.
            bool sawNewValue = false;
            for (int hit = 0; hit < 10 && !sawNewValue; hit++)
            {
                await backend.ContinueAsync();
                sawNewValue = await WaitForLastLineAsync(log, "30", attempts: 10);
            }

            Assert.True(sawNewValue,
                "Stepping succeeded but the resumed process kept returning the old value.");
        }
        finally
        {
            HotReloadService.Get(csproj)?.Stop();
            DebugSessionManager.DisposeSession();
        }
    }

    private async Task RunAsync()
    {
        Directory.CreateDirectory(_root);
        string csproj = Path.Combine(_root, "FxHotReloadTarget.csproj");
        string sourcePath = Path.Combine(_root, "Program.cs");
        string log = Path.Combine(_root, "values.txt");

        await File.WriteAllTextAsync(csproj, Project);
        await File.WriteAllTextAsync(sourcePath, BaselineSource);
        Assert.True(await BuildAsync(csproj), "The .NET Framework target did not build.");

        string exe = Path.Combine(_root, "bin", "Debug", "net48", "FxHotReloadTarget.exe");
        Assert.True(File.Exists(exe), $"'{exe}' was not produced by the build.");

        // The guard under test: an x86 target on this x64 host has to resolve to a worker.
        Assert.NotNull(DebugEngineFactory.FindWorker(DebugArch.X86));

        // Through the session manager, not a bare engine: an apply finds its target the way the
        // product does — the registered session, or one published by another process.
        var backend = (RoslynMCP.Services.Debugging.PublishingDebugBackend)
            DebugSessionManager.CreateSession(Services.DebugRuntime.NetFramework);
        try
        {
            string launched = await backend.LaunchAsync(
                exe, [log], null, Path.GetDirectoryName(exe),
                [(sourcePath, LineOf("File.AppendAllText"))]);
            Assert.DoesNotContain("Error:", launched);

            // The apply happens from a real break state, which is the only kind the desktop CLR
            string stopped = await backend.ContinueAsync();
            Assert.DoesNotContain("still running", stopped);
            Assert.NotNull(backend.CurrentFrame);

            var (session, _) = await HotReloadService.StartAsync(csproj);
            Assert.NotNull(session);

            await File.WriteAllTextAsync(sourcePath, BaselineSource.Replace("input * 2", "input * 10"));

            var outcome = await session!.ApplyAsync();

            Assert.True(outcome.Ok,
                $"{outcome.Summary}\n" +
                string.Join("\n", outcome.Diagnostics.Select(d => $"{d.Severity} {d.Id}: {d.Message}")) +
                string.Join("\n", outcome.Errors) +
                // The engine's captured output carries the worker's stderr, which is where an
                // ApplyChanges fault says what it actually was.
                "\n--- engine ---\n" + backend.GetStatus());

            // Resumed after the apply: the new IL only runs when the process does.
            _ = backend.ContinueAsync();

            Assert.True(await WaitForLastLineAsync(log, "30"),
                "ApplyChanges reported success but the process kept returning the old value.");
        }
        finally
        {
            HotReloadService.Get(csproj)?.Stop();
            DebugSessionManager.DisposeSession();
        }
    }

    /// <summary>Locates a line in the target by its text, so the breakpoint cannot drift when the
    /// program is edited.</summary>
    private static int LineOf(string text) => Array.FindIndex(
        BaselineSource.ReplaceLineEndings("\n").Split('\n'),
        line => line.Contains(text, StringComparison.Ordinal)) + 1;

    private static async Task<bool> BuildAsync(string csproj)
    {
        var build = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(csproj),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "build", csproj, "-c", "Debug", "--nologo" },
        })!;

        string output = await build.StandardOutput.ReadToEndAsync();
        await build.WaitForExitAsync();

        if (build.ExitCode != 0)
            Assert.Fail(output);

        return true;
    }

    internal static string? FrameworkDirectory()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        return new[] { "Framework64", "Framework" }
            .Select(flavour => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "Microsoft.NET", flavour, "v4.0.30319"))
            .FirstOrDefault(directory => File.Exists(Path.Combine(directory, "mscorlib.dll")));
    }

    /// <summary>Waits for the target's most recent write to be a given value. The last line, not
    /// any line: every earlier value is still in the file.</summary>
    private static async Task<bool> WaitForLastLineAsync(string log, string value, int attempts = 200)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                if (File.Exists(log))
                {
                    var lines = File.ReadLines(log).ToList();
                    if (lines.Count > 0 && lines[^1].Trim() == value)
                        return true;
                }
            }
            catch (IOException)
            {
                // The target appends while this reads.
            }

            await Task.Delay(100);
        }

        return false;
    }
}
