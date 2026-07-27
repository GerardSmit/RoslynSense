using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RoslynMCP.Debugger;

namespace RoslynMCP.Services.Memory;

/// <summary>
/// Captures managed heap statistics from live processes via <see cref="HeapAnalyzer"/> (ClrMD).
/// Works for both .NET Framework and modern .NET, which makes it the one memory-tracing path
/// that covers everything RunProject can launch.
/// </summary>
/// <remarks>
/// ClrMD cannot inspect across x86/x64, so a target whose bitness differs from this host is
/// analyzed through the bitness-matched debug worker (the same one ICorDebug debugging uses),
/// invoked in a one-shot mode that prints JSON. Same-bitness targets are walked in-process.
/// </remarks>
public static class MemorySnapshotService
{
    public record RootPath(string Root, IReadOnlyList<string> Chain);

    /// <summary>
    /// Walks the managed heap of <paramref name="pid"/> and aggregates object counts and sizes
    /// per type. The target process keeps running.
    /// </summary>
    public static async Task<MemorySnapshotStore.HeapSnapshot> CaptureAsync(
        int pid, string description, CancellationToken cancellationToken)
    {
        var stats = await DispatchAsync(
            pid,
            () => HeapAnalyzer.CaptureStats(pid, cancellationToken),
            ["--heap-snapshot", pid.ToString()],
            cancellationToken);

        return new MemorySnapshotStore.HeapSnapshot(
            MemorySnapshotStore.NewId(),
            description,
            DateTime.UtcNow,
            pid,
            stats.TotalHeapBytes,
            stats.ObjectCount,
            stats.Gen0Bytes, stats.Gen1Bytes, stats.Gen2Bytes,
            stats.LargeObjectBytes, stats.PinnedObjectBytes,
            stats.Types.ToDictionary(
                t => t.TypeName,
                t => new MemorySnapshotStore.TypeStat(t.TypeName, t.Count, t.TotalBytes)));
    }

    /// <summary>
    /// Finds why instances of a type are kept alive: walks GC roots to up to
    /// <paramref name="maxInstances"/> instances whose type name contains
    /// <paramref name="typeNameSubstring"/>.
    /// </summary>
    public static async Task<List<RootPath>> FindPathsToRootAsync(
        int pid, string typeNameSubstring, int maxInstances, CancellationToken cancellationToken)
    {
        var paths = await DispatchAsync(
            pid,
            () => HeapAnalyzer.FindPathsToRoot(pid, typeNameSubstring, maxInstances, cancellationToken),
            ["--heap-roots", pid.ToString(), typeNameSubstring, maxInstances.ToString()],
            cancellationToken);

        return paths.Select(p => new RootPath(p.Root, p.Chain)).ToList();
    }

    /// <summary>
    /// Runs the capture in-process when the target's bitness matches this host, otherwise in the
    /// bitness-matched debug worker.
    /// </summary>
    private static async Task<T> DispatchAsync<T>(
        int pid, Func<T> inProcess, string[] workerArgs, CancellationToken cancellationToken)
    {
        var targetArch = ProcessArch.OfProcess(pid);
        if (targetArch == ProcessArch.Host)
        {
            // ClrMD is a synchronous API; the walk of a large heap can take seconds.
            return await Task.Run(inProcess, cancellationToken);
        }

        var workerPath = DebugEngineFactory.FindWorker(targetArch);
        if (workerPath is null)
        {
            var host = ProcessArch.Host == DebugArch.X86 ? "32-bit" : "64-bit";
            var target = targetArch == DebugArch.X86 ? "32-bit" : "64-bit";
            throw new InvalidOperationException(
                $"Process {pid} is {target} but this host is {host}, and heap inspection cannot " +
                $"cross architectures. The matching worker was not found (expected under " +
                $"'workers/{(targetArch == DebugArch.X86 ? "x86" : "x64")}'). Either install it or " +
                $"run the target as {host}.");
        }

        return await RunWorkerAsync<T>(workerPath, workerArgs, cancellationToken);
    }

    private static async Task<T> RunWorkerAsync<T>(
        string workerPath, string[] args, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = workerPath,
                WorkingDirectory = Path.GetDirectoryName(workerPath) ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            }
        };

        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        // The worker is a different architecture from this process, so anything pinning this
        // process's runtime would send its apphost looking for a runtime of the wrong bitness and
        // it would exit immediately. Clear those and let the worker resolve its own.
        foreach (var variable in new[]
                 {
                     "DOTNET_ROOT", "DOTNET_ROOT(x86)", "DOTNET_ROOT_X86", "DOTNET_ROOT_X64",
                     "DOTNET_HOST_PATH", "DOTNET_MULTILEVEL_LOOKUP",
                     "MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildSDKsPath",
                 })
        {
            process.StartInfo.Environment.Remove(variable);
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        string output;
        lock (stdout) output = stdout.ToString();

        if (process.ExitCode != 0)
        {
            string error;
            lock (stderr) error = stderr.ToString().Trim();
            throw new InvalidOperationException(
                error.Length > 0 ? error : $"The heap worker exited with code {process.ExitCode}.");
        }

        var result = JsonSerializer.Deserialize<T>(output, HeapAnalyzer.JsonOptions);
        return result is null
            ? throw new InvalidOperationException("The heap worker produced no result.")
            : result;
    }
}
