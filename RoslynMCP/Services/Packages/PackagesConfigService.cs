using System.IO.Compression;
using Microsoft.Language.Xml;
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
            return Parser.ParseText(File.ReadAllText(path))
                .DescendantsByLocalName("package")
                .Select(e => new PackagesConfigEntry(
                    e.GetAttributeValue("id") ?? "",
                    e.GetAttributeValue("version") ?? "",
                    e.GetAttributeValue("targetFramework")))
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
        string projectPath, string id, string? version, CancellationToken ct,
        PackageMutationScope? scope = null)
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

        await TouchAsync(projectPath, scope, ct);

        return new PackageOperationResult(true,
            $"Installed {id} {resolved} into {Path.GetFileNameWithoutExtension(projectPath)}.");
    }

    public static async Task<PackageOperationResult> UninstallAsync(
        string projectPath, string id, CancellationToken ct,
        PackageMutationScope? scope = null)
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

        await TouchAsync(projectPath, scope, ct);

        // The extracted package stays in the packages folder: other projects in the solution may
        // still reference it, and NuGet's own uninstall leaves it too.
        return new PackageOperationResult(true,
            $"Removed {id} from {Path.GetFileNameWithoutExtension(projectPath)}.");
    }

    /// <summary>
    /// Records the change against the caller's batch when there is one. A bulk operation must not
    /// reload the workspace once per package.
    /// </summary>
    private static async Task TouchAsync(string projectPath, PackageMutationScope? scope, CancellationToken ct)
    {
        if (scope is not null)
        {
            scope.Touch(projectPath);
            return;
        }

        ProjectModel.ProjectEvaluationService.Evict(projectPath);
        await WorkspaceService.EvictAllAsync(ct);
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

        var versions = await NuGetService.AllVersionsAsync(id, includePrerelease: false, refresh: false, ct);
        return versions.Results.FirstOrDefault();
    }

    /// <returns><c>null</c> on success, or the reason it failed.</returns>
    private static async Task<string?> DownloadAsync(
        string id, NuGetVersion version, string packageDirectory, CancellationToken ct)
    {
        try
        {
            var resource = await NuGetService.FindPackageResourceAsync(id, ct);
            if (resource is null)
                return "No NuGet feed is configured.";

            using var cache = NuGetFeedContext.RentCache();
            using var stream = new MemoryStream();
            bool found = await resource.CopyNupkgToStreamAsync(
                id, version, stream, cache, NullLogger.Instance, ct);

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
                // The separator matters: without it "packages\Foo.1.0" also prefixes
                // "packages\Foo.1.0.evil", so a sibling directory would pass the check.
                if (!destination.StartsWith(
                        packageDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

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
            string? version = Parser.ParseText(File.ReadAllText(projectPath))
                .DescendantsByLocalName("TargetFrameworkVersion")
                .FirstOrDefault()
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

    /// <remarks>
    /// Element names are written without a prefix, which is what a legacy project wants: it binds
    /// the MSBuild namespace as the default one on <c>&lt;Project&gt;</c>, so an unprefixed child
    /// is already in it. The old writer had to carry the namespace around because it matched on
    /// resolved names; this one matches on the name as written.
    /// </remarks>
    private static void WriteReferences(
        string projectPath, string packagesRoot, string folderName, IReadOnlyList<string> assemblies)
    {
        var document = Parser.ParseText(File.ReadAllText(projectPath));
        if (document.RootSyntax is not { } original)
            return;

        var root = original;
        string projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;

        foreach (string assembly in assemblies)
        {
            string name = Path.GetFileNameWithoutExtension(assembly);
            string hintPath = Path.GetRelativePath(projectDirectory, assembly);

            // Replacing rather than appending is what makes an upgrade work: the old version's
            // HintPath must not survive alongside the new one. Re-found each time round the loop,
            // because every removal returns a new tree and leaves the remaining matches pointing
            // into the one before it.
            while (ReferenceNamed(root, name) is { } stale)
                root = root.RemoveNode(stale, SyntaxRemoveOptions.KeepNoTrivia)!;

            root = root.GetOrAddElement(
                "ItemGroup",
                group => group.GetElementByLocalName("Reference") is not null,
                out var itemGroup);

            root = root.ReplaceNode(
                itemGroup,
                itemGroup.AddChild(Reference(name, hintPath).NormalizeTrivia(itemGroup)));
        }

        File.WriteAllText(projectPath, document.ReplaceNode(original, root).ToFullString());
    }

    /// <summary>One <c>Reference</c> item, built flat and away from the document.</summary>
    private static XmlElementBaseSyntax Reference(string name, string hintPath)
    {
        var reference = (XmlElementBaseSyntax)Parser.ParseText("<Reference></Reference>").RootSyntax!;

        reference = reference.SetAttribute("Include", name);
        reference = reference.AddElement("HintPath", out _, (_, element) => element.WithText(hintPath));

        return reference.AddElement("Private", out _, (_, element) => element.WithText("True"));
    }

    private static void RemoveReferences(string projectPath, string folderName)
    {
        try
        {
            var document = Parser.ParseText(File.ReadAllText(projectPath));
            if (document.RootSyntax is not { } original)
                return;

            var root = original;
            bool removed = false;

            while (ReferenceInto(root, folderName) is { } stale)
            {
                root = root.RemoveNode(stale, SyntaxRemoveOptions.KeepNoTrivia)!;
                removed = true;
            }

            if (!removed)
                return;

            File.WriteAllText(projectPath, document.ReplaceNode(original, root).ToFullString());
        }
        catch (Exception ex)
        {
            ServiceLog.Warn(
                $"Could not remove references to '{folderName}': {ex.Message}",
                key: $"packages-config-remove:{projectPath}");
        }
    }

    /// <summary>The first <c>Reference</c> to an assembly of a given simple name.</summary>
    private static XmlElementBaseSyntax? ReferenceNamed(XmlElementBaseSyntax root, string name) =>
        root.DescendantsByLocalName("Reference")
            .FirstOrDefault(reference =>
                ReferenceName(reference).Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The first <c>Reference</c> whose hint path points into a package folder.</summary>
    private static XmlElementBaseSyntax? ReferenceInto(XmlElementBaseSyntax root, string folderName) =>
        root.DescendantsByLocalName("Reference")
            .FirstOrDefault(reference =>
                reference.GetElementByLocalName("HintPath")?.Value is { } hint &&
                hint.Contains(folderName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The simple name of a reference, which is the assembly name before any comma.</summary>
    private static string ReferenceName(XmlElementBaseSyntax reference)
    {
        string include = reference.GetAttributeValue("Include") ?? "";
        int comma = include.IndexOf(',');
        return (comma < 0 ? include : include[..comma]).Trim();
    }

    private static void WriteConfigEntry(
        string configPath, string id, string version, string targetFramework)
    {
        var document = Parser.ParseText(
            File.Exists(configPath) ? File.ReadAllText(configPath) : NewConfig);

        if (document.RootSyntax is not { } original)
            return;

        var root = Without(original, id).AddElement("package", out _, (_, package) => package
            .SetAttribute("id", id)
            .SetAttribute("version", version)
            .SetAttribute("targetFramework", targetFramework));

        File.WriteAllText(configPath, document.ReplaceNode(original, root).ToFullString());
    }

    private static void RemoveConfigEntry(string configPath, string id)
    {
        try
        {
            var document = Parser.ParseText(File.ReadAllText(configPath));

            if (document.RootSyntax is not { } original)
                return;

            File.WriteAllText(
                configPath, document.ReplaceNode(original, Without(original, id)).ToFullString());
        }
        catch (Exception ex)
        {
            ServiceLog.Warn(
                $"Could not update packages.config: {ex.Message}", key: $"packages-config-write:{configPath}");
        }
    }

    /// <summary>
    /// An empty packages.config, for a project that has none yet. The line break is what the first
    /// entry indents against: a document written on one line gets its children on one line too.
    /// </summary>
    private const string NewConfig = "<packages>\r\n</packages>";

    /// <summary>
    /// The <c>packages</c> element without any entry for a package, whatever its case.
    /// </summary>
    /// <remarks>
    /// One at a time and re-found each time: a removal returns a new tree, so the second match of
    /// a batch would otherwise be removed from a document that no longer exists.
    /// </remarks>
    private static XmlElementBaseSyntax Without(XmlElementBaseSyntax root, string id)
    {
        while (root.GetElementsByLocalName("package")
                   .FirstOrDefault(package => string.Equals(
                       package.GetAttributeValue("id"), id, StringComparison.OrdinalIgnoreCase)) is { } entry)
        {
            root = root.RemoveNode(entry, SyntaxRemoveOptions.KeepNoTrivia)!;
        }

        return root;
    }
}
