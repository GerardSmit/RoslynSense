using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

/// <summary>
/// Profiles .NET application or test execution using dotnet-trace CPU sampling,
/// returning the hottest methods by self-time.
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
                return "Error: Profiling is not supported for legacy .NET Framework projects. " +
                       "dotnet-trace only supports .NET Core 3.0+ processes.";

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

            return await RunProfileAsync(
                "dotnet", testArgs.ToString(),
                Path.GetDirectoryName(csprojPath)!,
                maxDurationSeconds, maxResults, description, fmt, store, cancellationToken);
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
        "Profile a .NET application to find CPU hotspots. Runs the app under dotnet-trace " +
        "CPU sampling and returns the hottest methods by self-time. " +
        "The profile session is saved for follow-up investigation with ProfileSearchMethods, " +
        "ProfileCallers, ProfileCallees, and ProfileHotPaths. " +
        "Requires dotnet-trace (auto-installed if missing).")]
    public static async Task<string> ProfileApp(
        [Description("Path to the project (.csproj) or a source file in the project.")]
        string projectPath,
        IOutputFormatter fmt,
        ProfilingSessionStore store,
        [Description("Command-line arguments to pass to the application.")]
        string? appArgs = null,
        [Description("Maximum profiling duration in seconds. Default: 30.")]
        int maxDurationSeconds = 30,
        [Description("Number of top methods to return. Default: 30.")]
        int maxResults = 30,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var csprojPath = PathHelper.ResolveCsprojPath(projectPath);
            if (csprojPath is null)
                return $"Error: Could not find a .csproj file for '{projectPath}'.";

            if (PathHelper.RequiresMsBuild(csprojPath))
                return "Error: Profiling is not supported for legacy .NET Framework projects. " +
                       "dotnet-trace only supports .NET Core 3.0+ processes.";

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

            return await RunProfileAsync(
                "dotnet", runArgs.ToString(),
                Path.GetDirectoryName(csprojPath)!,
                maxDurationSeconds, maxResults, description, fmt, store, cancellationToken);
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

    private static async Task<string> RunProfileAsync(
        string command, string arguments,
        string workingDirectory,
        int maxDurationSeconds, int maxResults,
        string description,
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
            traceArgs.Append($" -- {command} {arguments}");

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

            var result = SpeedscopeParser.Parse(speedscopePath, maxResults);

            if (result.Error is not null)
                return result.Error;

            // Store session for follow-up investigation
            string? sessionId = null;
            if (result.FrameNames is not null && result.Samples is not null && result.Weights is not null)
                sessionId = store.Store(description, result);

            return FormatResult(result, sessionId, fmt);
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

    private static string FormatResult(SpeedscopeParser.ProfilingResult result, string? sessionId, IOutputFormatter fmt)
    {
        var sb = new StringBuilder();
        fmt.AppendHeader(sb, "CPU Profile Results");
        fmt.AppendField(sb, "Total Duration", $"{result.TotalDurationMs:F1}ms");
        fmt.AppendField(sb, "Total Samples", result.TotalSamples);
        fmt.AppendField(sb, "Methods Shown", result.HotMethods.Count);
        if (sessionId is not null)
            fmt.AppendField(sb, "Session ID", sessionId);
        fmt.AppendSeparator(sb);

        if (result.HotMethods.Count == 0)
        {
            fmt.AppendEmpty(sb, "No method samples were captured. The application may have exited too quickly.");
            return sb.ToString();
        }

        var columns = new[] { "#", "Self%", "Total%", "Self(ms)", "Method", "Module" };
        var rows = new List<string[]>();

        for (int i = 0; i < result.HotMethods.Count; i++)
        {
            var m = result.HotMethods[i];
            rows.Add([
                (i + 1).ToString(),
                $"{m.SelfPercent:F1}%",
                $"{m.TotalPercent:F1}%",
                $"{m.SelfTimeMs:F1}",
                m.Name,
                m.Module
            ]);
        }

        fmt.AppendTable(sb, "Hot Methods", columns, rows, result.HotMethods.Count);

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
        }

        fmt.AppendHints(sb, [.. hints]);

        return sb.ToString();
    }
}
