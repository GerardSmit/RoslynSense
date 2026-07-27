using System.Text.RegularExpressions;

namespace RoslynMCP.Services;

/// <summary>
/// What the machine can actually do with legacy .NET Framework / WebForms projects.
/// </summary>
/// <remarks>
/// Empty strings mean "not found" rather than null so the report renders uniformly.
/// </remarks>
internal sealed record NetFxToolchainInfo
{
    /// <summary>Whether the desktop CLR (clr.dll) is installed.</summary>
    public bool DesktopClr { get; init; }

    /// <summary>The <c>NDP\v4\Full</c> release key, which identifies the installed 4.x servicing level.</summary>
    public string FrameworkRelease { get; init; } = "";

    /// <summary>Visual Studio's MSBuild.exe — legacy projects cannot be built by the dotnet CLI.</summary>
    public string MsBuildPath { get; init; } = "";

    /// <summary>Whether Microsoft.WebApplication.targets is present; legacy web projects import it.</summary>
    public bool WebApplicationTargets { get; init; }

    public string IisExpressX64 { get; init; } = "";
    public string IisExpressX86 { get; init; } = "";

    /// <summary>aspnet_compiler.exe, for precompiling a WebForms site.</summary>
    public string AspnetCompiler { get; init; } = "";

    /// <summary>SqlMetal.exe, which regenerates a .dbml's designer code.</summary>
    public string SqlMetal { get; init; } = "";

    /// <summary>Whether .NET Framework reference assemblies (the developer pack) are installed.</summary>
    public bool ReferenceAssemblies { get; init; }

    /// <summary>Whether this process runs elevated, which IIS/w3wp attach requires.</summary>
    public bool Elevated { get; init; }

    /// <summary>The IIS Express to launch, preferring x64 so the debugger avoids a bitness worker.</summary>
    public string? PreferredIisExpress =>
        IisExpressX64.Length > 0 ? IisExpressX64 :
        IisExpressX86.Length > 0 ? IisExpressX86 : null;
}

/// <summary>
/// Discovers the .NET Framework / WebForms toolchain on this machine: desktop CLR, Visual Studio
/// MSBuild, Microsoft.WebApplication.targets, IIS Express, aspnet_compiler and SqlMetal.
/// </summary>
/// <remarks>
/// Probed once per process — these are external installs that do not appear mid-session. Reported
/// through <c>OpenSolution</c> so a missing prerequisite produces an actionable message instead of
/// an opaque run or regenerate failure later.
/// </remarks>
internal static class NetFxToolchain
{
    private static readonly Lazy<NetFxToolchainInfo> s_cached = new(Probe);

    public static NetFxToolchainInfo Info => s_cached.Value;

    internal static NetFxToolchainInfo Probe()
    {
        // Everything here is Windows-only; off Windows the whole toolchain is simply absent.
        if (!OperatingSystem.IsWindows())
            return new NetFxToolchainInfo();

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var clrDir = Path.Combine(windows, "Microsoft.NET", "Framework64", "v4.0.30319");
        if (!Directory.Exists(clrDir))
            clrDir = Path.Combine(windows, "Microsoft.NET", "Framework", "v4.0.30319");

        var aspnetCompiler = Path.Combine(clrDir, "aspnet_compiler.exe");
        var msbuild = MsBuildLocator.FindMsBuild();

        return new NetFxToolchainInfo
        {
            DesktopClr = File.Exists(Path.Combine(clrDir, "clr.dll")),
            FrameworkRelease = ReadFrameworkRelease(),
            AspnetCompiler = File.Exists(aspnetCompiler) ? aspnetCompiler : "",
            IisExpressX64 = ProbeFile(Environment.SpecialFolder.ProgramFiles, "IIS Express", "iisexpress.exe"),
            IisExpressX86 = ProbeFile(Environment.SpecialFolder.ProgramFilesX86, "IIS Express", "iisexpress.exe"),
            SqlMetal = FindSqlMetal() ?? "",
            ReferenceAssemblies = HasReferenceAssemblies(),
            MsBuildPath = msbuild ?? "",
            WebApplicationTargets = msbuild is not null && HasWebApplicationTargets(msbuild),
            Elevated = IsElevated(),
        };
    }

    private static string ProbeFile(Environment.SpecialFolder root, params string[] parts)
    {
        var dir = Environment.GetFolderPath(root);
        if (string.IsNullOrEmpty(dir)) return "";
        var path = Path.Combine([dir, .. parts]);
        return File.Exists(path) ? path : "";
    }

    private static string ReadFrameworkRelease()
    {
        if (!OperatingSystem.IsWindows()) return "";
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full");
            return key?.GetValue("Release")?.ToString() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static bool HasReferenceAssemblies()
    {
        var refAsm = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Reference Assemblies", "Microsoft", "Framework", ".NETFramework");
        try
        {
            return Directory.Exists(refAsm) && Directory.EnumerateDirectories(refAsm, "v4.*").Any();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Microsoft.WebApplication.targets lives under
    /// <c>&lt;VS&gt;\MSBuild\Microsoft\VisualStudio\v*\WebApplications</c>. Legacy web projects
    /// import it via <c>$(VSToolsPath)</c>; without it VS MSBuild cannot build them.
    /// </summary>
    internal static bool HasWebApplicationTargets(string msbuildExePath)
    {
        var vsInstallDir = MsBuildLocator.GetVsInstallDir(msbuildExePath);
        if (vsInstallDir is null) return false;

        var vsRoot = Path.Combine(vsInstallDir, "MSBuild", "Microsoft", "VisualStudio");
        try
        {
            return Directory.Exists(vsRoot) && Directory.EnumerateDirectories(vsRoot, "v*")
                .Any(v => File.Exists(Path.Combine(v, "WebApplications", "Microsoft.WebApplication.targets")));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Finds the newest SqlMetal.exe under the Windows SDK's <c>NETFX &lt;ver&gt; Tools</c> folders,
    /// e.g. <c>…\Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.8 Tools\SqlMetal.exe</c>.
    /// </summary>
    internal static string? FindSqlMetal()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };

        string? best = null;
        var bestVersion = new Version(0, 0);

        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            var sdkRoot = Path.Combine(root, "Microsoft SDKs", "Windows");
            if (!Directory.Exists(sdkRoot)) continue;

            IEnumerable<string> candidates;
            try
            {
                // Two levels of versioning (SDK "v10.0A" then "NETFX 4.8 Tools"); the NETFX
                // version is what actually determines the tool, so rank on that.
                candidates = Directory.EnumerateDirectories(sdkRoot)
                    .SelectMany(sdk => SafeEnumerate(Path.Combine(sdk, "bin"), "NETFX * Tools"))
                    .Select(dir => Path.Combine(dir, "SqlMetal.exe"))
                    .Where(File.Exists);
            }
            catch
            {
                continue;
            }

            foreach (var candidate in candidates)
            {
                var version = ParseNetfxToolsVersion(Path.GetFileName(Path.GetDirectoryName(candidate)) ?? "");

                // Take the first match, then only replace it for a strictly newer NETFX version —
                // so an unparseable folder name still yields a usable tool.
                if (best is null || version > bestVersion)
                {
                    bestVersion = version;
                    best = candidate;
                }
            }
        }

        return best;
    }

    private static IEnumerable<string> SafeEnumerate(string path, string pattern)
    {
        try
        {
            return Directory.Exists(path) ? Directory.EnumerateDirectories(path, pattern) : [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Extracts "4.8" from a folder named "NETFX 4.8 Tools".</summary>
    private static Version ParseNetfxToolsVersion(string folderName)
    {
        var match = Regex.Match(folderName, @"NETFX\s+(\d+(?:\.\d+)*)", RegexOptions.IgnoreCase);
        return match.Success && Version.TryParse(Normalize(match.Groups[1].Value), out var version)
            ? version
            : new Version(0, 0);

        // Version.TryParse needs at least major.minor.
        static string Normalize(string raw) => raw.Contains('.') ? raw : raw + ".0";
    }

    private static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
