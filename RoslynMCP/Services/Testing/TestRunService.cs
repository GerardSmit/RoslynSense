using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace RoslynMCP.Services.Testing;

/// <summary>Outcome of one `dotnet test` invocation.</summary>
public sealed record TestRunOutcome(
    int ExitCode,
    IReadOnlyList<TestResult> Results,
    string Output,
    string? Error)
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

        return new TestRunOutcome(
            process.ExitCode,
            results,
            stdout.ToString(),
            results.Count == 0 && process.ExitCode != 0 ? FirstBuildError(stdout.ToString(), stderr.ToString()) : null);
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
        var args = new StringBuilder("test ");
        args.Append('"').Append(csprojPath).Append('"');
        args.Append(" -c Debug");
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
