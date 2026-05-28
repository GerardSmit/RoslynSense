using System.Text;

namespace RoslynMCP.Daemon;

/// <summary>
/// Tracks in-flight client connections for a shared host and shuts it down after an idle
/// period with no clients. Also owns the per-host lock file (exclusive open) that advertises
/// liveness to would-be spawners and is auto-released when the process exits.
/// </summary>
internal sealed class DaemonLifecycle : IDisposable
{
    private readonly TimeSpan _idleTimeout;
    private readonly Action _onIdle;
    private readonly object _gate = new();
    private int _activeConnections;
    private Timer? _idleTimer;
    private FileStream? _lockStream;
    private bool _disposed;

    public DaemonLifecycle(TimeSpan idleTimeout, Action onIdle)
    {
        _idleTimeout = idleTimeout;
        _onIdle = onIdle;
    }

    /// <summary>
    /// Acquires the exclusive lock file. Throws <see cref="IOException"/> if another live
    /// daemon already holds it. Starts the idle timer so a daemon nobody connects to still exits.
    /// </summary>
    public void AcquireLock(string lockPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        _lockStream = new FileStream(lockPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        _lockStream.Write(Encoding.UTF8.GetBytes(Environment.ProcessId.ToString()));
        _lockStream.Flush();

        lock (_gate) StartIdleTimerLocked();
    }

    public void OnConnectionOpened()
    {
        lock (_gate)
        {
            _activeConnections++;
            _idleTimer?.Dispose();
            _idleTimer = null;
        }
    }

    public void OnConnectionClosed()
    {
        lock (_gate)
        {
            if (--_activeConnections <= 0)
            {
                _activeConnections = 0;
                StartIdleTimerLocked();
            }
        }
    }

    private void StartIdleTimerLocked()
    {
        if (_disposed) return;
        _idleTimer?.Dispose();
        _idleTimer = new Timer(_ =>
        {
            lock (_gate)
            {
                if (_activeConnections > 0) return;
            }
            _onIdle();
        }, null, _idleTimeout, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _idleTimer?.Dispose();
            _idleTimer = null;
        }
        _lockStream?.Dispose();
    }
}
