using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

[McpServerToolType]
public static class RunTestsTool
{
    /// <summary>
    /// Runs tests in a .NET test project.
    /// </summary>
    [McpServerTool, Description(
        "Run tests in a .NET test project. Provide the project path and optional filter.")]
    public static async Task<string> RunTests(
        [Description("Path to the test project (.csproj) or a source file in the test project.")]
        string projectPath,
        [Description("Optional test filter expression (e.g. 'ClassName.MethodName', " +
                     "'FullyQualifiedName~MyTest', 'Category=Unit'). " +
                     "If empty, all tests in the project are run.")]
        string? filter = null,
        [Description("Whether to build before running tests. Default is true.")]
        bool build = true,
        [Description("Timeout in seconds for the test run. Default is 300 (5 minutes). Set to 0 for no timeout.")]
        int timeoutSeconds = 300,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var csprojPath = ResolveCsprojPath(projectPath);
            if (csprojPath is null)
                return $"Error: Could not find a .csproj file for '{projectPath}'.";

            var args = new StringBuilder();
            args.Append("test ");
            args.Append('"');
            args.Append(csprojPath);
            args.Append('"');
            args.Append(" --verbosity normal");

            if (!build)
                args.Append(" --no-build");

            if (!string.IsNullOrWhiteSpace(filter))
            {
                args.Append(" --filter \"");
                args.Append(filter.Replace("\"", "\\\""));
                args.Append('"');
            }

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = args.ToString(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(csprojPath)
                }
            };

            // Disable terminal logger to get clean parseable output
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
                using var timeoutCts = timeoutSeconds > 0
                    ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                    : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                if (timeoutSeconds > 0)
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return $"Test run timed out after {timeoutSeconds} seconds.";
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return "Test run was cancelled.";
            }

            return FormatTestOutput(stdout.ToString(), stderr.ToString(), process.ExitCode);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    internal static string? ResolveCsprojPath(string projectPath)
    {
        var normalized = PathHelper.NormalizePath(projectPath);

        if (normalized.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && File.Exists(normalized))
            return normalized;

        if (File.Exists(normalized))
        {
            var dir = Path.GetDirectoryName(normalized);
            while (dir is not null)
            {
                var csprojs = Directory.GetFiles(dir, "*.csproj");
                if (csprojs.Length >= 1) return csprojs[0];
                dir = Path.GetDirectoryName(dir);
            }
        }

        if (Directory.Exists(normalized))
        {
            var csprojs = Directory.GetFiles(normalized, "*.csproj");
            if (csprojs.Length >= 1) return csprojs[0];
        }

        return null;
    }

    internal static string FormatTestOutput(string stdout, string stderr, int exitCode)
    {
        var sb = new StringBuilder();

        if (exitCode == 0)
        {
            sb.AppendLine("✅ **Tests Passed**");
        }
        else
        {
            sb.AppendLine("❌ **Tests Failed**");
        }

        sb.AppendLine();

        var lines = stdout.Split('\n');
        var failedTests = new List<string>();
        var currentFailure = new StringBuilder();
        var inFailure = false;
        var hasSummary = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            // Summary lines
            if (line.Contains("Total tests:", StringComparison.Ordinal) || line.Contains("Passed!", StringComparison.Ordinal) ||
                line.Contains("Failed!", StringComparison.Ordinal) || line.Contains("Test Run", StringComparison.Ordinal))
            {
                if (inFailure && currentFailure.Length > 0)
                {
                    failedTests.Add(currentFailure.ToString());
                    currentFailure.Clear();
                    inFailure = false;
                }
                hasSummary = true;
                sb.AppendLine(line.Trim());
                continue;
            }

            // New failed test starts
            if (line.Contains("Failed ", StringComparison.Ordinal) && line.Contains('['))
            {
                if (inFailure && currentFailure.Length > 0)
                    failedTests.Add(currentFailure.ToString());
                currentFailure.Clear();
                inFailure = true;
                currentFailure.AppendLine(line.Trim());
                continue;
            }

            if (inFailure)
            {
                // A "Passed" test line ends the failure block
                if (line.Contains("Passed ", StringComparison.Ordinal) && line.Contains('['))
                {
                    failedTests.Add(currentFailure.ToString());
                    currentFailure.Clear();
                    inFailure = false;
                    continue;
                }

                // Capture all indented or continuation lines as part of the failure
                if (!string.IsNullOrWhiteSpace(line))
                {
                    currentFailure.AppendLine(line.Trim());
                }
                else if (currentFailure.Length > 0)
                {
                    // Blank line within failure — keep it as separator
                    currentFailure.AppendLine();
                }
            }
        }

        if (inFailure && currentFailure.Length > 0)
            failedTests.Add(currentFailure.ToString());

        if (failedTests.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Failed Tests:**");
            foreach (var failure in failedTests)
            {
                sb.AppendLine("```");
                sb.AppendLine(failure.TrimEnd());
                sb.AppendLine("```");
            }
        }

        // When exit code is non-zero but we found no test failures or summary,
        // it's likely a build error — include raw output so the caller sees what happened.
        if (exitCode != 0 && failedTests.Count == 0 && !hasSummary)
        {
            var rawOutput = stdout.Trim();
            if (!string.IsNullOrEmpty(rawOutput))
            {
                sb.AppendLine("**Output:**");
                sb.AppendLine("```");
                sb.AppendLine(rawOutput);
                sb.AppendLine("```");
            }
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            var filtered = FilterStderr(stderr);
            if (!string.IsNullOrWhiteSpace(filtered))
            {
                sb.AppendLine();
                sb.AppendLine("**Errors:**");
                sb.AppendLine("```");
                sb.AppendLine(filtered.TrimEnd());
                sb.AppendLine("```");
            }
        }

        return sb.ToString();
    }

    private static string FilterStderr(string stderr)
    {
        var sb = new StringBuilder();
        foreach (var line in stderr.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            if (trimmed.Contains("VSTEST_HOST_DEBUG", StringComparison.Ordinal)) continue;
            // Keep build-related errors/warnings — they're useful diagnostics
            if (trimmed.StartsWith("Build started", StringComparison.OrdinalIgnoreCase)) continue;
            sb.AppendLine(trimmed);
        }
        return sb.ToString();
    }
}
