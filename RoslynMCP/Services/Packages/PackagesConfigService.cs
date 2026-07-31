using System.IO.Compression;
using System.Xml.Linq;
using NuGet.Common;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace RoslynMCP.Services.Packages;

/// <summary>One entry of a project's packages.config.</summary>
public sealed record PackagesConfigEntry(string Id, string Version, string? TargetFramework);

/// <summary>
/// Package management for projects that predate PackageReference.
/// </summary>
/// <remarks>
/// `dotnet add package` refuses these projects outright, and NuGet's own packages.config support
/// lives in NuGet.PackageManagement, which drags in the whole MSBuild project system. What the
/// format actually requires is small and stable, so it is done here: resolve and unpack into the
/// solution's `packages` folder, write a <c>Reference</c> with a <c>HintPath</c>, and record the
/// install in packages.config — which is exactly what Visual Studio leaves behind.
/// </remarks>
public static class PackagesConfigService
{
    public static string? PathFor(string projectPath)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(projectPath));
        if (directory is null)
            return null;

        string candidate = Path.Combine(directory, "packages.config");
        return File.Exists(candidate) ? candidate : null;
    }

    public static bool Uses(string projectPath) => PathFor(projectPath) is not null;

    public static IReadOnlyList<PackagesConfigEntry> Read(string projectPath)
    {
        string? path = PathFor(projectPath);
        if (path is null)
            return [];

        try
        {
            return XDocument.Load(path)
                .Descendants("package")
                .Select(e => new PackagesConfigEntry(
                    e.Attribute("id")?.Value ?? "",
                    e.Attribute("version")?.Value ?? "",
                    e.Attribute("targetFramework")?.Value))
                .Where(p => p.Id.Length > 0)
                .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            ServiceLog.Warn(
                $"Could not read packages.config for '{Path.GetFileName(projectPath)}': {ex.Message}",
                key: $"packages-config:{projectPath}");
            return [];
        }
    }

    public static async Task<PackageOperationResult> InstallAsync(
        string projectPath, string id, string? version, CancellationToken ct)
    {
        string? configPath = PathFor(projectPath);
        if (configPath is null)
            return new PackageOperationResult(false, "This project does not use packages.config.");

        var resolved = await ResolveVersionAsync(id, version, ct);
        if (resolved is null)
            return new PackageOperationResult(false, $"No version of {id} was found on the configured feeds.");

        string packagesRoot = PackagesRootFor(projectPath);
        string folderName = $"{id}.{resolved}";
        string packageDirectory = Path.Combine(packagesRoot, folderName);

        if (!Directory.Exists(packageDirectory))
        {
            var download = await DownloadAsync(id, resolved, packageDirectory, ct);
            if (download is not null)
                return new PackageOperationResult(false, download);
        }

        string targetFramework = ProjectTargetFramework(projectPath);
        var assemblies = LibAssembliesFor(packageDirectory, targetFramework);
        if (assemblies.Count == 0)
        {
            return new PackageOperationResult(false,
                $"{id} {resolved} has no assembly compatible with {targetFramework}.");
        }

        WriteReferences(projectPath, packagesRoot, folderName, assemblies);
        WriteConfigEntry(configPath, id, resolved.ToString(), targetFramework);

        ProjectModel.ProjectEvaluationService.Evict(projectPath);
        await WorkspaceService.EvictAllAsync(ct);

        return new PackageOperationResult(true,
            $"Installed {id} {resolved} into {Path.GetFileNameWithoutExtension(projectPath)}.");
    }

    public static async Task<PackageOperationResult> UninstallAsync(
        string projectPath, string id, CancellationToken ct)
    {
        string? configPath = PathFor(projectPath);
        if (configPath is null)
            return new PackageOperationResult(false, "This project does not use packages.config.");

        var entry = Read(projectPath)
            .FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return new PackageOperationResult(false, $"{id} is not installed in this project.");

        RemoveReferences(projectPath, $"{entry.Id}.{entry.Version}");
        RemoveConfigEntry(configPath, id);

        ProjectModel.ProjectEvaluationService.Evict(projectPath);
        await WorkspaceService.EvictAllAsync(ct);

        // The extracted package stays in the packages folder: other projects in the solution may
        // still reference it, and NuGet's own uninstall leaves it too.
        return new PackageOperationResult(true,
            $"Removed {id} from {Path.GetFileNameWithoutExtension(projectPath)}.");
    }

    /// <summary>The solution-level <c>packages</c> folder every packages.config project shares.</summary>
    public static string PackagesRootFor(string projectPath)
    {
        string? solution = PathHelper.FindNearestSolution(projectPath);
        string root = solution is not null
            ? Path.GetDirectoryName(solution)!
            : Path.GetDirectoryName(Path.GetFullPath(projectPath))!;

        return Path.Combine(root, "packages");
    }

    private static async Task<NuGetVersion?> ResolveVersionAsync(
        string id, string? version, CancellationToken ct)
    {
        if (version is { Length: > 0 } && NuGetVersion.TryParse(version, out var parsed))
            return parsed;

        var versions = await NuGetService.VersionsAsync(id, includePrerelease: false, ct);
        return versions.Count > 0 && NuGetVersion.TryParse(versions[0], out var latest) ? latest : null;
    }

    /// <returns><c>null</c> on success, or the reason it failed.</returns>
    private static async Task<string?> DownloadAsync(
        string id, NuGetVersion version, string packageDirectory, CancellationToken ct)
    {
        try
        {
            var resource = await NuGetService.FindPackageResourceAsync(ct);
            if (resource is null)
                return "No NuGet feed is configured.";

            using var stream = new MemoryStream();
            bool found = await resource.CopyNupkgToStreamAsync(
                id, version, stream, NuGetService.Cache, NullLogger.Instance, ct);

            if (!found)
                return $"{id} {version} was not found on the configured feeds.";

            stream.Position = 0;
            Directory.CreateDirectory(packageDirectory);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            foreach (var zipEntry in archive.Entries)
            {
                if (zipEntry.FullName.EndsWith('/'))
                    continue;

                string destination = Path.GetFullPath(
                    Path.Combine(packageDirectory, zipEntry.FullName.Replace('/', Path.DirectorySeparatorChar)));

                // A crafted .nupkg can name an entry that climbs out of the extraction directory.
                if (!destination.StartsWith(packageDirectory, StringComparison.OrdinalIgnoreCase))
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                zipEntry.ExtractToFile(destination, overwrite: true);
            }

            return null;
        }
        catch (Exception ex)
        {
            try { Directory.Delete(packageDirectory, recursive: true); } catch { }
            return $"Could not download {id} {version}: {ex.Message}";
        }
    }

    /// <summary>
    /// The assemblies to reference, from the best <c>lib</c> folder for the project's framework.
    /// </summary>
    /// <remarks>
    /// Ranked by string rather than through NuGet.Frameworks on purpose: that assembly ships with
    /// runtime assets excluded and resolves only through MSBuildLocator, so touching it here would
    /// mean carrying MSBuild registration into package management for a comparison this simple.
    /// </remarks>
    private static IReadOnlyList<string> LibAssembliesFor(string packageDirectory, string targetFramework)
    {
        string lib = Path.Combine(packageDirectory, "lib");
        if (!Directory.Exists(lib))
            return [];

        // A package can put assemblies straight under lib/, meaning "any framework".
        var flat = Directory.EnumerateFiles(lib, "*.dll", SearchOption.TopDirectoryOnly).ToList();

        var best = Directory.EnumerateDirectories(lib)
            .Select(directory => (Directory: directory, Score: FrameworkScore(Path.GetFileName(directory), targetFramework)))
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .Select(candidate => candidate.Directory)
            .FirstOrDefault();

        return best is null
            ? flat
            : Directory.EnumerateFiles(best, "*.dll", SearchOption.TopDirectoryOnly).ToList();
    }

    /// <summary>Higher is a better match; 0 means incompatible.</summary>
    internal static int FrameworkScore(string folder, string targetFramework)
    {
        string candidate = folder.ToLowerInvariant();
        string target = targetFramework.ToLowerInvariant();

        // An exact moniker wins outright.
        if (candidate == target)
            return 1000;

        if (candidate.StartsWith("net", StringComparison.Ordinal) &&
            !candidate.StartsWith("netstandard", StringComparison.Ordinal) &&
            !candidate.StartsWith("netcore", StringComparison.Ordinal))
        {
            // A Framework project can use any net4x assembly up to its own version.
            int candidateVersion = VersionDigits(candidate[3..]);
            int targetVersion = VersionDigits(target.StartsWith("net", StringComparison.Ordinal) ? target[3..] : "");
            return candidateVersion > 0 && targetVersion >= candidateVersion ? 500 + candidateVersion : 0;
        }

        if (candidate.StartsWith("netstandard", StringComparison.Ordinal))
        {
            // .NET Framework 4.6.1+ can consume netstandard2.0; 2.1 it never can.
            int standard = VersionDigits(candidate["netstandard".Length..]);
            return standard <= 200 ? 100 + standard / 10 : 0;
        }

        return 0;
    }

    /// <summary>
    /// A framework moniker's version as a comparable number: net48 and net4.8 both become 480,
    /// net472 becomes 472.
    /// </summary>
    /// <remarks>
    /// Padding on the right is the whole point — comparing the raw digits would read net48 as 48
    /// and rank it below net472, which is backwards.
    /// </remarks>
    private static int VersionDigits(string text)
    {
        string digits = new([.. text.Where(char.IsAsciiDigit)]);
        return digits.Length == 0 ? 0 : int.Parse(digits.PadRight(3, '0')[..3]);
    }

    private static string ProjectTargetFramework(string projectPath)
    {
        try
        {
            string? version = XDocument.Load(projectPath)
                .Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "TargetFrameworkVersion")
                ?.Value;

            // "v4.7.2" is how legacy projects spell it; packages.config wants "net472".
            return version is { Length: > 1 }
                ? "net" + new string([.. version.Where(char.IsAsciiDigit)])
                : "net48";
        }
        catch
        {
            return "net48";
        }
    }

    private static void WriteReferences(
        string projectPath, string packagesRoot, string folderName, IReadOnlyList<string> assemblies)
    {
        var document = XDocument.Load(projectPath);
        if (document.Root is null)
            return;

        var ns = document.Root.Name.Namespace;
        string projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;

        var group = document.Root.Elements(ns + "ItemGroup")
            .FirstOrDefault(g => g.Elements(ns + "Reference").Any())
            ?? AddGroup(document.Root, ns);

        foreach (string assembly in assemblies)
        {
            string name = Path.GetFileNameWithoutExtension(assembly);
            string hintPath = Path.GetRelativePath(projectDirectory, assembly);

            // Replacing rather than appending is what makes an upgrade work: the old version's
            // HintPath must not survive alongside the new one.
            document.Root.Descendants(ns + "Reference")
                .Where(r => ReferenceName(r).Equals(name, StringComparison.OrdinalIgnoreCase))
                .ToList()
                .ForEach(r => r.Remove());

            group.Add(new XElement(ns + "Reference",
                new XAttribute("Include", name),
                new XElement(ns + "HintPath", hintPath),
                new XElement(ns + "Private", "True")));
        }

        document.Save(projectPath);
    }

    private static void RemoveReferences(string projectPath, string folderName)
    {
        try
        {
            var document = XDocument.Load(projectPath);
            if (document.Root is null)
                return;

            var ns = document.Root.Name.Namespace;
            var stale = document.Root.Descendants(ns + "Reference")
                .Where(r => r.Element(ns + "HintPath")?.Value is { } hint &&
                            hint.Contains(folderName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (stale.Count == 0)
                return;

            foreach (var reference in stale)
                reference.Remove();
            document.Save(projectPath);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn(
                $"Could not remove references to '{folderName}': {ex.Message}",
                key: $"packages-config-remove:{projectPath}");
        }
    }

    private static XElement AddGroup(XElement root, XNamespace ns)
    {
        var group = new XElement(ns + "ItemGroup");
        root.Add(group);
        return group;
    }

    /// <summary>The simple name of a reference, which is the assembly name before any comma.</summary>
    private static string ReferenceName(XElement reference)
    {
        string include = reference.Attribute("Include")?.Value ?? "";
        int comma = include.IndexOf(',');
        return (comma < 0 ? include : include[..comma]).Trim();
    }

    private static void WriteConfigEntry(
        string configPath, string id, string version, string targetFramework)
    {
        var document = File.Exists(configPath)
            ? XDocument.Load(configPath)
            : new XDocument(new XElement("packages"));

        var root = document.Root ?? new XElement("packages");

        root.Elements("package")
            .Where(e => string.Equals(e.Attribute("id")?.Value, id, StringComparison.OrdinalIgnoreCase))
            .ToList()
            .ForEach(e => e.Remove());

        root.Add(new XElement("package",
            new XAttribute("id", id),
            new XAttribute("version", version),
            new XAttribute("targetFramework", targetFramework)));

        document.Save(configPath);
    }

    private static void RemoveConfigEntry(string configPath, string id)
    {
        try
        {
            var document = XDocument.Load(configPath);
            document.Root?.Elements("package")
                .Where(e => string.Equals(e.Attribute("id")?.Value, id, StringComparison.OrdinalIgnoreCase))
                .ToList()
                .ForEach(e => e.Remove());
            document.Save(configPath);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn(
                $"Could not update packages.config: {ex.Message}", key: $"packages-config-write:{configPath}");
        }
    }
}
