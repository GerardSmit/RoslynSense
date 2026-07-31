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
    public async Task ARoslynDeltaIsAcceptedByTheDesktopClrAndChangesALiveProcess()
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
            // The apply has to happen from a real break state, so the session breaks in the loop
            // rather than being async-interrupted — the same rule Visual Studio and Rider enforce
            // by only offering Apply Code Changes while paused.
            int loopLine = LineOf("File.AppendAllText");

            string launched = await backend.LaunchAsync(
                exe, [log], null, Path.GetDirectoryName(exe),
                [(sourcePath, loopLine)]);
            Assert.DoesNotContain("Error:", launched);

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
    private static async Task<bool> WaitForLastLineAsync(string log, string value)
    {
        for (int attempt = 0; attempt < 200; attempt++)
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
