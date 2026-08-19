using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RoslynMCP.Debugger;

/// <summary>
/// Builds Win32 <c>CreateProcess</c> Unicode environment blocks: the host environment merged with
/// per-launch overrides.
/// </summary>
public static class EnvBlock
{
    /// <summary>
    /// MSBuildLocator pins these to the SDK this process resolved. Leaking them into a debuggee
    /// would make any build it runs resolve the wrong MSBuild.
    /// </summary>
    private static readonly string[] LocatorKeys =
    [
        "MSBUILD_EXE_PATH",
        "MSBuildExtensionsPath",
        "MSBuildSDKsPath",
    ];

    /// <summary>Merges the current process environment with <paramref name="overrides"/>, which win.</summary>
    public static string Build(IReadOnlyDictionary<string, string>? overrides)
    {
        var baseEnv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
                baseEnv[key] = value;
        }

        foreach (var key in LocatorKeys)
            baseEnv.Remove(key);

        return BuildFrom(baseEnv, overrides ?? new Dictionary<string, string>());
    }

    /// <summary>
    /// Produces the <c>lpEnvironment</c> block: <c>name=value\0</c> entries sorted
    /// case-insensitively by name and closed by one extra NUL, so a populated block ends
    /// <c>\0\0</c>. Must be passed with <c>CREATE_UNICODE_ENVIRONMENT</c>.
    /// </summary>
    public static string BuildFrom(
        IReadOnlyDictionary<string, string> baseEnv, IReadOnlyDictionary<string, string> overrides)
    {
        var merged = new Dictionary<string, string>(baseEnv, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in overrides)
        {
            if (string.IsNullOrEmpty(key)) continue;
            merged[key] = value;
        }

        var sb = new StringBuilder();
        foreach (var (key, value) in merged.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            sb.Append(key).Append('=').Append(value).Append('\0');

        // An empty block is two NULs, not one.
        if (merged.Count == 0)
            sb.Append('\0');

        sb.Append('\0');
        return sb.ToString();
    }
}

/// <summary>
/// A debuggee launched suspended, with its output captured.
/// </summary>
/// <remarks>
/// The debugger must attach before the target executes any code, so the process is created with
/// <c>CREATE_SUSPENDED</c>, attached to, and only then resumed. .NET's <see cref="System.Diagnostics.Process"/>
/// cannot express that, hence the direct <c>CreateProcess</c> call.
/// </remarks>
public sealed class SuspendedProcess : IDisposable
{
    private readonly SafeProcessHandle _processHandle;
    private readonly IntPtr _threadHandle;
    private readonly StreamReader? _stdout;
    private readonly StreamReader? _stderr;
    private int _resumed;
    private int _disposed;

    public int ProcessId { get; }

    /// <summary>Raised for each line the debuggee writes to stdout or stderr.</summary>
    public event Action<string>? OutputReceived;

    private SuspendedProcess(
        SafeProcessHandle processHandle, IntPtr threadHandle, int processId,
        StreamReader? stdout, StreamReader? stderr)
    {
        _processHandle = processHandle;
        _threadHandle = threadHandle;
        ProcessId = processId;
        _stdout = stdout;
        _stderr = stderr;
    }

    public static SuspendedProcess Start(
        string commandLine,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Launching a suspended debuggee requires Windows.");

        var security = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            bInheritHandle = true,
        };

        CreatePipe(out var stdoutRead, out var stdoutWrite, security);
        CreatePipe(out var stderrRead, out var stderrWrite, security);

        var startupInfo = new STARTUPINFO
        {
            cb = Marshal.SizeOf<STARTUPINFO>(),
            dwFlags = STARTF_USESTDHANDLES,
            hStdOutput = stdoutWrite,
            hStdError = stderrWrite,
            hStdInput = IntPtr.Zero,
        };

        var envBlock = EnvBlock.Build(environment);

        // CreateProcess may modify lpCommandLine in place, so it must be a writable buffer.
        var commandLineBuffer = new StringBuilder(commandLine);

        var created = CreateProcess(
            null,
            commandLineBuffer,
            IntPtr.Zero,
            IntPtr.Zero,
            bInheritHandles: true,
            dwCreationFlags: CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW
                | CREATE_NEW_PROCESS_GROUP,
            lpEnvironment: envBlock,
            lpCurrentDirectory: workingDirectory,
            lpStartupInfo: ref startupInfo,
            lpProcessInformation: out var info);

        var error = Marshal.GetLastWin32Error();

        // The child owns the write ends now; the parent must close its copies or the readers
        // never see end-of-stream.
        CloseHandle(stdoutWrite);
        CloseHandle(stderrWrite);

        if (!created)
        {
            CloseHandle(stdoutRead);
            CloseHandle(stderrRead);
            throw new InvalidOperationException(
                $"CreateProcess failed for '{commandLine}' (Win32 error {error}).");
        }

        var process = new SuspendedProcess(
            new SafeProcessHandle(info.hProcess, ownsHandle: true),
            info.hThread,
            info.dwProcessId,
            OpenReader(stdoutRead),
            OpenReader(stderrRead));

        process.StartReaders();
        return process;
    }

    /// <summary>
    /// Asks the debuggee to shut down the way Ctrl+Break from a console would, so its shutdown
    /// path actually runs. Returns whether the signal was delivered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the only general way to ask a Windows process to exit gracefully: it raises the
    /// debuggee's console control handler, which is what <c>Console.CancelKeyPress</c> and, above
    /// it, the generic host's <c>ConsoleLifetime</c> listen to — so hosted services get their
    /// <c>StopAsync</c> instead of dying mid-call.
    /// </para>
    /// <para>
    /// The event goes to a process group, and <c>CREATE_NEW_PROCESS_GROUP</c> at launch made the
    /// debuggee the leader of its own — otherwise it would share this process's group and the
    /// signal would come back at us. Ctrl+Break rather than Ctrl+C because a new process group
    /// starts with Ctrl+C disabled.
    /// </para>
    /// <para>
    /// Delivering it means being attached to the debuggee's console, and a process can be attached
    /// to only one. A host with no console of its own — the server as the editor starts it, with
    /// its streams redirected — can simply borrow the debuggee's. A host that has one cannot:
    /// freeing it to make room would invalidate the handles its own stdio is built on, which is
    /// the MCP transport. That case borrows a throwaway process instead.
    /// </para>
    /// </remarks>
    public bool RequestShutdown()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        // Doubles as the "do we already own a console" test: it fails with ERROR_ACCESS_DENIED
        // when this process has one, and leaves ours untouched either way.
        if (AttachConsole(ProcessId))
        {
            try
            {
                return GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, (uint)ProcessId);
            }
            finally
            {
                FreeConsole();
            }
        }

        return SignalThroughHelper(ProcessId);
    }

    /// <summary>
    /// Raises the debuggee's console control handler from a short-lived process, for a host that
    /// cannot give up its own console to do it directly.
    /// </summary>
    /// <remarks>
    /// The helper is free to do what this process is not — drop the console it was born with and
    /// take the debuggee's — because it exists for the length of one API call. PowerShell hosts
    /// it because it is on every Windows machine, so a clean shutdown does not depend on a
    /// component of this product being deployed next to it.
    /// </remarks>
    private static bool SignalThroughHelper(int processId)
    {
        var script = Path.Combine(
            Path.GetTempPath(), $"roslynsense-shutdown-{Guid.NewGuid():N}.ps1");

        try
        {
            File.WriteAllText(script, HelperScript);

            using var helper = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList =
                {
                    "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                    "-File", script, processId.ToString(),
                },
            });

            if (helper is null)
                return false;

            return helper.WaitForExit(HelperTimeoutMilliseconds) && helper.ExitCode == 0;
        }
        catch
        {
            // No PowerShell, or it is locked down enough to refuse: the caller terminates instead.
            return false;
        }
        finally
        {
            try { File.Delete(script); } catch { /* best effort */ }
        }
    }

    /// <summary>Compiling the helper's P/Invokes costs most of this; the call itself is instant.</summary>
    private const int HelperTimeoutMilliseconds = 30_000;

    /// <summary>
    /// Drops this process's own console, takes the target's, and raises Ctrl+Break on the target's
    /// process group. The exit code says which step failed, so a caller can tell "no console to
    /// attach to" from "the signal was refused".
    /// </summary>
    private const string HelperScript =
        """
        param([Parameter(Mandatory = $true)][int]$TargetProcessId)

        Add-Type -TypeDefinition @'
        using System;
        using System.Runtime.InteropServices;

        public static class ShutdownSignal
        {
            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool FreeConsole();

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool AttachConsole(int dwProcessId);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

            public static int Send(int processId)
            {
                FreeConsole();
                if (!AttachConsole(processId))
                    return 2;
                return GenerateConsoleCtrlEvent(1, (uint)processId) ? 0 : 3;
            }
        }
        '@

        exit [ShutdownSignal]::Send($TargetProcessId)
        """;

    /// <summary>Waits for the debuggee to exit, returning false if it outlasts the timeout.</summary>
    public bool WaitForExit(TimeSpan timeout)
    {
        if (_processHandle.IsInvalid || _processHandle.IsClosed)
            return true;

        var milliseconds = (uint)Math.Max(0, Math.Min(timeout.TotalMilliseconds, uint.MaxValue - 1));
        return WaitForSingleObject(_processHandle, milliseconds) == WAIT_OBJECT_0;
    }

    /// <summary>Releases the debuggee's main thread. Safe to call more than once.</summary>
    public void ResumeMainThread()
    {
        if (Interlocked.Exchange(ref _resumed, 1) != 0)
            return;

        if (ResumeThread(_threadHandle) == unchecked((uint)-1))
            throw new InvalidOperationException(
                $"ResumeThread failed (Win32 error {Marshal.GetLastWin32Error()}).");
    }

    private void StartReaders()
    {
        Pump(_stdout);
        Pump(_stderr);

        void Pump(StreamReader? reader)
        {
            if (reader is null) return;

            var thread = new Thread(() =>
            {
                try
                {
                    while (reader.ReadLine() is { } line)
                        OutputReceived?.Invoke(line);
                }
                catch
                {
                    // The pipe closes when the debuggee exits; that ends the pump normally.
                }
            })
            {
                IsBackground = true,
                Name = $"debuggee-output-{ProcessId}",
            };

            thread.Start();
        }
    }

    private static StreamReader? OpenReader(IntPtr handle)
    {
        try
        {
            return new StreamReader(
                new FileStream(new SafeFileHandle(handle, ownsHandle: true), FileAccess.Read));
        }
        catch
        {
            return null;
        }
    }

    private static void CreatePipe(out IntPtr read, out IntPtr write, SECURITY_ATTRIBUTES security)
    {
        if (!CreatePipe(out read, out write, ref security, 0))
            throw new InvalidOperationException(
                $"CreatePipe failed (Win32 error {Marshal.GetLastWin32Error()}).");

        // Only the write end is inherited; a child holding the read end would deadlock the parent.
        SetHandleInformation(read, HANDLE_FLAG_INHERIT, 0);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try { _stdout?.Dispose(); } catch { }
        try { _stderr?.Dispose(); } catch { }
        try { if (_threadHandle != IntPtr.Zero) CloseHandle(_threadHandle); } catch { }
        try { _processHandle.Dispose(); } catch { }
    }

    // -------------------------------------------------------------------------
    // Win32
    // -------------------------------------------------------------------------

    private const uint CREATE_SUSPENDED = 0x00000004;
    private const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private const uint CTRL_BREAK_EVENT = 1;
    private const uint WAIT_OBJECT_0 = 0;
    private const int HANDLE_FLAG_INHERIT = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX, dwY, dwXSize, dwYSize;
        public int dwXCountChars, dwYCountChars, dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        [MarshalAs(UnmanagedType.LPWStr)] string lpEnvironment,
        string lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(
        out IntPtr hReadPipe, out IntPtr hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr hObject, int dwMask, int dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle hHandle, uint dwMilliseconds);
}
