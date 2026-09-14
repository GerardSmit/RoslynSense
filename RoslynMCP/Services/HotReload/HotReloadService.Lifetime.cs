using System.Collections.Concurrent;
using System.Diagnostics;
using RoslynMCP.Services.Memory;

namespace RoslynMCP.Services.HotReload;

internal sealed partial class HotReloadService : IMemoryCache
{
    internal sealed record SessionOwner(string Id, int? Pid, long? Started)
    {
        public static SessionOwner Create(string id, int? pid)
        {
            if (pid is null) return new(id, null, null);
            using var process = Process.GetProcessById(pid.Value);
            return new(id, pid, process.StartTime.ToUniversalTime().Ticks);
        }
        public bool HasExited()
        {
            if (Pid is null) return false;
            try
            {
                using var process = Process.GetProcessById(Pid.Value);
                return process.HasExited || process.StartTime.ToUniversalTime().Ticks != Started;
            }
            catch (ArgumentException) { return true; }
            catch (InvalidOperationException) { return true; }
            catch (System.ComponentModel.Win32Exception) { return false; }
        }
    }

    private readonly ConcurrentDictionary<string, SessionOwner> _owners = new(StringComparer.Ordinal);
    private volatile bool _stopping;
    private int _reaping, _endFailures;
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    internal int OwnerCount => _owners.Count;

    public async Task StopAsync()
    {
        await s_startGate.WaitAsync().ConfigureAwait(false);
        try { await EndLockedAsync().ConfigureAwait(false); }
        finally { s_startGate.Release(); }
    }

    private async Task EndLockedAsync()
    {
        if (!s_sessions.TryGetValue(_projectPath, out var current) || !ReferenceEquals(current, this)) return;
        _stopping = true;
        await _applyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _encService.EndSession();
            s_sessions.TryRemove(new KeyValuePair<string, HotReloadService>(_projectPath, this));
            _owners.Clear();
            _stamps.Clear();
            _texts.Clear();
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _endFailures);
            Console.Error.WriteLine($"[HotReload] Could not end session {SessionId}: {ex.Message}");
            throw;
        }
        finally { _applyGate.Release(); }
    }

    public static async Task ReleaseOwnerAsync(string projectPath, string ownerId)
    {
        await s_startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!s_sessions.TryGetValue(PathHelper.NormalizePath(projectPath), out var session)) return;
            if (!session._owners.TryRemove(ownerId, out _)) return;
            if (session._owners.IsEmpty) await session.EndLockedAsync().ConfigureAwait(false);
        }
        finally { s_startGate.Release(); }
    }

    public static async Task ReleaseClientAsync(string prefix)
    {
        foreach (var session in s_sessions.Values)
            foreach (var owner in session._owners.Values)
                if (owner.Id.StartsWith(prefix, StringComparison.Ordinal) && (owner.Pid is null || owner.HasExited()))
                    await ReleaseOwnerAsync(session.ProjectPath, owner.Id).ConfigureAwait(false);
    }

    public static async Task StopAllAsync()
    {
        List<Exception> failures = [];
        foreach (var session in s_sessions.Values.ToArray())
        {
            try { await session.StopAsync().ConfigureAwait(false); }
            catch (Exception ex) { failures.Add(ex); }
        }
        if (failures.Count > 0) throw new AggregateException("Hot Reload shutdown failed.", failures);
    }

    internal static object[] MemoryInventory() => s_sessions.Values.Select(session => (object)new
    {
        session.SessionId, session.ProjectPath, State = session._stopping ? "Stopping" : "Active",
        EndFailures = Volatile.Read(ref session._endFailures),
        Owners = session._owners.Values.Select(owner => new { owner.Id, owner.Pid, owner.Started }).ToArray(),
    }).ToArray();

    public CacheMemoryInfo Inspect() => new("hotReload:" + SessionId, _owners.Count,
        ReferenceEquals(Get(_projectPath), this) ? 1 : 0, _owners.Count);
    public void Trim()
    {
        if (Interlocked.CompareExchange(ref _reaping, 1, 0) != 0) return;
        _ = ReapAsync();
    }
    private async Task ReapAsync()
    {
        try
        {
            foreach (var owner in _owners.Values)
                if (owner.HasExited()) await ReleaseOwnerAsync(_projectPath, owner.Id).ConfigureAwait(false);
            if (_stopping && _endFailures < 3) await StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[HotReload] Cleanup: {ex.Message}"); }
        finally { Volatile.Write(ref _reaping, 0); }
    }
}
