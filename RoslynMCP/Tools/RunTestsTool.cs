using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
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
        "Run tests in a .NET test project. Set background=true to run in the background " +
        "and continue working — check results later with GetBackgroundTaskResult.")]
    public static async Task<string> RunTests(
        [Description("Path to the test project (.csproj) or a source file in the test project.")]
        string projectPath,
        IOutputFormatter fmt,
        BackgroundTaskStore taskStore,
        [Description("Optional test filter expression (e.g. 'ClassName.MethodName', " +
                     "'FullyQualifiedName~MyTest', 'Category=Unit'). " +
                     "If empty, all tests in the project are run.")]
        string? filter = null,
        [Description("Whether to build before running tests. Default is true.")]
        bool build = true,
        [Description("Set to true to run tests in the background. Returns a task ID immediately " +
                     "so you can continue working. Use GetBackgroundTaskResult to check results later.")]
        bool background = false,
        [Description("Timeout in seconds for the test run. Default is 300 (5 minutes). Set to 0 for no timeout.")]
        int timeoutSeconds = 300,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var csprojPath = ResolveCsprojPath(projectPath);
            if (csprojPath is null)
                return $"Error: Could not find a .csproj file for '{projectPath}'.";

            if (background)
                return BackgroundTaskHelper.StartTestsBackground(
                    csprojPath, filter, build, timeoutSeconds, taskStore);

            var trxPath = Path.Combine(Path.GetTempPath(), $"roslyn-mcp-{Guid.NewGuid():N}.trx");

            var args = new StringBuilder();
            args.Append("test ");
            args.Append('"');
            args.Append(csprojPath);
            args.Append('"');
            args.Append(" --verbosity normal");
            args.Append($" --logger \"trx;LogFileName={trxPath}\"");

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
                    : null;

                timeoutCts?.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

                await process.WaitForExitAsync(timeoutCts?.Token ?? cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                try { File.Delete(trxPath); } catch { }
                return $"Test run timed out after {timeoutSeconds} seconds.";
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                try { File.Delete(trxPath); } catch { }
                return "Test run was cancelled.";
            }

            string result;
            if (File.Exists(trxPath))
            {
                try
                {
                    result = FormatTrxOutput(trxPath, process.ExitCode, fmt);
                }
                catch
                {
                    result = FormatTestOutput(stdout.ToString(), stderr.ToString(), process.ExitCode, fmt);
                }
                finally
                {
                    try { File.Delete(trxPath); } catch { }
                }
            }
            else
            {
                result = FormatTestOutput(stdout.ToString(), stderr.ToString(), process.ExitCode, fmt);
            }

            return result;
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    internal static string? ResolveCsprojPath(string projectPath) =>
        PathHelper.ResolveCsprojPath(projectPath);

    internal static string FormatTestOutput(string stdout, string stderr, int exitCode, IOutputFormatter fmt)
    {
        var sb = new StringBuilder();

        if (exitCode == 0)
            fmt.AppendHeader(sb, "✅ Tests Passed");
        else
            fmt.AppendHeader(sb, "❌ Tests Failed");

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
            fmt.AppendSeparator(sb);
            fmt.AppendHeader(sb, "Failed Tests", 2);
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
                fmt.AppendHeader(sb, "Output", 2);
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
                fmt.AppendSeparator(sb);
                fmt.AppendHeader(sb, "Errors", 2);
                sb.AppendLine("```");
                sb.AppendLine(filtered.TrimEnd());
                sb.AppendLine("```");
            }
        }

        if (exitCode == 0)
            fmt.AppendHints(sb, "Use GetCoverage to see code coverage");
        else
            fmt.AppendHints(sb, "Use GetRoslynDiagnostics to check for compilation errors");

        return sb.ToString();
    }

    internal static string FormatTrxOutput(string trxPath, int exitCode, IOutputFormatter fmt)
    {
        var sb = new StringBuilder();
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var doc = XDocument.Load(trxPath);
        var results = doc.Descendants(ns + "UnitTestResult").ToList();

        int passed = results.Count(r => r.Attribute("outcome")?.Value == "Passed");
        int failed = results.Count(r => r.Attribute("outcome")?.Value == "Failed");
        int skipped = results.Count(r => r.Attribute("outcome")?.Value is "NotExecuted" or "Inconclusive");
        int total = results.Count;

        if (exitCode == 0)
            fmt.AppendHeader(sb, "✅ Tests Passed");
        else
            fmt.AppendHeader(sb, "❌ Tests Failed");

        fmt.AppendField(sb, "Total tests", total);
        fmt.AppendField(sb, "Passed", passed);
        if (failed > 0) fmt.AppendField(sb, "Failed", failed);
        if (skipped > 0) fmt.AppendField(sb, "Skipped", skipped);

        // Show failed tests with details
        var failedResults = results.Where(r => r.Attribute("outcome")?.Value == "Failed").ToList();
        if (failedResults.Count > 0)
        {
            fmt.AppendSeparator(sb);
            fmt.AppendHeader(sb, "Failed Tests", 2);
            foreach (var result in failedResults)
            {
                var testName = result.Attribute("testName")?.Value ?? "Unknown";
                var duration = result.Attribute("duration")?.Value;
                var output = result.Element(ns + "Output");
                var errorInfo = output?.Element(ns + "ErrorInfo");
                var message = errorInfo?.Element(ns + "Message")?.Value;
                var stackTrace = errorInfo?.Element(ns + "StackTrace")?.Value;

                fmt.AppendHeader(sb, fmt.Escape(testName), 3);
                if (duration is not null) fmt.AppendField(sb, "Duration", duration);
                if (!string.IsNullOrWhiteSpace(message))
                {
                    sb.AppendLine("```");
                    sb.AppendLine(message.Trim());
                    sb.AppendLine("```");
                }
                if (!string.IsNullOrWhiteSpace(stackTrace))
                {
                    sb.AppendLine("<details><summary>Stack trace</summary>");
                    sb.AppendLine();
                    sb.AppendLine("```");
                    sb.AppendLine(stackTrace.Trim());
                    sb.AppendLine("```");
                    sb.AppendLine("</details>");
                }
            }
        }

        if (exitCode == 0)
            fmt.AppendHints(sb, "Use GetCoverage to see code coverage");
        else
            fmt.AppendHints(sb, "Use GetRoslynDiagnostics to check for compilation errors");

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
