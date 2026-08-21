using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.NETCore.Client;

namespace RoslynMCP.Services;

/// <summary>
/// Live profiling recordings started by ProfileStart and finished by ProfileStop, so the caller
/// can exercise the application (HTTP requests, UI actions) while data is being collected.
/// </summary>
/// <remarks>
/// Recordings hold child processes and open sessions, so the store kills everything it still
/// owns at process exit. A recording that hits its max duration stops collecting by itself; its
/// artifacts stay on disk until ProfileStop picks them up or the store is disposed.
/// </remarks>
public sealed class ProfileRecordingStore : IDisposable
{
    private readonly ConcurrentDictionary<string, ProfileRecording> _recordings = new(StringComparer.OrdinalIgnoreCase);
    private int _counter;

    public ProfileRecordingStore()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
    }

    public string NextId() => $"rec-{Interlocked.Increment(ref _counter)}";

    public void Add(ProfileRecording recording) => _recordings[recording.Id] = recording;

    public ProfileRecording? Get(string id) => _recordings.GetValueOrDefault(id);

    public bool Remove(string id) => _recordings.TryRemove(id, out _);

    public IReadOnlyList<ProfileRecording> All() =>
        [.. _recordings.Values.OrderBy(r => r.StartedAtUtc)];

    /// <summary>
    /// Resolves the recording to stop: by ID, or the single active one when no ID is given.
    /// </summary>
    public (ProfileRecording? Recording, string? Error) Resolve(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            var byId = Get(id);
            return byId is not null
                ? (byId, null)
                : (null, $"No recording '{id}'. Active: {DescribeActive()}");
        }

        var all = _recordings.Values.ToList();
        return all.Count switch
        {
            0 => (null, "No active recordings. Start one with ProfileStart."),
            1 => (all[0], null),
            _ => (null, $"Multiple recordings are active; pass recordingId. Active: {DescribeActive()}"),
        };
    }

    private string DescribeActive()
    {
        var all = _recordings.Values.ToList();
        return all.Count == 0 ? "none" : string.Join(", ", all.Select(r => r.Id));
    }

    public void Dispose()
    {
        foreach (var recording in _recordings.Values)
            recording.Dispose();
        _recordings.Clear();
    }
}

/// <summary>
/// One in-flight profiling recording. Concrete types implement the runtime-specific collection
/// and stop protocol; the result of a stop is always a parseable artifact on disk.
/// </summary>
public abstract class ProfileRecording : IDisposable
{
    public required string Id { get; init; }
    public required string Description { get; init; }
    public required int Pid { get; init; }
    public required string TempDir { get; init; }
    public required DateTime StartedAtUtc { get; init; }

    /// <summary>Project used to decide what counts as own code when formatting results.</summary>
    public string? ProjectPath { get; init; }

    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private string? _artifactPath;

    public TimeSpan Elapsed => DateTime.UtcNow - StartedAtUtc;

    /// <summary>"dotTrace report" or "speedscope" — tells the caller which parser to use.</summary>
    public abstract ProfileArtifactKind ArtifactKind { get; }

    /// <summary>
    /// Stops collection and returns the artifact to parse. Safe to call more than once; the
    /// first call does the work, later calls return the same artifact.
    /// </summary>
    public async Task<string> StopAndCollectAsync(CancellationToken cancellationToken)
    {
        await _stopGate.WaitAsync(cancellationToken);
        try
        {
            return _artifactPath ??= await StopCoreAsync(cancellationToken);
        }
        finally
        {
            _stopGate.Release();
        }
    }

    protected abstract Task<string> StopCoreAsync(CancellationToken cancellationToken);

    public virtual void Dispose()
    {
        _stopGate.Dispose();
        try { Directory.Delete(TempDir, recursive: true); } catch { }
    }
}

public enum ProfileArtifactKind
{
    /// <summary>A dotTrace snapshot; convert with Reporter.exe then DotTraceReportParser.</summary>
    DotTraceSnapshot,

    /// <summary>A speedscope JSON file; parse with SpeedscopeParser.</summary>
    Speedscope,
}

/// <summary>
/// .NET Framework recording: the dotTrace command-line profiler attached with
/// <c>--service-input=stdin</c>, stopped by sending <c>get-snapshot</c> + <c>disconnect</c>
/// service messages. dotTrace's own <c>--timeout</c> is the max-duration backstop: when it
/// fires, the snapshot is saved and the process exits without our involvement.
/// </summary>
public sealed partial class DotTraceRecording : ProfileRecording
{
    public required Process Process { get; init; }
    public required string SnapshotPath { get; init; }

    private readonly TaskCompletionSource<string> _snapshotSaved =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<string> _outputTail = new();

    public override ProfileArtifactKind ArtifactKind => ProfileArtifactKind.DotTraceSnapshot;

    /// <summary>Completes once dotTrace reports it is collecting data from the target.</summary>
    public Task Started => _started.Task;

    /// <summary>The last profiler output lines, for diagnosing a failed attach.</summary>
    public string OutputTail => string.Join(Environment.NewLine, _outputTail);

    /// <summary>Wire this to the process's stdout to observe dotTrace service messages.</summary>
    public void OnOutputLine(string line)
    {
        _outputTail.Enqueue(line);
        while (_outputTail.Count > 20)
            _outputTail.TryDequeue(out _);

        // ##dotTrace["snapshot-saved", {pid: 1234, filename:"..."}] — pseudo-JSON (unquoted
        // keys), so a regex is the honest parser here.
        if (!line.Contains("##dotTrace", StringComparison.Ordinal))
            return;

        if (line.Contains("\"started\"", StringComparison.Ordinal))
        {
            _started.TrySetResult();
        }
        else if (line.Contains("snapshot-saved", StringComparison.Ordinal))
        {
            var match = SnapshotFileRegex().Match(line);
            var path = match.Success ? match.Groups[1].Value.Replace(@"\\", @"\") : SnapshotPath;
            _snapshotSaved.TrySetResult(path);
        }
    }

    protected override async Task<string> StopCoreAsync(CancellationToken cancellationToken)
    {
        if (!Process.HasExited)
        {
            await SendAsync("##dotTrace[\"get-snapshot\"]", cancellationToken);

            using var snapshotTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            snapshotTimeout.CancelAfter(TimeSpan.FromMinutes(3));
            try
            {
                // A crashed profiler never reports a snapshot; racing against its exit fails
                // fast instead of sitting out the full timeout. Give the output pump a moment
                // to deliver a snapshot-saved that raced the exit.
                var exited = Process.WaitForExitAsync(snapshotTimeout.Token);
                var finished = await Task.WhenAny(_snapshotSaved.Task, exited);
                if (finished == exited && !_snapshotSaved.Task.IsCompleted)
                    await Task.WhenAny(_snapshotSaved.Task, Task.Delay(2000, snapshotTimeout.Token));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    "dotTrace did not report a saved snapshot within 3 minutes.");
            }

            await SendAsync("##dotTrace[\"disconnect\"]", cancellationToken);

            using var exitTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            exitTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await Process.WaitForExitAsync(exitTimeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { Process.Kill(entireProcessTree: true); } catch { }
            }
        }

        // Max-duration timeout already saved a snapshot and exited; prefer the reported file.
        var artifact = _snapshotSaved.Task.IsCompletedSuccessfully
            ? _snapshotSaved.Task.Result
            : SnapshotPath;

        if (!File.Exists(artifact))
            throw new InvalidOperationException(
                "No dotTrace snapshot was produced. The recording may have been idle, or the " +
                "profiler exited early — check that the target process is still running.");

        return artifact;
    }

    private async Task SendAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            // "Messages must always start with a new line and end with a carriage return!" —
            // and the reader only consumes a completed line, so close it with \r\n.
            await Process.StandardInput.WriteAsync($"\n{message}\r\n".AsMemory(), cancellationToken);
            await Process.StandardInput.FlushAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The profiler already exited (e.g. its --timeout fired); the artifact check decides.
        }
    }

    public override void Dispose()
    {
        try
        {
            if (!Process.HasExited)
                Process.Kill(entireProcessTree: true);
        }
        catch { }
        Process.Dispose();
        base.Dispose();
    }

    [GeneratedRegex(@"filename\s*:\s*""([^""]+)""")]
    private static partial Regex SnapshotFileRegex();
}

/// <summary>
/// Modern .NET recording: an EventPipe session opened directly through
/// <see cref="DiagnosticsClient"/>, streamed to a .nettrace file. Stop closes the session
/// (which flushes rundown so method names resolve) and converts to speedscope with
/// <c>dotnet-trace convert</c> — an offline command, unlike <c>collect</c>, which cannot be
/// stopped over redirected stdio.
/// </summary>
public sealed class EventPipeRecording : ProfileRecording
{
    public required EventPipeSession Session { get; init; }
    public required string NettracePath { get; init; }
    public required Task CopyTask { get; init; }
    public required string DotnetTracePath { get; init; }

    public override ProfileArtifactKind ArtifactKind => ProfileArtifactKind.Speedscope;

    /// <summary>Starts an EventPipe CPU-sampling recording against a CoreCLR process.</summary>
    public static EventPipeRecording Start(
        string id, string description, int pid, string tempDir, string? projectPath,
        string dotnetTracePath)
    {
        var client = new DiagnosticsClient(pid);
        var session = client.StartEventPipeSession(
            new EventPipeProvider("Microsoft-DotNETCore-SampleProfiler", System.Diagnostics.Tracing.EventLevel.Informational),
            requestRundown: true);

        var nettracePath = Path.Combine(tempDir, "recording.nettrace");
        var file = new FileStream(nettracePath, FileMode.Create, FileAccess.Write, FileShare.Read);

        // Drain continuously; EventPipeSession.Stop blocks until the stream is consumed.
        var copyTask = Task.Run(async () =>
        {
            await using (file)
                await Session_CopyAsync(session, file);
        });

        return new EventPipeRecording
        {
            Id = id,
            Description = description,
            Pid = pid,
            TempDir = tempDir,
            StartedAtUtc = DateTime.UtcNow,
            ProjectPath = projectPath,
            Session = session,
            NettracePath = nettracePath,
            CopyTask = copyTask,
            DotnetTracePath = dotnetTracePath,
        };

        static async Task Session_CopyAsync(EventPipeSession session, FileStream file)
        {
            try
            {
                await session.EventStream.CopyToAsync(file);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // The session ended (target exited or Stop raced the stream); the file holds
                // whatever was flushed, which is still convertible.
            }
        }
    }

    protected override async Task<string> StopCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(Session.Stop, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Already stopped, or the target process exited — the stream just ends.
            Console.Error.WriteLine($"[EventPipeRecording] Stop: {ex.Message}");
        }

        await CopyTask.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

        var info = new FileInfo(NettracePath);
        if (!info.Exists || info.Length == 0)
            throw new InvalidOperationException("The recording produced no trace data.");

        // dotnet-trace convert writes <output>.speedscope.json
        var outputBase = Path.Combine(TempDir, "recording");
        using var convert = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = DotnetTracePath,
                Arguments = $"convert \"{NettracePath}\" --format speedscope --output \"{outputBase}\"",
                WorkingDirectory = TempDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            }
        };

        var output = new StringBuilder();
        convert.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        convert.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        convert.Start();
        convert.BeginOutputReadLine();
        convert.BeginErrorReadLine();

        using var convertTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        convertTimeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await convert.WaitForExitAsync(convertTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { convert.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        var speedscopePath = outputBase + ".speedscope.json";
        if (!File.Exists(speedscopePath))
        {
            string log;
            lock (output) log = output.ToString();
            throw new InvalidOperationException($"dotnet-trace convert failed:\n{log}");
        }

        return speedscopePath;
    }

    public override void Dispose()
    {
        try { Session.Dispose(); } catch { }
        base.Dispose();
    }
}
