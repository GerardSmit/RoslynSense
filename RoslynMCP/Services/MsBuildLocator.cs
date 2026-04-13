using System.Diagnostics;

namespace RoslynMCP.Services;

/// <summary>
/// Locates the MSBuild executable for building legacy .NET Framework projects.
/// Uses vswhere first, then falls back to known Visual Studio installation paths.
/// </summary>
internal static class MsBuildLocator
{
    private static string? _cachedPath;
    private static bool _searched;

    /// <summary>
    /// Returns the full path to MSBuild.exe, or null if not found.
    /// Result is cached after the first call.
    /// </summary>
    public static string? FindMsBuild()
    {
        if (_searched) return _cachedPath;
        _searched = true;
        _cachedPath = LocateMsBuild();
        return _cachedPath;
    }

    private static string? LocateMsBuild()
    {
        // Try vswhere first — the authoritative way to find VS tools
        var vswherePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            @"Microsoft Visual Studio\Installer\vswhere.exe");

        if (File.Exists(vswherePath))
        {
            var msbuild = RunVsWhere(vswherePath);
            if (msbuild is not null) return msbuild;
        }

        // Fallback: probe known VS installation paths
        return ProbeKnownPaths();
    }

    private static string? RunVsWhere(string vswherePath)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = vswherePath,
                    Arguments = "-latest -requires Microsoft.Component.MSBuild " +
                                "-find MSBuild\\**\\Bin\\MSBuild.exe",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();

            // vswhere may return multiple lines; pick the first valid one
            foreach (var line in output.Split('\n'))
            {
                var candidate = line.Trim();
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        catch
        {
            // Ignore — fall through to probe
        }

        return null;
    }

    private static string? ProbeKnownPaths()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        var editions = new[] { "Enterprise", "Professional", "Community", "BuildTools", "Preview" };
        var versions = new[] { "2022", "2019", "2017" };
        var relativePath = @"MSBuild\Current\Bin\MSBuild.exe";

        foreach (var version in versions)
        {
            foreach (var edition in editions)
            {
                foreach (var root in new[] { programFiles, programFilesX86 })
                {
                    var candidate = Path.Combine(root, "Microsoft Visual Studio", version, edition, relativePath);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
        }

        // MSBuild shipped standalone in VS 2017 Build Tools at a different path
        foreach (var root in new[] { programFiles, programFilesX86 })
        {
            var candidate = Path.Combine(root, "Microsoft Visual Studio", "2017", "BuildTools",
                @"MSBuild\15.0\Bin\MSBuild.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
