using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace RoslynMCP.Debugger;

/// Process/PE architecture, so the host debugs a target through a worker of matching bitness —
/// ICorDebug cannot attach across x86/x64.
public enum DebugArch
{
    X64,
    X86,
}

public static class ProcessArch
{
    /// The bitness this host process runs as.
    public static DebugArch Host => Environment.Is64BitProcess ? DebugArch.X64 : DebugArch.X86;

    /// Read a managed/native executable's target architecture from its PE header. AnyCPU
    /// assemblies (I386 machine + no 32-bit-required flag) run as the OS bitness, so they map to
    /// the host arch. Falls back to the host arch when the file can't be read.
    public static DebugArch OfExecutable(string exePath)
    {
        try
        {
            using var stream = File.OpenRead(exePath);
            using var pe = new PEReader(stream);
            var headers = pe.PEHeaders;
            var machine = headers.CoffHeader.Machine;
            return machine switch
            {
                Machine.Amd64 or Machine.Arm64 or Machine.IA64 => DebugArch.X64,
                Machine.I386 => IsAnyCpu(headers) ? Host : DebugArch.X86,
                _ => Host,
            };
        }
        catch
        {
            return Host;
        }
    }

    /// AnyCPU: an I386-machine managed image WITHOUT the 32BitRequired corflag. Such an image
    /// runs 64-bit on a 64-bit OS. A pure-x86 or 32-bit-preferred image sets the flag.
    private static bool IsAnyCpu(PEHeaders headers)
    {
        if (headers.CorHeader is not { } cor)
            return false; // native x86 image
        return (cor.Flags & CorFlags.Requires32Bit) == 0;
    }

    /// <summary>
    /// The bitness a running process actually runs at, which is what decides whether it can be
    /// debugged in-process or needs a bitness-matched worker.
    /// </summary>
    /// <remarks>
    /// Reads WOW64 status rather than the image on disk: an AnyCPU image can be running either
    /// way, and a 32-bit IIS Express app pool is exactly that case.
    /// </remarks>
    public static DebugArch OfProcess(int pid)
    {
        // A 32-bit OS has no WOW64 layer, so everything on it is x86.
        if (!Environment.Is64BitOperatingSystem)
            return DebugArch.X86;

        if (!OperatingSystem.IsWindows())
            return Host;

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            if (IsWow64Process(process.Handle, out var isWow64))
                return isWow64 ? DebugArch.X86 : DebugArch.X64;
        }
        catch
        {
            // Process gone, or not enough rights to open it; fall back to the executable image.
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            var image = process.MainModule?.FileName;
            if (!string.IsNullOrEmpty(image))
                return OfExecutable(image);
        }
        catch
        {
            // Nothing left to read; assume the host's bitness and let the attach report the truth.
        }

        return Host;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process(IntPtr hProcess, out bool wow64Process);
}
