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
/// Something worth reporting while a run is still going.
/// </summary>
/// <param name="Kind">
/// <c>output</c> for a line of console output, or <c>passed</c>/<c>failed</c>/<c>skipped</c>
/// for a test that has just finished.
/// </param>
public sealed record TestProgress(
    string Kind,
    string? FullyQualifiedName = null,
    string? Message = null,
    double DurationMs = 0);

/// <summary>
/// Runs tests through `dotnet test` with a TRX logger and returns structured results.
/// Shared by the MCP tool (which formats markdown from this) and the editor's Test Explorer
/// (which maps it onto test items), so both agree on what ran and what it did.
/// </summary>
public static partial class TestRunService
{
    /// <param name="onProgress">
    /// Called as the run happens, on the process's output thread. The TRX is still the source
    /// of truth for the final results — this only exists so a long run is not a blank screen
    /// until it ends, which is what a test explorer that reports nothing until the last test
    /// amounts to.
    /// </param>
    public static async Task<TestRunOutcome> RunAsync(
        string csprojPath,
        string? filter = null,
        bool build = true,
        int timeoutSeconds = 300,
        CancellationToken cancellationToken = default,
        Action<TestProgress>? onProgress = null)
    {
        // On the project's shape, not its target framework. What the dotnet CLI cannot handle is a
        // *legacy* project — it has no SDK to resolve and needs full MSBuild. An SDK-style project
        // targeting net48 is the dotnet CLI's job and always was: sending it to Visual Studio's
        // MSBuild made it fail to resolve Microsoft.NET.Sdk, or load the .NET SDK's targets into
        // Framework MSBuild and die inside ResolvePackageAssets.
        if (ProjectClassifier.Classify(csprojPath).BuildTool == BuildTool.VisualStudioMsBuild)
            return await RunFrameworkAsync(csprojPath, filter, build, timeoutSeconds, cancellationToken, onProgress);

        string trxPath = Path.Combine(Path.GetTempPath(), $"roslyn-sense-{Guid.NewGuid():N}.trx");

        var args = new StringBuilder("test ");
        args.Append('"').Append(csprojPath).Append('"');
        args.Append(" --verbosity normal");
        args.Append($" --logger \"trx;LogFileName={trxPath}\"");
        // The console logger's verbosity is its own, not MSBuild's: without this it prints
        // failures only, and a run of passing tests reports nothing until it ends.
        args.Append(" --logger \"console;verbosity=normal\"");
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
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            stdout.AppendLine(e.Data);
            Report(onProgress, e.Data);
        };
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
        catch (OperationCanceledException)
        {
            // Killed on either kind of cancellation. Letting it run on after a cancel leaves a
            // test host holding the output assembly, which the next build then cannot write.
            try { process.Kill(entireProcessTree: true); } catch { }
            TryDelete(trxPath);

            if (cancellationToken.IsCancellationRequested)
                return new TestRunOutcome(-1, [], stdout.ToString(), "Test run cancelled.");

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
        CancellationToken cancellationToken, Action<TestProgress>? onProgress = null)
    {
        string? msbuild = MsBuildLocator.FindMsBuild();
        if (msbuild is null)
        {
            return new TestRunOutcome(-1, [], "",
                "This is a .NET Framework test project and Visual Studio's MSBuild was not found. " +
                "Install Visual Studio or the Build Tools for Visual Studio.");
        }

        string workingDirectory = Path.GetDirectoryName(csprojPath) ?? Environment.CurrentDirectory;
        using var timeout = timeoutSeconds > 0
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        timeout?.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var runToken = timeout?.Token ?? cancellationToken;

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

            try
            {
                var built = await RunBuildProcessAsync(buildInfo, runToken);
                if (built.ExitCode != 0)
                {
                    return new TestRunOutcome(built.ExitCode, [], built.Output,
                        FirstBuildError(built.Output, built.Error) ?? "The build failed.");
                }
            }
            catch (OperationCanceledException)
            {
                return new TestRunOutcome(-1, [], "", cancellationToken.IsCancellationRequested
                    ? "Test run cancelled."
                    : $"Test run timed out after {timeoutSeconds} seconds.");
            }
        }

        string? assembly;
        try
        {
            assembly = await GetFrameworkTargetPathAsync(msbuild, csprojPath, runToken);
        }
        catch (OperationCanceledException)
        {
            return new TestRunOutcome(-1, [], "", cancellationToken.IsCancellationRequested
                ? "Test run cancelled."
                : $"Test run timed out after {timeoutSeconds} seconds.");
        }
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
        args.Append(" /logger:\"console;verbosity=normal\"");

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
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            stdout.AppendLine(e.Data);
            Report(onProgress, e.Data);
        };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            // The build and test host share one deadline, like `dotnet test` does.
            await process.WaitForExitAsync(runToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            TryDelete(trxPath);

            if (cancellationToken.IsCancellationRequested)
                return new TestRunOutcome(-1, [], stdout.ToString(), "Test run cancelled.");

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

        var built = await RunBuildProcessAsync(startInfo, cancellationToken);
        if (built.ExitCode != 0)
            return (null, FirstBuildError(built.Output, built.Error) ?? "The build failed.");

        string? assembly = await GetFrameworkTargetPathAsync(msbuild, csprojPath, cancellationToken);
        return assembly is not null && File.Exists(assembly)
            ? (assembly, null)
            : (null, "Could not find the built test assembly.");
    }

    private static Task<string?> GetFrameworkTargetPathAsync(
        string msbuild, string csprojPath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(msbuild)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(csprojPath),
        };
        startInfo.ArgumentList.Add(csprojPath);
        startInfo.ArgumentList.Add("/nologo");
        startInfo.ArgumentList.Add("/v:minimal");
        startInfo.ArgumentList.Add(BuildProcessHelper.NoNodeReuseArg);
        startInfo.ArgumentList.Add("/getProperty:TargetPath");
        MsBuildLocator.SetVsEnvironment(startInfo, msbuild);
        return ReadFrameworkTargetPathAsync(startInfo, cancellationToken);
    }

    internal static async Task<string?> ReadFrameworkTargetPathAsync(
        ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        var evaluated = await RunBuildProcessAsync(startInfo, cancellationToken);
        // Preserve the existing property reader's tolerance for warnings before the value.
        return evaluated.ExitCode == 0
            ? evaluated.Output.Split('\n').Reverse().Select(line => line.Trim())
                .FirstOrDefault(line => line.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    || line.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            : null;
    }

    /// <summary>Captures a Framework build without blocking either redirected pipe.</summary>
    internal static async Task<(int ExitCode, string Output, string Error)> RunBuildProcessAsync(
        ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BuildProcessHelper.ConfigureMsBuildEnvironment(startInfo);
        using var process = new Process { StartInfo = startInfo };
        BuildProcessHelper.StartWithClosedInput(process);

        // MSBuild can fill stderr while stdout is still open. Both streams must drain from
        // the start, otherwise neither the process nor the first ReadToEnd can finish.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdout, stderr).WaitAsync(cancellationToken);
            return (process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            // Disposing Process only releases our handle. Stop MSBuild and its children,
            // then drain/reap them before a subsequent build can reuse their output files.
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            throw;
        }
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
            .Select(name => $"FullyQualifiedName={name}"));
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
        bool isLegacy =
            ProjectClassifier.Classify(csprojPath).BuildTool == BuildTool.VisualStudioMsBuild;

        var args = new StringBuilder();
        if (isLegacy)
        {
            // A legacy test project has to be built by MSBuild and run from its assembly;
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

    /// <summary>
    /// Turns one line of console output into a progress event.
    /// </summary>
    /// <remarks>
    /// vstest prints an outcome line as each test finishes, so the run can be reported live
    /// without a second logger or a streaming protocol. The TRX is still parsed at the end and
    /// still decides the result — this is a preview of it, and a line that does not parse is
    /// simply passed through as output rather than guessed at.
    /// </remarks>
    /// <summary>Drives <see cref="Report"/> over one line, for tests.</summary>
    internal static void ReportForTests(Action<TestProgress> onProgress, string line) =>
        Report(onProgress, line);

    private static void Report(Action<TestProgress>? onProgress, string line)
    {
        if (onProgress is null)
            return;

        var match = OutcomeLine().Match(line);
        if (!match.Success)
        {
            if (line.Trim().Length > 0)
                onProgress(new TestProgress("output", Message: line));
            return;
        }

        double duration = 0;
        if (match.Groups[3].Success
            && double.TryParse(match.Groups[3].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double value))
        {
            duration = match.Groups[4].Value switch
            {
                "s" => value * 1000,
                "m" => value * 60_000,
                _ => value,
            };
        }

        onProgress(new TestProgress(
            match.Groups[1].Value.ToLowerInvariant(), match.Groups[2].Value, DurationMs: duration));
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

    /// <summary>
    /// One finished test: "  Passed Some.Test.Method [12 ms]".
    /// </summary>
    /// <remarks>
    /// The summary lines this must not match are excluded by the whitespace after the outcome:
    /// "Passed!  - Failed: 0, …" has "!" there and "     Failed: 1" has ":".
    /// A theory's display name includes spaces and brackets in its arguments. Capture the
    /// whole name, leaving only a duration suffix at the end of the line out of it.
    /// </remarks>
    [GeneratedRegex(@"^\s*(Passed|Failed|Skipped)\s+([^\s!].*?)(?:\s+\[\s*(?:<\s*)?([\d.,]+)\s*(ms|s|m)\s*\])?\s*$")]
    private static partial Regex OutcomeLine();

    [GeneratedRegex(@"^.*: error [A-Za-z]+\d+:.*$", RegexOptions.Multiline)]
    private static partial Regex BuildError();
}
