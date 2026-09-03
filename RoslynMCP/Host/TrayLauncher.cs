using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace RoslynMCP.Daemon;

/// <summary>
/// Starts the Windows tray icon, if it isn't already up, when a host comes online.
/// </summary>
/// <remarks>
/// The daemon is deliberately invisible — spawned on demand, no window, self-terminating — which
/// is right for a tool the editor drives but leaves the user with no way to tell that a host is
/// loaded, or which apps it has running. The host is the only party that knows the moment one
/// starts, so it is what brings the tray up; the tray then outlives any single host and steps down
/// on its own once nothing is left to show.
/// </remarks>
internal static class TrayLauncher
{
    /// <summary>Session-scoped, not <c>Global\</c>: a tray icon belongs to one interactive
    /// session, so two logged-on users each get their own.</summary>
    private const string SingleInstanceMutex = @"Local\RoslynSenseTray";

    private const string TrayExeName = "RoslynSense.Tray.exe";

    public static void EnsureRunning()
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive)
            return;

        try
        {
            // Cheap negative check. Losing this race only costs a process start: the duplicate
            // fails to take the mutex and exits before showing an icon.
            if (Mutex.TryOpenExisting(SingleInstanceMutex, out var existing))
            {
                existing.Dispose();
                return;
            }

            string exe = Path.Combine(AppContext.BaseDirectory, "tray", TrayExeName);
            if (!File.Exists(exe))
                return; // not shipped in this build (non-Windows publish)

            if (TryStartUnderShell(exe))
                return;

            // Fallback: a child of this host. The tray copes — it degrades "stop host" to a
            // single-process kill when it finds itself inside the tree it was asked to fell.
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            });
        }
        catch (Exception ex)
        {
            // A missing desktop, a blocked launch — the host's job is unaffected either way.
            Console.Error.WriteLine($"[Daemon] Tray icon not started: {ex.Message}");
        }
    }

    /// <summary>
    /// Starts the tray as a child of the shell (explorer.exe) rather than of this host.
    /// </summary>
    /// <remarks>
    /// The tray outlives the host that starts it, so being its child is wrong in the one way that
    /// matters: <c>Process.Kill(entireProcessTree: true)</c> refuses to fell a tree containing the
    /// caller, which turned the tray's own "stop host" into an error and left the daemon's MSBuild
    /// workers orphaned when it fell back to killing the host alone. Re-parenting to the shell —
    /// the same thing the user's own launches descend from — puts the tray outside every host's
    /// tree, so the ordinary tree kill is correct again.
    ///
    /// It also stops the tray inheriting this process's standard handles, which are the spawning
    /// MCP client's redirected pipes: a tray holding those open outlives the client that owns them.
    /// </remarks>
    /// <returns><c>false</c> if the shell can't be found or opened, so the caller can fall back.</returns>
    [SupportedOSPlatform("windows")]
    private static bool TryStartUnderShell(string exe)
    {
        IntPtr shell = IntPtr.Zero;
        IntPtr attributeList = IntPtr.Zero;
        IntPtr parentHandle = IntPtr.Zero;

        try
        {
            int shellPid = ShellProcessId();
            if (shellPid == 0)
                return false;

            shell = OpenProcess(PROCESS_CREATE_PROCESS, false, shellPid);
            if (shell == IntPtr.Zero)
                return false;

            // Sized, then allocated, then filled: the attribute list is opaque and only the OS
            // knows how big one holding a single attribute needs to be.
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            if (size == IntPtr.Zero)
                return false;

            attributeList = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref size))
            {
                Marshal.FreeHGlobal(attributeList);
                attributeList = IntPtr.Zero;
                return false;
            }

            // UpdateProcThreadAttribute stores a pointer to the value, not the value, so the
            // handle must stay put until CreateProcess has read it.
            parentHandle = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(parentHandle, shell);

            if (!UpdateProcThreadAttribute(
                    attributeList, 0, PROC_THREAD_ATTRIBUTE_PARENT_PROCESS,
                    parentHandle, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                return false;
            }

            var startup = new STARTUPINFOEX();
            startup.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            startup.lpAttributeList = attributeList;

            var commandLine = new StringBuilder($"\"{exe}\"");
            bool created = CreateProcess(
                exe,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                bInheritHandles: false,
                EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW,
                IntPtr.Zero,
                AppContext.BaseDirectory,
                ref startup,
                out PROCESS_INFORMATION info);

            if (!created)
                return false;

            // Nothing here waits on the tray; releasing both handles lets it stand alone.
            CloseHandle(info.hProcess);
            CloseHandle(info.hThread);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
            if (parentHandle != IntPtr.Zero)
                Marshal.FreeHGlobal(parentHandle);
            if (shell != IntPtr.Zero)
                CloseHandle(shell);
        }
    }

    /// <summary>
    /// The shell process owning this desktop, via the window it created — rather than the first
    /// process named "explorer", which on a machine with several desktops or sessions need not be
    /// the one this host belongs to.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static int ShellProcessId()
    {
        IntPtr window = GetShellWindow();
        if (window == IntPtr.Zero)
            return 0;

        _ = GetWindowThreadProcessId(window, out int pid);
        return pid;
    }

    private const int PROCESS_CREATE_PROCESS = 0x0080;
    private const int EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const int CREATE_NO_WINDOW = 0x08000000;
    private static readonly IntPtr PROC_THREAD_ATTRIBUTE_PARENT_PROCESS = 0x00020000;

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue,
        IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        int dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);
}
