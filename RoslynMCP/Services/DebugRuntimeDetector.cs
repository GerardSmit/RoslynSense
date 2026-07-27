using System.Diagnostics;

namespace RoslynMCP.Services;

/// <summary>
/// Works out which runtime a debug target hosts, so the right engine is chosen without the caller
/// having to say.
/// </summary>
/// <remarks>
/// netcoredbg only speaks to CoreCLR and ICorDebug is the only way into .NET Framework, so picking
/// wrong means the attach simply fails. Detection is therefore deliberately conservative: it reads
/// the evidence it can and falls back to CoreCLR, which is the common case.
/// </remarks>
internal static class DebugRuntimeDetector
{
    /// <summary>Determines the runtime of a project from its classification.</summary>
    public static DebugRuntime ForProject(string projectPath) =>
        ProjectClassifier.Classify(projectPath).DebugRuntime;

    /// <summary>
    /// Determines the runtime of a running process from the CLR it loaded: desktop .NET Framework
    /// maps <c>clr.dll</c> (or <c>mscorwks.dll</c> on very old versions), while .NET Core and
    /// later map <c>coreclr.dll</c>.
    /// </summary>
    public static DebugRuntime ForProcess(int pid)
    {
        // .NET Framework is Windows-only, so nothing off Windows can be one. Without this the
        // probes below would misreport: a Linux process loads no coreclr.dll (the module is
        // libcoreclr.so) and its host is named `dotnet` rather than `dotnet.exe`.
        if (!OperatingSystem.IsWindows())
            return DebugRuntime.CoreClr;

        try
        {
            using var process = Process.GetProcessById(pid);

            foreach (ProcessModule module in process.Modules)
            {
                var name = module.ModuleName;
                if (name is null)
                    continue;

                if (name.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase))
                    return DebugRuntime.CoreClr;

                if (name.Equals("clr.dll", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("mscorwks.dll", StringComparison.OrdinalIgnoreCase))
                    return DebugRuntime.NetFramework;
            }
        }
        catch (Exception)
        {
            // Module enumeration fails across a bitness boundary and for protected processes.
            // Fall through to the executable-image check, which needs no handle to the process.
        }

        return ForProcessImage(pid);
    }

    /// <summary>
    /// Falls back to the executable on disk: a .NET Core app ships a
    /// <c>&lt;name&gt;.runtimeconfig.json</c> beside it, a .NET Framework app does not.
    /// </summary>
    private static DebugRuntime ForProcessImage(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var imagePath = process.MainModule?.FileName;
            if (string.IsNullOrEmpty(imagePath))
                return DebugRuntime.CoreClr;

            // A host-launched app runs as dotnet.exe, which is always CoreCLR.
            if (Path.GetFileName(imagePath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
                return DebugRuntime.CoreClr;

            var runtimeConfig = Path.ChangeExtension(imagePath, ".runtimeconfig.json");
            return File.Exists(runtimeConfig) ? DebugRuntime.CoreClr : DebugRuntime.NetFramework;
        }
        catch (Exception)
        {
            return DebugRuntime.CoreClr;
        }
    }

    /// <summary>
    /// Whether IIS Express or an IIS worker — always .NET Framework when it hosts a classic
    /// ASP.NET site, and the process name alone is enough to tell.
    /// </summary>
    public static bool IsClassicAspNetHost(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var name = process.ProcessName;
            return name.Equals("iisexpress", StringComparison.OrdinalIgnoreCase)
                || name.Equals("w3wp", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
