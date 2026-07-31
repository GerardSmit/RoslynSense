using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;

namespace RoslynMCP.Daemon;

/// <summary>
/// Client-side: connects to the shared host for a solution, spawning it on demand. The spawn
/// is guarded by a global mutex so concurrent clients don't each launch a daemon.
/// </summary>
internal static class DaemonSpawner
{
    private static readonly TimeSpan ConnectProbe = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SpawnWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Returns a connected pipe to the host for <paramref name="solutionKey"/>, or <c>null</c>
    /// if the host could not be reached/started (caller should fall back to in-process).
    /// </summary>
    public static async Task<NamedPipeClientStream?> ConnectOrSpawnAsync(string solutionKey, CancellationToken ct)
    {
        string pipeName = HostPaths.PipeName(solutionKey);

        var pipe = await TryConnectAsync(pipeName, ct);
        if (pipe is not null)
            return pipe;

        // A named mutex has thread affinity: it must be released by the thread that took it.
        // Awaiting while holding one resumes the continuation on some other pool thread, and the
        // release then throws "Object synchronization method was called from an unsynchronized
        // block of code" — which was swallowed as "shared host unreachable", so every client
        // silently fell back to an in-process workspace instead of sharing the daemon's.
        // The whole guarded section therefore runs synchronously, on one thread of its own.
        return await Task.Run(() => ConnectOrSpawnGuarded(solutionKey, pipeName, ct), ct);
    }

    private static NamedPipeClientStream? ConnectOrSpawnGuarded(
        string solutionKey, string pipeName, CancellationToken ct)
    {
        using var mutex = new Mutex(false, HostPaths.SpawnMutexName(solutionKey));
        bool owned = false;
        try
        {
            try { owned = mutex.WaitOne(SpawnWait); }
            catch (AbandonedMutexException) { owned = true; }

            // Another client may have spawned it while we waited for the mutex.
            var pipe = TryConnect(pipeName, ct);
            if (pipe is not null)
                return pipe;

            if (!TrySpawnDaemon(solutionKey))
                return null;

            // Poll until the daemon's pipe is up.
            var deadline = DateTime.UtcNow + SpawnWait;
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                pipe = TryConnect(pipeName, ct);
                if (pipe is not null)
                    return pipe;
                ct.WaitHandle.WaitOne(150);
            }
            return null;
        }
        finally
        {
            if (owned) mutex.ReleaseMutex();
        }
    }

    /// <summary>The synchronous twin of <see cref="TryConnectAsync"/>, for use under the spawn
    /// mutex where an await would move the release to another thread.</summary>
    private static NamedPipeClientStream? TryConnect(string pipeName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            client.Connect((int)ConnectProbe.TotalMilliseconds);
            return client;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException)
        {
            client.Dispose();
            if (ex is OperationCanceledException) throw;
            return null;
        }
    }

    private static async Task<NamedPipeClientStream?> TryConnectAsync(string pipeName, CancellationToken ct)
    {
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await client.ConnectAsync((int)ConnectProbe.TotalMilliseconds, ct);
            return client;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException)
        {
            await client.DisposeAsync();
            if (ex is OperationCanceledException) throw;
            return null;
        }
    }

    private static bool TrySpawnDaemon(string solutionKey)
    {
        try
        {
            string exe = Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot determine current executable path.");

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                // Detach the daemon's standard streams so it never writes to the spawning
                // client's MCP stdout pipe. The daemon redirects its own Console to a log file.
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(solutionKey) ?? Environment.CurrentDirectory,
            };

            // When launched via `dotnet RoslynMCP.dll`, ProcessPath is the dotnet muxer; pass the dll.
            string exeName = Path.GetFileNameWithoutExtension(exe);
            if (exeName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                string? entryDll = Assembly.GetEntryAssembly()?.Location;
                if (!string.IsNullOrEmpty(entryDll))
                    psi.ArgumentList.Add(entryDll);
            }

            psi.ArgumentList.Add("--host");
            psi.ArgumentList.Add(solutionKey);

            var proc = Process.Start(psi);
            if (proc is null)
                return false;

            // Drain (and discard) the redirected streams so the OS pipe buffers never fill.
            // The daemon writes only to its log file after startup, so this stays near-empty.
            _ = proc.StandardOutput.ReadToEndAsync();
            _ = proc.StandardError.ReadToEndAsync();
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DaemonSpawner] Failed to spawn host: {ex.Message}");
            return false;
        }
    }
}
