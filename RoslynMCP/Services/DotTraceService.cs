using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace RoslynMCP.Services;

/// <summary>
/// Drives the free JetBrains dotTrace command-line profiler to CPU-profile .NET Framework
/// processes, which dotnet-trace (EventPipe) cannot reach. Attaches by PID in Sampling mode,
/// takes a snapshot on timeout, and converts it to XML with the bundled Reporter.exe.
/// </summary>
/// <remarks>
/// The console profiler and Reporter are a "JetBrains Redistributable Product": collecting and
/// reporting are free end-to-end; only opening snapshots in the dotTrace GUI needs a license.
/// Windows-only, matching the runtimes it exists for.
/// </remarks>
public static class DotTraceService
{
    public sealed record DotTraceTools(string DotTracePath, string ReporterPath);

    private static readonly string s_toolsDirectory = Path.Combine(
        Path.GetTempPath(), "RoslynMCP", "Tools", "dottrace");

    private const string NuGetPackageUrl =
        "https://www.nuget.org/api/v2/package/JetBrains.dotTrace.CommandLineTools.windows-x64";

    /// <summary>
    /// Finds the dotTrace command-line tools: an environment override, an installed
    /// dotTrace/Rider toolbox copy, our tools cache, or a fresh NuGet download, in that order.
    /// </summary>
    public static async Task<DotTraceTools?> FindOrProvisionAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        // 1. Explicit override for non-standard installs
        var overrideDir = Environment.GetEnvironmentVariable("ROSLYNSENSE_DOTTRACE_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir) && Probe(overrideDir) is { } fromOverride)
            return fromOverride;

        // 2. Installed dotTrace (standalone/Toolbox): %LOCALAPPDATA%\JetBrains\Installations\dotTrace*
        var installations = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JetBrains", "Installations");
        if (Directory.Exists(installations))
        {
            var newest = Directory.EnumerateDirectories(installations, "dotTrace*")
                .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Select(Probe)
                .FirstOrDefault(tools => tools is not null);
            if (newest is not null)
                return newest;
        }

        // 3. Our tools cache from a previous provisioning run
        var cached = Path.Combine(s_toolsDirectory, "tools");
        if (Probe(cached) is { } fromCache)
            return fromCache;

        // 4. Download the free command-line tools package from NuGet
        return await DownloadAsync(cancellationToken);
    }

    private static DotTraceTools? Probe(string directory)
    {
        var dottrace = Path.Combine(directory, "dottrace.exe");
        var reporter = Path.Combine(directory, "Reporter.exe");
        return File.Exists(dottrace) && File.Exists(reporter)
            ? new DotTraceTools(dottrace, reporter)
            : null;
    }

    private static async Task<DotTraceTools?> DownloadAsync(CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(s_toolsDirectory);
            var packagePath = Path.Combine(s_toolsDirectory, "package.nupkg");

            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            await using (var download = await client.GetStreamAsync(NuGetPackageUrl, cancellationToken))
            await using (var file = File.Create(packagePath))
            {
                await download.CopyToAsync(file, cancellationToken);
            }

            // The package carries the tools under tools\; extract just that folder.
            ZipFile.ExtractToDirectory(packagePath, s_toolsDirectory, overwriteFiles: true);
            try { File.Delete(packagePath); } catch { }

            return Probe(Path.Combine(s_toolsDirectory, "tools"));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Attaches the sampling profiler to <paramref name="pid"/>, waits for the timeout-triggered
    /// snapshot, and returns the snapshot path. Uses thread-cycle time so blocked/idle threads do
    /// not drown out actual CPU work.
    /// </summary>
    public static async Task<(string? SnapshotPath, string? Error)> AttachAndSnapshotAsync(
        DotTraceTools tools, int pid, int durationSeconds, string workingDirectory,
        CancellationToken cancellationToken)
    {
        var snapshotPath = Path.Combine(workingDirectory, "snapshot.dtp");

        var args = new StringBuilder();
        args.Append("attach ").Append(pid);
        args.Append(" --profiling-type=Sampling --time-measurement=ThreadCycleTime");
        args.Append(" --save-to=\"").Append(snapshotPath).Append('"');
        args.Append(" --overwrite --no-check-for-updates");
        args.Append(" --timeout=").Append(durationSeconds).Append('s');

        var (exitCode, output) = await RunAsync(
            tools.DotTracePath, args.ToString(), workingDirectory,
            // Attach + snapshot writing can take a while beyond the profiling window itself.
            TimeSpan.FromSeconds(durationSeconds + 120), cancellationToken);

        if (!File.Exists(snapshotPath))
        {
            var reason = exitCode == 0 ? "no snapshot was produced" : $"exit code {exitCode}";
            return (null, $"dotTrace attach failed ({reason}).\n\n{output}");
        }

        return (snapshotPath, null);
    }

    /// <summary>
    /// Converts a snapshot to XML with Reporter.exe, requesting full call stacks for every
    /// function so callers/callees/hot-paths can be reconstructed.
    /// </summary>
    public static async Task<(string? ReportPath, string? Error)> GenerateReportAsync(
        DotTraceTools tools, string snapshotPath, CancellationToken cancellationToken)
    {
        var workingDirectory = Path.GetDirectoryName(snapshotPath)!;
        var patternPath = Path.Combine(workingDirectory, "pattern.xml");
        var reportPath = Path.Combine(workingDirectory, "report.xml");

        // The pattern is a regex allow-list; ".*" with Full call stacks captures the entire
        // call tree (one Instance per node, OwnTime = node self-time).
        await File.WriteAllTextAsync(patternPath,
            """
            <Patterns>
              <Pattern PrintCallstacks="Full">.*</Pattern>
            </Patterns>
            """, cancellationToken);

        var args = $"report \"{snapshotPath}\" --pattern=\"{patternPath}\" " +
                   $"--save-to=\"{reportPath}\" --overwrite --no-check-for-updates";

        var (exitCode, output) = await RunAsync(
            tools.ReporterPath, args, workingDirectory, TimeSpan.FromMinutes(10), cancellationToken);

        if (exitCode != 0 || !File.Exists(reportPath))
            return (null, $"Reporter.exe failed (exit code {exitCode}).\n\n{output}");

        return (reportPath, null);
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(
        string fileName, string arguments, string workingDirectory,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            }
        };

        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            if (cancellationToken.IsCancellationRequested)
                throw;
            lock (output) output.AppendLine($"[roslyn-sense] Timed out after {timeout.TotalSeconds:0}s and was killed.");
            return (-1, output.ToString());
        }

        lock (output) return (process.ExitCode, output.ToString());
    }
}
