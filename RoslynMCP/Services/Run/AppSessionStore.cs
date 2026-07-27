using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace RoslynMCP.Services.Run;

public enum AppSessionState
{
    Starting,
    Running,
    Exited,
    Stopped,
    Failed,
}

/// <summary>
/// A launched application and everything needed to observe or stop it.
/// </summary>
/// <remarks>
/// <see cref="BackgroundTaskStore"/> models finite tasks that start and complete; a run session is
/// open-ended, so it needs a process handle, a bounded output buffer, and explicit termination.
/// </remarks>
public sealed class AppSession : IDisposable
{
    /// <summary>How many output lines to retain. Enough to diagnose a startup failure without
    /// letting a chatty app grow unboundedly.</summary>
    private const int OutputLimit = 500;

    private readonly ConcurrentQueue<string> _output = new();
    private readonly Lock _gate = new();

    public required string Id { get; init; }
    public required string ProjectPath { get; init; }
    public required AppKind Kind { get; init; }
    public required Process Process { get; init; }
    public required DateTime StartedAtUtc { get; init; }

    public string? Url { get; init; }
    public int? Port { get; init; }
    public DebugRuntime DebugRuntime { get; init; }

    public AppSessionState State { get; private set; } = AppSessionState.Starting;
    public int? ExitCode { get; private set; }
    public DateTime? EndedAtUtc { get; private set; }

    public int Pid
    {
        get
        {
            try { return Process.Id; } catch { return -1; }
        }
    }

    public TimeSpan Uptime => (EndedAtUtc ?? DateTime.UtcNow) - StartedAtUtc;

    public void MarkRunning()
    {
        lock (_gate)
        {
            if (State == AppSessionState.Starting)
                State = AppSessionState.Running;
        }
    }

    public void MarkExited(int? exitCode)
    {
        lock (_gate)
        {
            if (State is AppSessionState.Exited or AppSessionState.Stopped)
                return;

            State = State == AppSessionState.Stopped ? AppSessionState.Stopped : AppSessionState.Exited;
            ExitCode = exitCode;
            EndedAtUtc = DateTime.UtcNow;
        }
    }

    public void MarkStopped()
    {
        lock (_gate)
        {
            State = AppSessionState.Stopped;
            EndedAtUtc ??= DateTime.UtcNow;
        }
    }

    public void MarkFailed(string reason)
    {
        lock (_gate)
        {
            State = AppSessionState.Failed;
            EndedAtUtc ??= DateTime.UtcNow;
        }

        Append($"[roslyn-sense] {reason}");
    }

    public void Append(string line)
    {
        _output.Enqueue(line);
        while (_output.Count > OutputLimit)
            _output.TryDequeue(out _);
    }

    /// <summary>Returns the most recent <paramref name="lines"/> lines of captured output.</summary>
    public string Tail(int lines)
    {
        var snapshot = _output.ToArray();
        var start = Math.Max(0, snapshot.Length - lines);

        var sb = new StringBuilder();
        for (var i = start; i < snapshot.Length; i++)
            sb.AppendLine(snapshot[i]);

        return sb.ToString();
    }

    public void Dispose()
    {
        try { Process.Dispose(); } catch { }
    }
}

/// <summary>
/// Holds the applications launched by this process.
/// </summary>
/// <remarks>
/// Sessions are per-chat, mirroring debug sessions: the tools that use this store are
/// <c>[InProcessOnly]</c>, so each client owns its own launched apps and they die with the client
/// rather than being orphaned.
/// </remarks>
public sealed class AppSessionStore : IDisposable
{
    private readonly ConcurrentDictionary<string, AppSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private int _counter;

    public AppSessionStore()
    {
        // A launched app is a child process that would outlive an abrupt shutdown otherwise.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
    }

    public string NextId(AppKind kind)
    {
        var suffix = Interlocked.Increment(ref _counter);
        var label = kind switch
        {
            AppKind.AspNetCore => "web",
            AppKind.AspNetClassic => "iis",
            AppKind.WindowsApp => "app",
            _ => "run",
        };
        return $"{label}-{suffix}";
    }

    public void Add(AppSession session) => _sessions[session.Id] = session;

    public AppSession? Get(string id) => _sessions.GetValueOrDefault(id);

    public IReadOnlyList<AppSession> All() =>
        [.. _sessions.Values.OrderByDescending(s => s.StartedAtUtc)];

    /// <summary>Sessions still holding a live process, for a given project.</summary>
    public IReadOnlyList<AppSession> LiveFor(string projectPath) =>
    [
        .. _sessions.Values.Where(s =>
            IsLive(s) &&
            string.Equals(s.ProjectPath, projectPath, StringComparison.OrdinalIgnoreCase))
    ];

    public static bool IsLive(AppSession session) =>
        session.State is AppSessionState.Starting or AppSessionState.Running;

    public bool Remove(string id) => _sessions.TryRemove(id, out _);

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
        {
            try
            {
                if (!session.Process.HasExited)
                    session.Process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The process may already be gone; nothing further to do.
            }

            session.Dispose();
        }

        _sessions.Clear();
    }
}
