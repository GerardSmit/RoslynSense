using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RoslynMCP.Services.Memory;

internal static class ChildProcessMemory
{
    internal sealed record Sample(int Pid, int ParentPid, string Name, long PrivateBytes, long WorkingSet);

    public static Sample[] Capture(int parentPid)
    {
        if (!OperatingSystem.IsWindows()) return [];
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) return [];
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            var parents = new Dictionary<int, int>();
            if (Process32First(snapshot, ref entry))
                do { parents[(int)entry.Pid] = (int)entry.ParentPid; } while (Process32Next(snapshot, ref entry));
            var descendants = new HashSet<int> { parentPid };
            bool added;
            do
            {
                added = false;
                foreach (var (pid, parent) in parents)
                    if (descendants.Contains(parent) && descendants.Add(pid)) added = true;
            } while (added);
            var result = new List<Sample>();
            foreach (int pid in descendants.Where(p => p != parentPid))
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    result.Add(new(pid, parents[pid], process.ProcessName, process.PrivateMemorySize64, process.WorkingSet64));
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            return result.ToArray();
        }
        finally { CloseHandle(snapshot); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Pid;
        public UIntPtr DefaultHeap;
        public uint ModuleId, Threads, ParentPid;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Exe;
    }
    [DllImport("kernel32.dll")] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
