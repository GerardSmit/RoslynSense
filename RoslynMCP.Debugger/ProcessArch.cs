using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace RoslynMCP.Debugger;

/// Process/PE architecture, so the host debugs a target through a worker of matching architecture —
/// ICorDebug cannot attach across architectures.
public enum DebugArch
{
    X64,
    X86,
    Arm64,
}

public static class ProcessArch
{
    private const ushort ImageFileMachineUnknown = 0;
    private const ushort ImageFileMachineI386 = 0x014C;
    private const ushort ImageFileMachineAmd64 = 0x8664;
    private const ushort ImageFileMachineArm64 = 0xAA64;

    /// The architecture this host process runs as.
    public static DebugArch Host => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X86 => DebugArch.X86,
        Architecture.Arm64 => DebugArch.Arm64,
        _ => DebugArch.X64,
    };

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
                Machine.Amd64 => DebugArch.X64,
                Machine.Arm64 => DebugArch.Arm64,
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

            // IsWow64Process2 reports which machine the process is emulating and which the OS runs
            // natively, so it can tell an arm64-native process from an x64 one. Plain
            // IsWow64Process only answers "is this emulated", which on an arm64 OS collapses
            // arm64 and x64 into the same answer.
            if (IsWow64Process2(process.Handle, out var processMachine, out var nativeMachine))
            {
                // A known emulated machine is a direct answer.
                switch (processMachine)
                {
                    case ImageFileMachineI386:
                        return DebugArch.X86;
                    case ImageFileMachineAmd64:
                        return DebugArch.X64;
                    case ImageFileMachineArm64:
                        return DebugArch.Arm64;
                }

                // Otherwise the process is native to the OS — except on arm64, where x64 processes
                // do not necessarily report as emulated. Trusting the native machine there would
                // call every x64 target arm64 and send it to a worker that cannot attach to it, so
                // that one case is settled from the image instead.
                if (processMachine == ImageFileMachineUnknown && nativeMachine != ImageFileMachineArm64)
                {
                    return nativeMachine switch
                    {
                        ImageFileMachineI386 => DebugArch.X86,
                        ImageFileMachineAmd64 => DebugArch.X64,
                        _ => Host,
                    };
                }
            }
            else if (IsWow64Process(process.Handle, out var isWow64))
            {
                // Windows too old for IsWow64Process2. It cannot distinguish arm64 from x64, but
                // such a Windows predates arm64 anyway.
                return isWow64 ? DebugArch.X86 : Host;
            }
        }
        catch
        {
            // Process gone, not enough rights to open it, or a Windows too old for
            // IsWow64Process2; fall back to the executable image.
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process2(IntPtr hProcess, out ushort processMachine, out ushort nativeMachine);
}
