using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using ModelContextProtocol.Server;
using RoslynMCP.Services;
using RoslynMCP.Services.Run;

namespace RoslynMCP.Tools;

/// <summary>
/// Profiles .NET application or test execution, returning the hottest methods by self-time.
/// Modern .NET is sampled with dotnet-trace (EventPipe); .NET Framework processes are sampled
/// with the free JetBrains dotTrace command-line profiler, feeding the same session store.
/// </summary>
[McpServerToolType]
public static class ProfileTool
{
    /// <summary>
    /// Profiles a .NET test project's execution to find CPU hotspots.
    /// </summary>
    [McpServerTool, Description(
        "Profile a .NET test project to find CPU hotspots. Runs tests under dotnet-trace " +
        "CPU sampling and returns the hottest methods by self-time. " +
        "The profile session is saved for follow-up investigation with ProfileSearchMethods, " +
        "ProfileCallers, ProfileCallees, and ProfileHotPaths. " +
        "Requires dotnet-trace (auto-installed if missing).")]
    public static async Task<string> ProfileTests(
        [Description("Path to the test project (.csproj) or a source file in the test project.")]
        string projectPath,
        IOutputFormatter fmt,
        ProfilingSessionStore store,
        [Description("Optional test filter expression (e.g. 'ClassName.MethodName').")]
        string? filter = null,
        [Description("Maximum profiling duration in seconds. Default: 120.")]
        int maxDurationSeconds = 120,
        [Description("Number of top methods to return. Default: 30.")]
        int maxResults = 30,
        [Description("Show only methods from the current solution's code, hiding framework and " +
                     "third-party methods. Default: true. Set false to include everything.")]
        bool ownCodeOnly = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var normalizedInput = PathHelper.NormalizePath(projectPath);
            var csprojPath = PathHelper.ResolveCsprojPath(projectPath);
            if (csprojPath is null)
                return $"Error: Could not find a .csproj file for '{projectPath}'.";

            if (PathHelper.IsSourceFile(normalizedInput))
                filter = PathHelper.BuildSourceFileFilter(normalizedInput, filter);

            if (PathHelper.RequiresMsBuild(csprojPath))
                return "Error: Test profiling is not supported for legacy .NET Framework test projects " +
                       "(dotnet-trace only supports .NET Core 3.0+ processes). " +
                       "Run the tests, then use ProfileProcess to attach to the test host by PID.";

            var testArgs = new StringBuilder();
            testArgs.Append("test \"");
            testArgs.Append(csprojPath);
            testArgs.Append("\" --no-build --verbosity quiet");

            if (!string.IsNullOrWhiteSpace(filter))
            {
                testArgs.Append(" --filter \"");
                testArgs.Append(filter.Replace("\"", "\\\""));
                testArgs.Append('"');
            }

            var description = $"dotnet test {Path.GetFileNameWithoutExtension(csprojPath)}";
            if (!string.IsNullOrWhiteSpace(filter))
                description += $" --filter {filter}";

            return await RunDotnetTraceAsync(
                $"-- dotnet {testArgs}",
                Path.GetDirectoryName(csprojPath)!,
                maxDurationSeconds, maxResults, description, hitUrls: null,
                ownCodeOnly ? CodeScope.OwnPrefixesForProject(csprojPath) : null,
                fmt, store, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ProfileTests] Unhandled error: {ex}");
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Profiles a .NET application's execution to find CPU hotspots.
    /// </summary>
    [McpServerTool, Description(
        "Profile a .NET application to find CPU hotspots and returns the hottest methods by " +
        "self-time. Modern .NET apps run under dotnet-trace CPU sampling; legacy .NET Framework " +
        "apps (including ASP.NET sites under IIS Express) are launched and sampled with the free " +
        "dotTrace command-line profiler. For web apps, pass hitUrls so the pages under " +
        "investigation are actually exercised during the profiling window. " +
        "The profile session is saved for follow-up investigation with ProfileSearchMethods, " +
        "ProfileCallers, ProfileCallees, and ProfileHotPaths. " +
        "Uses existing build output — build the project first.")]
    public static async Task<string> ProfileApp(
        [Description("Path to the project (.csproj) or a source file in the project.")]
        string projectPath,
        IOutputFormatter fmt,
        ProfilingSessionStore store,
        AppRunService runner,
        AppSessionStore sessions,
        [Description("Command-line arguments to pass to the application.")]
        string? appArgs = null,
        [Description("URLs to request repeatedly while profiling, semicolon-separated. " +
                     "Legacy web projects default to the app's root URL.")]
        string? hitUrls = null,
        [Description("Maximum profiling duration in seconds. Default: 30.")]
        int maxDurationSeconds = 30,
        [Description("Number of top methods to return. Default: 30.")]
        int maxResults = 30,
        [Description("Show only methods from the current solution's code, hiding framework and " +
                     "third-party methods. Default: true. Set false to include everything.")]
        bool ownCodeOnly = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var csprojPath = PathHelper.ResolveCsprojPath(projectPath);
            if (csprojPath is null)
                return $"Error: Could not find a .csproj file for '{projectPath}'.";

            var ownPrefixes = ownCodeOnly ? CodeScope.OwnPrefixesForProject(csprojPath) : null;

            if (PathHelper.RequiresMsBuild(csprojPath))
            {
                // The netfx launch goes through AppRunService (IIS Express or the built exe),
                // which has no argument pass-through; silently dropping them would mislead.
                if (!string.IsNullOrWhiteSpace(appArgs))
                    return "Error: 'appArgs' is not supported when profiling .NET Framework projects. " +
                           "Start the app yourself and use ProfileProcess with its PID instead.";

                return await ProfileNetFxAppAsync(
                    csprojPath, hitUrls, maxDurationSeconds, maxResults, ownPrefixes,
                    fmt, store, runner, sessions, cancellationToken);
            }

            var runArgs = new StringBuilder();
            runArgs.Append("run --project \"");
            runArgs.Append(csprojPath);
            runArgs.Append("\" --no-build");

            if (!string.IsNullOrWhiteSpace(appArgs))
            {
                runArgs.Append(" -- ");
                runArgs.Append(appArgs);
            }

            var description = $"dotnet run {Path.GetFileNameWithoutExtension(csprojPath)}";

            return await RunDotnetTraceAsync(
                $"-- dotnet {runArgs}",
                Path.GetDirectoryName(csprojPath)!,
                maxDurationSeconds, maxResults, description, hitUrls, ownPrefixes,
                fmt, store, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ProfileApp] Unhandled error: {ex}");
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Profiles an already-running .NET process by attaching to it.
    /// </summary>
    [McpServerTool, Description(
        "Profile an already-running .NET process by PID to find CPU hotspots. Works for both " +
        "modern .NET (dotnet-trace attach) and .NET Framework processes such as iisexpress.exe " +
        "or w3wp.exe (dotTrace attach) — use the PID returned by RunProject. For web apps, pass " +
        "hitUrls so the pages under investigation are exercised during the profiling window. " +
        "The profile session is saved for follow-up investigation with ProfileSearchMethods, " +
        "ProfileCallers, ProfileCallees, and ProfileHotPaths.")]
    public static async Task<string> ProfileProcess(
        [Description("PID of the process to attach to (e.g. from RunProject).")]
        int processId,
        IOutputFormatter fmt,
        ProfilingSessionStore store,
        [Description("URLs to request repeatedly while profiling, semicolon-separated.")]
        string? hitUrls = null,
        [Description("Profiling duration in seconds. Default: 30.")]
        int durationSeconds = 30,
        [Description("Number of top methods to return. Default: 30.")]
        int maxResults = 30,
        [Description("Show only methods from the current solution's code, hiding framework and " +
                     "third-party methods. Default: true. Set false to include everything.")]
        bool ownCodeOnly = true,
        [Description("Project or solution the process belongs to, used to decide what counts as " +
                     "own code. Defaults to the solution nearest the working directory.")]
        string? projectPath = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string processName;
            try
            {
                using var process = Process.GetProcessById(processId);
                processName = process.ProcessName;
            }
            catch (ArgumentException)
            {
                return $"Error: No process with PID {processId} is running.";
            }

            IReadOnlyList<string>? ownPrefixes = null;
            if (ownCodeOnly)
            {
                ownPrefixes = projectPath is not null && PathHelper.ResolveCsprojPath(projectPath) is { } csproj
                    ? CodeScope.OwnPrefixesForProject(csproj)
                    : CodeScope.OwnPrefixesForDirectory(Environment.CurrentDirectory);
            }

            var description = $"attach {processName} (pid {processId})";

            if (DebugRuntimeDetector.ForProcess(processId) == DebugRuntime.NetFramework)
                return await ProfileNetFxProcessAsync(
                    processId, hitUrls, durationSeconds, maxResults, ownPrefixes, description,
                    fmt, store, cancellationToken);

            return await RunDotnetTraceAsync(
                $"-p {processId}",
                Path.GetTempPath(),
                durationSeconds, maxResults, description, hitUrls, ownPrefixes,
                fmt, store, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ProfileProcess] Unhandled error: {ex}");
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Launches a .NET Framework app (console app directly, classic ASP.NET under IIS Express),
    /// profiles it with dotTrace, and stops it again.
    /// </summary>
    private static async Task<string> ProfileNetFxAppAsync(
        string csprojPath, string? hitUrls, int maxDurationSeconds, int maxResults,
        IReadOnlyList<string>? ownPrefixes,
        IOutputFormatter fmt, ProfilingSessionStore store,
        AppRunService runner, AppSessionStore sessions,
        CancellationToken cancellationToken)
    {
        var outcome = await runner.StartAsync(csprojPath, "Debug", null, null, cancellationToken);
        if (!outcome.Succeeded)
            return $"Error: {outcome.Error}\n\n" +
                   "If the app is already running (e.g. via RunProject), use ProfileProcess with its PID instead.";

        var session = outcome.Session!;
        try
        {
            if (!AppSessionStore.IsLive(session) || session.Process.HasExited)
            {
                return "Error: The application exited before profiling could start. Output:\n\n" +
                       session.Tail(40);
            }

            // Profiling an idle web app measures nothing; default to hammering the root URL.
            if (string.IsNullOrWhiteSpace(hitUrls) && session.Url is not null)
                hitUrls = session.Url;

            var description = $"dotTrace {Path.GetFileNameWithoutExtension(csprojPath)}";
            return await ProfileNetFxProcessAsync(
                session.Pid, hitUrls, maxDurationSeconds, maxResults, ownPrefixes, description,
                fmt, store, cancellationToken);
        }
        finally
        {
            await AppRunService.StopAsync(session);
            sessions.Remove(session.Id);
            session.Dispose();
        }
    }

    /// <summary>
    /// Attaches dotTrace to a running .NET Framework process, snapshots after the profiling
    /// window, converts the snapshot to XML with Reporter.exe, and stores the parsed session.
    /// </summary>
    private static async Task<string> ProfileNetFxProcessAsync(
        int pid, string? hitUrls, int durationSeconds, int maxResults,
        IReadOnlyList<string>? ownPrefixes, string description,
        IOutputFormatter fmt, ProfilingSessionStore store,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return "Error: .NET Framework profiling is only available on Windows.";

        var tools = await DotTraceService.FindOrProvisionAsync(cancellationToken);
        if (tools is null)
            return "Error: Could not find or download the dotTrace command-line tools. " +
                   "Install dotTrace, or set ROSLYNSENSE_DOTTRACE_DIR to a directory containing " +
                   "dottrace.exe and Reporter.exe.";

        var workingDirectory = Path.Combine(Path.GetTempPath(), $"roslyn-mcp-dottrace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);

        try
        {
            using var trafficCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var trafficTask = GenerateTrafficAsync(hitUrls, trafficCts.Token);

            string? snapshotPath, error;
            try
            {
                (snapshotPath, error) = await DotTraceService.AttachAndSnapshotAsync(
                    tools, pid, durationSeconds, workingDirectory, cancellationToken);
            }
            finally
            {
                trafficCts.Cancel();
                await trafficTask;
            }

            if (snapshotPath is null)
                return $"Error: {error}";

            var (reportPath, reportError) = await DotTraceService.GenerateReportAsync(
                tools, snapshotPath, cancellationToken);
            if (reportPath is null)
                return $"Error: {reportError}";

            // Parse everything; the own-code scope decides what is shown, not what is kept.
            var result = DotTraceReportParser.Parse(reportPath, int.MaxValue);
            if (result.Error is not null)
                return result.Error;

            string? sessionId = null;
            if (result.FrameNames is not null && result.Samples is not null && result.Weights is not null)
                sessionId = store.Store(description, result);

            return FormatResult(result, sessionId, ownPrefixes, maxResults, fmt);
        }
        finally
        {
            try { Directory.Delete(workingDirectory, recursive: true); } catch { }
        }
    }

    private static async Task<string> RunDotnetTraceAsync(
        string traceTarget, string workingDirectory,
        int maxDurationSeconds, int maxResults,
        string description, string? hitUrls,
        IReadOnlyList<string>? ownPrefixes,
        IOutputFormatter fmt,
        ProfilingSessionStore store,
        CancellationToken cancellationToken)
    {
        // Provision dotnet-trace
        var dotnetTracePath = await DebuggerService.FindOrProvisionDotnetTraceAsync(cancellationToken);
        if (dotnetTracePath is null)
            return "Error: Could not find or install dotnet-trace. Install it manually with: dotnet tool install -g dotnet-trace";

        var outputPath = Path.Combine(Path.GetTempPath(), $"roslyn-mcp-profile-{Guid.NewGuid():N}");

        try
        {
            // Build the dotnet-trace command
            // --format speedscope produces parseable JSON
            // --providers Microsoft-DotNET-SampleProfiler for CPU sampling
            var traceArgs = new StringBuilder();
            traceArgs.Append("collect --format speedscope");
            traceArgs.Append($" --output \"{outputPath}\"");
            traceArgs.Append(" --providers Microsoft-DotNET-SampleProfiler");
            if (maxDurationSeconds > 0)
            {
                var duration = TimeSpan.FromSeconds(maxDurationSeconds);
                traceArgs.Append($" --duration {duration:hh\\:mm\\:ss}");
            }
            traceArgs.Append($" {traceTarget}");

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = dotnetTracePath,
                    Arguments = traceArgs.ToString(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = workingDirectory
                }
            };

            process.StartInfo.Environment["MSBUILDTERMINALLOGGER"] = "off";

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null) stdout.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null) stderr.AppendLine(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var trafficCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var trafficTask = GenerateTrafficAsync(hitUrls, trafficCts.Token);

            try
            {
                // Give extra time beyond the trace duration for startup/shutdown
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(maxDurationSeconds + 60));
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return $"Profiling timed out after {maxDurationSeconds + 60} seconds.";
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw;
            }
            finally
            {
                trafficCts.Cancel();
                await trafficTask;
            }

            // dotnet-trace appends .speedscope.json to the output path
            var speedscopePath = outputPath + ".speedscope.json";
            if (!File.Exists(speedscopePath))
            {
                // Try alternate naming
                speedscopePath = outputPath + ".speedscope";
                if (!File.Exists(speedscopePath))
                {
                    // Check if it was created with the exact name
                    if (File.Exists(outputPath))
                        speedscopePath = outputPath;
                    else
                        return $"Error: No trace output was generated.\n\nstdout:\n{stdout}\n\nstderr:\n{stderr}";
                }
            }

            // Parse everything; the own-code scope decides what is shown, not what is kept.
            var result = SpeedscopeParser.Parse(speedscopePath, int.MaxValue);

            if (result.Error is not null)
                return result.Error;

            // Store session for follow-up investigation
            string? sessionId = null;
            if (result.FrameNames is not null && result.Samples is not null && result.Weights is not null)
                sessionId = store.Store(description, result);

            return FormatResult(result, sessionId, ownPrefixes, maxResults, fmt);
        }
        finally
        {
            // Clean up trace files
            foreach (var ext in new[] { "", ".speedscope.json", ".speedscope", ".nettrace" })
            {
                try { File.Delete(outputPath + ext); } catch { }
            }
        }
    }

    /// <summary>
    /// Requests the given URLs in a loop until cancelled, so a web app under profiling actually
    /// executes the code paths being investigated. Failures are ignored: a 500 still exercises
    /// the pipeline, and a refused connection just means the app is not ready yet.
    /// </summary>
    private static async Task GenerateTrafficAsync(string? hitUrls, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(hitUrls))
            return;

        var urls = hitUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (urls.Length == 0)
            return;

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var url in urls)
            {
                try
                {
                    using var response = await client.GetAsync(url, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    // Ignore request failures; the point is generating load, not asserting health.
                }
            }

            try { await Task.Delay(200, cancellationToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static string FormatResult(
        SpeedscopeParser.ProfilingResult result, string? sessionId,
        IReadOnlyList<string>? ownPrefixes, int maxResults, IOutputFormatter fmt)
    {
        var methods = result.HotMethods;
        int hidden = 0;

        // An empty prefix list means the scope could not be determined; show everything rather
        // than an empty table.
        if (ownPrefixes is { Count: > 0 })
        {
            (methods, hidden) = CodeScope.FilterOwn(methods, ownPrefixes);

            // Own methods often have little self-time (it sits in framework/native leaves), so
            // break self-time ties by subtree time to keep the entry points on top.
            methods.Sort((a, b) => b.SelfTimeMs != a.SelfTimeMs
                ? b.SelfTimeMs.CompareTo(a.SelfTimeMs)
                : b.TotalTimeMs.CompareTo(a.TotalTimeMs));
        }

        if (methods.Count > maxResults)
            methods = methods.GetRange(0, maxResults);

        var sb = new StringBuilder();
        fmt.AppendHeader(sb, "CPU Profile Results");
        fmt.AppendField(sb, "Total Duration", $"{result.TotalDurationMs:F1}ms");
        fmt.AppendField(sb, "Total Samples", result.TotalSamples);
        fmt.AppendField(sb, "Methods Shown", methods.Count);
        if (hidden > 0)
            fmt.AppendField(sb, "Scope", $"own code only — {hidden} framework/third-party methods hidden (ownCodeOnly=false to include)");
        if (sessionId is not null)
            fmt.AppendField(sb, "Session ID", sessionId);
        fmt.AppendSeparator(sb);

        if (methods.Count == 0)
        {
            fmt.AppendEmpty(sb, hidden > 0
                ? $"No solution methods were sampled ({hidden} framework/third-party methods hidden). " +
                  "The app may have been idle in own code — pass ownCodeOnly=false to see everything."
                : "No method samples were captured. The application may have exited too quickly.");
            return sb.ToString();
        }

        var columns = new[] { "#", "Self%", "Total%", "Self(ms)", "Method", "Module" };
        var rows = new List<string[]>();

        for (int i = 0; i < methods.Count; i++)
        {
            var m = methods[i];
            rows.Add([
                (i + 1).ToString(),
                $"{m.SelfPercent:F1}%",
                $"{m.TotalPercent:F1}%",
                $"{m.SelfTimeMs:F1}",
                m.Name,
                m.Module
            ]);
        }

        fmt.AppendTable(sb, "Hot Methods", columns, rows, methods.Count);

        var hints = new List<string>
        {
            "Methods with high Self% spend time in their own code",
            "Methods with high Total% are on hot call paths (callers of hot methods)",
            "Use GoToDefinition to navigate to a hot method's source code"
        };

        if (sessionId is not null)
        {
            hints.Add($"Use ProfileSearchMethods with session '{sessionId}' to search for specific methods");
            hints.Add($"Use ProfileCallers/ProfileCallees to investigate call relationships");
            hints.Add($"Use ProfileHotPaths to see the hottest execution paths through a method");
            if (hidden > 0)
                hints.Add("The session keeps ALL methods, including hidden ones — investigation tools search everything");
        }

        fmt.AppendHints(sb, [.. hints]);

        return sb.ToString();
    }
}
