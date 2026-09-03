using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RoslynMCP.Debugger;

/// <summary>
/// Notices when the session thread stops coming back, and takes a dump of the process while the
/// evidence is still there.
/// </summary>
/// <remarks>
/// <para>
/// Everything a client asks of this engine is marshalled onto one thread, and every ICorDebug call
/// is made from it. That is what makes the engine safe, and it is also what makes a single stuck
/// call indistinguishable from a busy session: no further command runs, no event is emitted, and
/// the only thing anyone can say afterwards is that it wedged.
/// </para>
/// <para>
/// A dump turns that into something readable. It costs nothing while the session is healthy — one
/// timestamp written per command — and it never intervenes: a session suspected of hanging is one
/// this reports on, not one it kills. Being wrong therefore costs a file and a line of narration,
/// which is why the patience can be generous and the whole thing left on.
/// </para>
/// </remarks>
public sealed class SessionWatchdog : IDisposable
{
    /// <summary>Turns the watchdog off entirely.</summary>
    public const string DisableVariable = "ROSLYNMCP_DEBUG_NO_WATCHDOG";

    /// <summary>How long one command may run before it is presumed stuck, in seconds.</summary>
    public const string PatienceVariable = "ROSLYNMCP_DEBUG_WATCHDOG_SECONDS";

    /// <summary>
    /// The default patience.
    /// </summary>
    /// <remarks>
    /// Long enough that nothing a working session does reaches it — a launch that has to start a
    /// runtime, an evaluation that runs the debuggee's own code, an attach to a process with a
    /// thousand modules — because the cost of being early is a dump nobody needed and a line of
    /// narration that says the session is fine after all.
    /// </remarks>
    private static readonly TimeSpan DefaultPatience = TimeSpan.FromSeconds(90);

    /// <summary>How often to look. A stuck session stays stuck, so this only decides how quickly
    /// the report arrives, not whether it does.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How many dumps one session may produce.
    /// </summary>
    /// <remarks>
    /// A wedged session stays wedged, and a watchdog that noticed that once a tick would fill the
    /// disk with copies of the same stack. One is the diagnosis; the rest are the same diagnosis.
    /// </remarks>
    private const int MaxDumps = 1;

    private readonly Action<string> _report;
    private readonly TimeSpan _patience;
    private readonly Timer? _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>What the session thread is doing, or null when it is waiting for work.</summary>
    private volatile string? _running;

    /// <summary>When the current command started, by <see cref="_clock"/>.</summary>
    private long _startedAt;

    private int _dumpsTaken;
    private bool _reported;

    public SessionWatchdog(Action<string> report)
    {
        _report = report;
        _patience = PatienceFromEnvironment();

        if (Environment.GetEnvironmentVariable(DisableVariable) is { Length: > 0 })
            return;

        _timer = new Timer(_ => Check(), null, Interval, Interval);
    }

    /// <summary>Marks the start of a command, and the end of the one before it.</summary>
    /// <remarks>
    /// Called from the session thread's own loop, which is what makes it free: it is a field write
    /// on the thread being watched, not a probe sent to it. A probe would have to queue behind
    /// whatever is stuck and could only ever confirm what the timestamp already says.
    /// </remarks>
    public void Starting(string what)
    {
        Interlocked.Exchange(ref _startedAt, _clock.ElapsedMilliseconds);
        _running = what;
    }

    /// <summary>Marks the session thread as back to waiting for work.</summary>
    public void Finished()
    {
        _running = null;

        // A session that recovered is worth saying so about: the report that it had stopped
        // responding is otherwise the last thing anybody reads about it.
        if (_reported)
        {
            _reported = false;
            _report("the debug session started responding again");
        }
    }

    private void Check()
    {
        if (_running is not { Length: > 0 } what)
            return;

        var elapsed = TimeSpan.FromMilliseconds(_clock.ElapsedMilliseconds - Interlocked.Read(ref _startedAt));
        if (elapsed < _patience || _reported)
            return;

        _reported = true;
        var dump = _dumpsTaken < MaxDumps && Interlocked.Increment(ref _dumpsTaken) <= MaxDumps
            ? TryWriteDump()
            : null;

        _report(
            $"the debug session has not responded for {(int)elapsed.TotalSeconds}s while it was " +
            $"{what}" +
            (dump is { Length: > 0 }
                ? $"; a dump of the debugger was written to {dump}"
                : "; no dump could be written"));
    }

    private static TimeSpan PatienceFromEnvironment()
    {
        var configured = Environment.GetEnvironmentVariable(PatienceVariable);
        return int.TryParse(configured, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : DefaultPatience;
    }

    /// <summary>
    /// Writes a dump of this process — the debugger, not the debuggee.
    /// </summary>
    /// <remarks>
    /// The debuggee is not the one that stopped answering, and its state is readable through the
    /// debugger anyway. What nobody can see from outside is which ICorDebug call this process is
    /// blocked in, and that is what a dump of this process is for.
    /// </remarks>
    private string? TryWriteDump()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "roslyn-sense", "debug-dumps");
            Directory.CreateDirectory(directory);

            using var process = Process.GetCurrentProcess();
            var path = Path.Combine(
                directory,
                $"debug-session-{process.Id}-{DateTime.Now:yyyyMMdd-HHmmss}.dmp");

            using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
            bool written = MiniDumpWriteDump(
                process.Handle,
                process.Id,
                file.SafeFileHandle.DangerousGetHandle(),
                // With the thread's stacks and enough of the heap to read what they point at.
                // A full-memory dump of a process holding a debuggee's symbols is enormous, and
                // the question here is only ever "which call is this thread inside".
                MiniDumpWithIndirectlyReferencedMemory | MiniDumpWithThreadInfo,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            if (written)
                return path;

            // A half-written dump is worse than none: it looks like evidence and reads as garbage.
            file.Dispose();
            try { File.Delete(path); } catch { }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private const int MiniDumpWithIndirectlyReferencedMemory = 0x00000040;
    private const int MiniDumpWithThreadInfo = 0x00001000;

    [SupportedOSPlatform("windows")]
    [DllImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(
        IntPtr hProcess,
        int processId,
        IntPtr hFile,
        int dumpType,
        IntPtr exceptionParam,
        IntPtr userStreamParam,
        IntPtr callbackParam);

    public void Dispose() => _timer?.Dispose();
}
