using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace RoslynMCP.Services.Testing;

/// <summary>Outcome of one `dotnet test` invocation.</summary>
public sealed record TestRunOutcome(
    int ExitCode,
    IReadOnlyList<TestResult> Results,
    string Output,
    string? Error,
    /// <summary>Identifies this run in <see cref="TestRunStore"/>; empty when nothing ran.</summary>
    string RunId = "")
{
    public bool TimedOut => Error is not null && Error.Contains("timed out", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Runs tests through `dotnet test` with a TRX logger and returns structured results.
/// Shared by the MCP tool (which formats markdown from this) and the editor's Test Explorer
/// (which maps it onto test items), so both agree on what ran and what it did.
/// </summary>
public static partial class TestRunService
{
    public static async Task<TestRunOutcome> RunAsync(
        string csprojPath,
        string? filter = null,
        bool build = true,
        int timeoutSeconds = 300,
        CancellationToken cancellationToken = default)
    {
        // `dotnet test` cannot run a .NET Framework test project: it drives a non-SDK project it
        // cannot build, and the test host it starts targets the wrong runtime. Those projects go
        // through MSBuild and vstest instead.
        if (ProjectClassifier.Classify(csprojPath).DebugRuntime == DebugRuntime.NetFramework)
            return await RunFrameworkAsync(csprojPath, filter, build, timeoutSeconds, cancellationToken);

        string trxPath = Path.Combine(Path.GetTempPath(), $"roslyn-sense-{Guid.NewGuid():N}.trx");

        var args = new StringBuilder("test ");
        args.Append('"').Append(csprojPath).Append('"');
        args.Append(" --verbosity normal");
        args.Append($" --logger \"trx;LogFileName={trxPath}\"");
        if (!build)
            args.Append(" --no-build");
        if (!string.IsNullOrWhiteSpace(filter))
            args.Append(" --filter \"").Append(filter.Replace("\"", "\\\"")).Append('"');

        var startInfo = new ProcessStartInfo("dotnet", args.ToString())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(csprojPath),
        };
        // The terminal logger rewrites lines in place, which is unparseable when captured.
        startInfo.Environment["MSBUILDTERMINALLOGGER"] = "off";

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            using var timeoutCts = timeoutSeconds > 0
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
            timeoutCts?.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            await process.WaitForExitAsync(timeoutCts?.Token ?? cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            TryDelete(trxPath);
            return new TestRunOutcome(-1, [], stdout.ToString(),
                $"Test run timed out after {timeoutSeconds} seconds.");
        }

        var results = TrxParser.Parse(trxPath);
        TryDelete(trxPath);

        // Recorded here rather than in the callers so every surface that runs tests — the MCP
        // tool, the Test Explorer, a CodeLens click — leaves the same trail to ask about later.
        string runId = "";
        if (results.Count > 0 && PathHelper.FindNearestSolution(csprojPath) is { } solution)
            runId = TestRunStore.Record(solution, csprojPath, results);

        return new TestRunOutcome(
            process.ExitCode,
            results,
            stdout.ToString(),
            results.Count == 0 && process.ExitCode != 0 ? FirstBuildError(stdout.ToString(), stderr.ToString()) : null,
            runId);
    }

    /// <summary>
    /// Builds a .NET Framework test project with Visual Studio's MSBuild and runs its assembly
    /// through vstest, which is the only combination that works for a non-SDK project.
    /// </summary>
    private static async Task<TestRunOutcome> RunFrameworkAsync(
        string csprojPath, string? filter, bool build, int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        string? msbuild = MsBuildLocator.FindMsBuild();
        if (msbuild is null)
        {
            return new TestRunOutcome(-1, [], "",
                "This is a .NET Framework test project and Visual Studio's MSBuild was not found. " +
                "Install Visual Studio or the Build Tools for Visual Studio.");
        }

        string workingDirectory = Path.GetDirectoryName(csprojPath) ?? Environment.CurrentDirectory;

        if (build)
        {
            var buildInfo = new ProcessStartInfo(msbuild, $"\"{csprojPath}\" /nologo /v:minimal")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory,
            };
            MsBuildLocator.SetVsEnvironment(buildInfo, msbuild);

            using var buildProcess = Process.Start(buildInfo);
            if (buildProcess is null)
                return new TestRunOutcome(-1, [], "", "Failed to start MSBuild.");

            string buildOutput = await buildProcess.StandardOutput.ReadToEndAsync(cancellationToken);
            string buildErrors = await buildProcess.StandardError.ReadToEndAsync(cancellationToken);
            await buildProcess.WaitForExitAsync(cancellationToken);

            if (buildProcess.ExitCode != 0)
            {
                return new TestRunOutcome(buildProcess.ExitCode, [], buildOutput,
                    FirstBuildError(buildOutput, buildErrors) ?? "The build failed.");
            }
        }

        string? assembly = MsBuildLocator.GetTargetPath(csprojPath);
        if (assembly is null || !File.Exists(assembly))
        {
            return new TestRunOutcome(-1, [], "",
                "Could not find the built test assembly. Build the project first.");
        }

        string trxPath = Path.Combine(Path.GetTempPath(), $"roslyn-sense-{Guid.NewGuid():N}.trx");
        var args = new StringBuilder($"vstest \"{assembly}\"");
        if (!string.IsNullOrWhiteSpace(filter))
            args.Append(" /TestCaseFilter:\"").Append(filter.Replace("\"", "\\\"")).Append('"');
        args.Append($" /logger:\"trx;LogFileName={trxPath}\"");

        var startInfo = new ProcessStartInfo("dotnet", args.ToString())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            using var timeout = timeoutSeconds > 0
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
            timeout?.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            await process.WaitForExitAsync(timeout?.Token ?? cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            TryDelete(trxPath);
            return new TestRunOutcome(-1, [], stdout.ToString(),
                $"Test run timed out after {timeoutSeconds} seconds.");
        }

        var results = TrxParser.Parse(trxPath);
        TryDelete(trxPath);

        string runId = "";
        if (results.Count > 0 && PathHelper.FindNearestSolution(csprojPath) is { } solution)
            runId = TestRunStore.Record(solution, csprojPath, results);

        return new TestRunOutcome(process.ExitCode, results, stdout.ToString(), null, runId);
    }

    /// <summary>Builds a Framework test project and returns the assembly vstest should run.</summary>
    private static async Task<(string? Assembly, string? Error)> BuildFrameworkTestAssemblyAsync(
        string csprojPath, CancellationToken cancellationToken)
    {
        string? msbuild = MsBuildLocator.FindMsBuild();
        if (msbuild is null)
        {
            return (null,
                "This is a .NET Framework test project and Visual Studio's MSBuild was not found. " +
                "Install Visual Studio or the Build Tools for Visual Studio.");
        }

        var startInfo = new ProcessStartInfo(msbuild, $"\"{csprojPath}\" /nologo /v:minimal")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(csprojPath),
        };
        MsBuildLocator.SetVsEnvironment(startInfo, msbuild);

        using var process = Process.Start(startInfo);
        if (process is null)
            return (null, "Failed to start MSBuild.");

        string stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        string stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
            return (null, FirstBuildError(stdout, stderr) ?? "The build failed.");

        string? assembly = MsBuildLocator.GetTargetPath(csprojPath);
        return assembly is not null && File.Exists(assembly)
            ? (assembly, null)
            : (null, "Could not find the built test assembly.");
    }

    /// <summary>
    /// Builds a filter matching exactly these tests. VSTest has no "any of these ids" syntax,
    /// so it becomes an OR of full-name equality clauses.
    /// </summary>
    public static string? BuildFilter(IReadOnlyList<string> fullyQualifiedNames)
    {
        if (fullyQualifiedNames.Count == 0)
            return null;

        return string.Join(" | ", fullyQualifiedNames
            .Distinct(StringComparer.Ordinal)
            .Select(name => $"FullyQualifiedName~{name}"));
    }

    /// <summary>
    /// Starts a test host suspended for debugging and returns its PID.
    /// VSTEST_HOST_DEBUG=1 makes the host print its process id and wait, which is the window
    /// the debugger needs: attaching before it resumes is what makes a breakpoint in the very
    /// first test reliable.
    /// </summary>
    public static async Task<(int ProcessId, string? Error)> StartForDebugAsync(
        string csprojPath, string? filter, CancellationToken cancellationToken = default)
    {
        bool isNetFramework =
            ProjectClassifier.Classify(csprojPath).DebugRuntime == DebugRuntime.NetFramework;

        var args = new StringBuilder();
        if (isNetFramework)
        {
            // A Framework test project has to be built by MSBuild and run from its assembly;
            // VSTEST_HOST_DEBUG then suspends the *host*, which is what gets attached to.
            var (assembly, error) = await BuildFrameworkTestAssemblyAsync(csprojPath, cancellationToken);
            if (assembly is null)
                return (0, error);

            args.Append("vstest \"").Append(assembly).Append('"');
            if (!string.IsNullOrWhiteSpace(filter))
                args.Append(" /TestCaseFilter:\"").Append(filter.Replace("\"", "\\\"")).Append('"');
        }
        else
        {
            args.Append("test ");
            args.Append('"').Append(csprojPath).Append('"');
            args.Append(" -c Debug");
            if (!string.IsNullOrWhiteSpace(filter))
                args.Append(" --filter \"").Append(filter.Replace("\"", "\\\"")).Append('"');
        }

        var startInfo = new ProcessStartInfo("dotnet", args.ToString())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(csprojPath),
        };
        startInfo.Environment["VSTEST_HOST_DEBUG"] = "1";
        startInfo.Environment["MSBUILDTERMINALLOGGER"] = "off";

        var process = Process.Start(startInfo);
        if (process is null)
            return (0, "Failed to start the test host.");

        var pidSource = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            var match = TestHostPid().Match(e.Data);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int pid))
                pidSource.TrySetResult(pid);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));

        try
        {
            return (await pidSource.Task.WaitAsync(timeout.Token), null);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (0, "The test host did not report a process id to attach to.");
        }
    }

    private static string? FirstBuildError(string stdout, string stderr)
    {
        var match = BuildError().Match(stdout + "\n" + stderr);
        return match.Success ? match.Value.Trim() : null;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    [GeneratedRegex(@"Process Id:\s*(\d+)")]
    private static partial Regex TestHostPid();

    [GeneratedRegex(@"^.*: error [A-Za-z]+\d+:.*$", RegexOptions.Multiline)]
    private static partial Regex BuildError();
}
