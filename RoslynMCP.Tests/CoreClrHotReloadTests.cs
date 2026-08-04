using System.Diagnostics;
using RoslynMCP.Services.HotReload;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Hot reload on CoreCLR, end to end and through the product's own path.
/// </summary>
/// <remarks>
/// <para>
/// Everything else about this feature is tested in pieces — the launch environment, the module
/// identity, the agent's wire protocol against a fake on the other end. None of that answers the
/// only question that matters: does a running application actually change what it does. This does,
/// by building a real project, launching it, editing its source, and watching the number it prints
/// change without it restarting.
/// </para>
/// <para>
/// Deliberately through <see cref="HotReloadService"/> rather than a hand-built delta. Roslyn's
/// EnC engine constructs the baseline from the project's own PDB; a harness that stubs that out
/// proves nothing about the code that ships. It also means a failure here is a failure of the
/// product, which is the point of the test.
/// </para>
/// </remarks>
[Collection(DebuggerCollection.Name)]
public class CoreClrHotReloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hotreload-e2e-{Guid.NewGuid():N}");
    private readonly System.Text.StringBuilder _targetOutput = new();

    private static readonly string Project = $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net{Environment.Version.Major}.0</TargetFramework>
            <Nullable>disable</Nullable>
            <AssemblyName>HotReloadTarget</AssemblyName>
            <RootNamespace>HotReloadTarget</RootNamespace>
            <!-- Optimised code is not updatable; Debug is what hot reload is for anyway. -->
            <Optimize>false</Optimize>
            <DebugType>portable</DebugType>
          </PropertyGroup>
        </Project>
        """;

    private const string BaselineSource = """
        using System;
        using System.IO;
        using System.Threading;

        namespace HotReloadTarget
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

    [Fact]
    public async Task AnEditReachesTheRunningProcessWithoutRestartingIt()
    {
        Directory.CreateDirectory(_root);
        string csproj = Path.Combine(_root, "HotReloadTarget.csproj");
        string sourcePath = Path.Combine(_root, "Program.cs");
        string log = Path.Combine(_root, "values.txt");

        await File.WriteAllTextAsync(csproj, Project);
        await File.WriteAllTextAsync(sourcePath, BaselineSource);

        Assert.True(await BuildAsync(csproj), "The target project did not build.");

        string exe = Path.Combine(_root, "bin", "Debug", $"net{Environment.Version.Major}.0",
            OperatingSystem.IsWindows() ? "HotReloadTarget.exe" : "HotReloadTarget");
        Assert.True(File.Exists(exe), $"'{exe}' was not produced by the build.");

        using var target = Launch(exe, log);
        try
        {
            Assert.True(await WaitAsync(() => HotReloadAgentServer.Instance.Targets
                    .Any(t => t.ProcessId == target.Id)),
                "The hot reload agent never connected; nothing could have been applied." + Diagnose(target));

            Assert.True(await WaitForLastLineAsync(log, "6"),
                "The target never produced its baseline value." + Diagnose(target));

            // --- the edit ---

            var (session, message) = await HotReloadService.StartAsync(csproj);
            Assert.NotNull(session);

            await File.WriteAllTextAsync(sourcePath, BaselineSource.Replace("input * 2", "input * 10"));

            var outcome = await session!.ApplyAsync();

            Assert.True(outcome.Ok,
                $"{outcome.Summary}\n" +
                string.Join("\n", outcome.Diagnostics.Select(d => $"{d.Severity} {d.Id}: {d.Message}")) +
                string.Join("\n", outcome.Errors));

            Assert.Contains(outcome.AppliedTo, a => a.Contains(target.Id.ToString()));
            Assert.DoesNotContain("No changes", outcome.Summary);

            // The claim under test: the process that is already running now returns something else.
            Assert.True(await WaitForLastLineAsync(log, "30"),
                "The delta was reported as applied but the process kept returning the old value." + Diagnose(target));

            Assert.False(target.HasExited, "The process restarted; that would not be hot reload." + Diagnose(target));

            // --- a second edit, against the same session ---

            // The second apply is the regression that bit here: the workspace snapshot never
            // learns about the first edit, so a diff that forgets it would emit a delta
            // REVERTING it. The value must move forward to 60, not back to 6.
            await File.WriteAllTextAsync(sourcePath, BaselineSource.Replace("input * 2", "input * 20"));

            var second = await session!.ApplyAsync();
            Assert.True(second.Ok,
                $"{second.Summary}\n" +
                string.Join("\n", second.Diagnostics.Select(d => $"{d.Severity} {d.Id}: {d.Message}")) +
                string.Join("\n", second.Errors));
            Assert.Contains(second.AppliedTo, a => a.Contains(target.Id.ToString()));

            Assert.True(await WaitForLastLineAsync(log, "60"),
                "The second delta was reported as applied but the process kept the first edit's value." + Diagnose(target));
            Assert.False(target.HasExited, "The process restarted on the second apply." + Diagnose(target));
        }
        finally
        {
            HotReloadService.Get(csproj)?.Stop();
            try { if (!target.HasExited) target.Kill(entireProcessTree: true); } catch { }
        }
    }

    private Process Launch(string exe, string log)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(log);

        // The product's own preparation, not a test-local imitation: this is the thing that has to
        // work for hot reload to be reachable at all.
        Assert.True(HotReloadLauncher.Inject(startInfo), "The hot reload agent was not found.");

        var process = Process.Start(startInfo)!;

        // Both streams are redirected, so both must be read. A redirected pipe nobody drains fills
        // at about four kilobytes and then blocks the writer inside its next Console call — the
        // target stops looping, the log stops growing, and every wait below times out on a process
        // that is alive and stuck rather than slow. The agent writes to stderr on any apply it
        // cannot complete, which is why this only ever bit under load.
        process.OutputDataReceived += (_, e) => Capture(e.Data);
        process.ErrorDataReceived += (_, e) => Capture(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return process;
    }

    private void Capture(string? line)
    {
        if (line is null)
            return;

        lock (_targetOutput)
            _targetOutput.AppendLine(line);
    }

    /// <summary>What the target had to say, so a wait that expires says why rather than just that
    /// it did.</summary>
    private string Diagnose(Process target)
    {
        string captured;
        lock (_targetOutput)
            captured = _targetOutput.ToString();

        string state = target.HasExited
            ? $"The target exited with code {target.ExitCode}."
            : "The target was still running.";

        return captured.Length == 0
            ? $"\n{state} It wrote nothing."
            : $"\n{state} It wrote:\n{captured}";
    }

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

        return build.ExitCode == 0;
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, int attempts = 200)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (condition())
                return true;
            await Task.Delay(100);
        }
        return false;
    }

    /// <summary>Waits for the target's most recent write to be a given value. The last line, not
    /// any line: the file still holds every earlier value, so "contains" would pass before the
    /// edit landed.</summary>
    private static Task<bool> WaitForLastLineAsync(string log, string value) => WaitAsync(() =>
    {
        try
        {
            if (!File.Exists(log))
                return false;
            var lines = File.ReadLines(log).ToList();
            return lines.Count > 0 && lines[^1].Trim() == value;
        }
        catch (IOException)
        {
            return false; // the target appends while this reads
        }
    });
}
