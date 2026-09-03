using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace RoslynMCP.Languages.Proto.Core;

/// <summary>
/// Turns the path in an <c>import</c> statement into a file on disk.
/// </summary>
/// <remarks>
/// <para>
/// An import path is not relative to the file that writes it. It is relative to a <i>proto root</i>,
/// and Grpc.Tools decides that root in its <c>_Protobuf_SetProtoRoot</c> target: a <c>.proto</c>
/// under the project directory gets the project directory, and one outside gets its own directory.
/// That is why <c>widgets/widgets.proto</c> can write <c>import "common/types.proto"</c> and mean
/// the sibling folder rather than <c>widgets/common</c> — resolving imports against the importing
/// file's directory, which is what every other language does, gets this exactly backwards.
/// </para>
/// <para>
/// The roots below therefore lead with the project directory and only then fall back to the
/// importing file's own directory and the directories between, which is what a project that sets
/// <c>ProtoRoot</c> to a subfolder needs. The per-item <c>ProtoRoot</c> and <c>AdditionalImportDirs</c>
/// metadata that would settle it exactly is not visible from here: MSBuild evaluates it, and
/// Roslyn's workspace does not carry <c>Protobuf</c> items.
/// </para>
/// </remarks>
internal static class ProtoImportResolver
{
    private readonly record struct RootKey(string Directory, string ProjectDirectory);

    private static readonly ConcurrentDictionary<RootKey, ImmutableArray<string>> s_roots = new();

    private static readonly Lazy<string?> s_standardImports =
        new(FindStandardImports, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The <c>google/protobuf</c> protos that ship inside the Grpc.Tools package, or <c>null</c>
    /// when the package is not restored anywhere this process can see.
    /// </summary>
    /// <remarks>
    /// protoc is handed this directory on its import path, so a file importing
    /// <c>google/protobuf/timestamp.proto</c> compiles without the file existing anywhere in the
    /// solution. Finding it is what lets the editor open that import and resolve the types in it;
    /// <see cref="ProtoWellKnownTypes"/> is the answer for when it cannot be found.
    /// </remarks>
    public static string? StandardImportsDirectory => s_standardImports.Value;

    /// <summary>
    /// The absolute path an <c>import</c> names, or <c>null</c> when no candidate root has it.
    /// </summary>
    /// <param name="importPath">The path as written, forward slashes and all.</param>
    /// <param name="importingFilePath">The <c>.proto</c> the statement is in, which decides the
    /// roots to try.</param>
    /// <param name="projectDirectory">The owning project's directory when it is known.</param>
    public static string? Resolve(string importPath, string importingFilePath, string? projectDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(importPath))
            return null;

        string relative = ToSystemPath(importPath);

        foreach (string root in CandidateRoots(importingFilePath, projectDirectory))
        {
            if (Combine(root, relative) is { } resolved)
                return resolved;
        }

        return null;
    }

    /// <summary>
    /// Resolves a proto path against a project directly, for a caller that has a path but no file
    /// to resolve it relative to — the <c>source:</c> header protoc writes into every generated
    /// <c>.cs</c> is one, and it is how a generated file is traced back to the <c>.proto</c> it
    /// came from.
    /// </summary>
    public static string? ResolveInProject(string protoPath, string projectDirectory)
    {
        if (string.IsNullOrWhiteSpace(protoPath) || string.IsNullOrEmpty(projectDirectory))
            return null;

        string relative = ToSystemPath(protoPath);

        return Combine(projectDirectory, relative)
            ?? (StandardImportsDirectory is { } standard ? Combine(standard, relative) : null);
    }

    /// <summary>
    /// The path protoc knows a file by — relative to its proto root, forward slashed — or
    /// <c>null</c> when the file is under no root this can find.
    /// </summary>
    /// <remarks>
    /// The inverse of <see cref="Resolve"/>, and the form every generated artefact refers to a
    /// <c>.proto</c> by: the <c>source:</c> comment in the generated C# and the path in another
    /// file's <c>import</c> are both this, never an absolute path.
    /// </remarks>
    public static string? ToProtoPath(string protoFilePath, string? projectDirectory = null)
    {
        foreach (string root in CandidateRoots(protoFilePath, projectDirectory))
        {
            if (TryMakeRelative(root, protoFilePath) is { } relative)
                return relative;
        }

        return null;
    }

    /// <summary>
    /// Every directory an import from <paramref name="protoFilePath"/> is tried against, in the
    /// order protoc would have found them.
    /// </summary>
    /// <remarks>
    /// Memoized per directory, because a file with a dozen imports asks the same question a dozen
    /// times and the answer is path arithmetic plus at most one walk looking for the owning project.
    /// Nothing here looks at the imported file, so adding or deleting a <c>.proto</c> cannot stale
    /// an entry; only a <c>.csproj</c> appearing between a file and its previous owner could, and
    /// that walk runs at all only when the caller had no project to name.
    /// </remarks>
    public static ImmutableArray<string> CandidateRoots(string protoFilePath, string? projectDirectory = null)
    {
        string? directory = SafeFullPath(protoFilePath) is { } full ? Path.GetDirectoryName(full) : null;

        if (string.IsNullOrEmpty(directory))
        {
            if (StandardImportsDirectory is { } only)
                return [only];

            return [];
        }

        string project = projectDirectory is null
            ? string.Empty
            : TrimSeparator(SafeFullPath(projectDirectory) ?? string.Empty);

        return s_roots.GetOrAdd(
            new RootKey(TrimSeparator(directory), project),
            static key => BuildRoots(key.Directory, key.ProjectDirectory));
    }

    private static ImmutableArray<string> BuildRoots(string directory, string projectDirectory)
    {
        // The project directory only governs files inside it; Grpc.Tools gives everything else its
        // own directory, so an out-of-project file is looked at on its own terms.
        string? owner = projectDirectory.Length > 0 && IsWithin(directory, projectDirectory)
            ? projectDirectory
            : NearestProjectDirectory(directory);

        var roots = ImmutableArray.CreateBuilder<string>();

        if (owner is not null && !PathsEqual(owner, directory))
            roots.Add(owner);

        roots.Add(directory);

        // Up to the owner, never past it: a project's own root is already the first candidate, and
        // a probe above it would resolve an import against a directory protoc was never given.
        string? ancestor = Parent(directory);

        while (ancestor is not null)
        {
            if (owner is not null && (PathsEqual(ancestor, owner) || !IsWithin(ancestor, owner)))
                break;

            roots.Add(ancestor);
            ancestor = Parent(ancestor);
        }

        // Last, not first. protoc puts its own imports ahead of the proto root, but it is also
        // given the `AdditionalImportDirs` this cannot see; preferring a file the user actually has
        // in their tree keeps navigation inside the solution, and the copies are the same file.
        if (StandardImportsDirectory is { } standard)
            roots.Add(standard);

        return roots.ToImmutable();
    }

    /// <summary>The nearest ancestor holding a <c>.csproj</c>, for a file whose project the caller
    /// did not name.</summary>
    private static string? NearestProjectDirectory(string directory)
    {
        try
        {
            for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            {
                if (current.Exists && current.EnumerateFiles("*.csproj").Any())
                    return TrimSeparator(current.FullName);
            }
        }
        catch (ArgumentException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>
    /// Locates the newest restored Grpc.Tools package's include directory.
    /// </summary>
    /// <remarks>
    /// Newest rather than the version the project references, because the reference is MSBuild
    /// state this does not have and the well-known protos are stable across versions — the risk of
    /// reading a <c>timestamp.proto</c> one minor version off is nothing next to failing to resolve
    /// it at all. A prerelease sorts as its release version, so a stable build of the same version
    /// wins only if it is enumerated first; either copy answers the question.
    /// </remarks>
    private static string? FindStandardImports()
    {
        try
        {
            foreach (string packages in PackageDirectories())
            {
                foreach (string package in new[] { "grpc.tools", "google.protobuf.tools" })
                {
                    if (FindNewestInclude(Path.Combine(packages, package)) is { } include)
                        return include;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static string? FindNewestInclude(string packageDirectory)
    {
        if (!Directory.Exists(packageDirectory))
            return null;

        string? best = null;
        Version bestVersion = new(0, 0);

        foreach (string versionDirectory in Directory.EnumerateDirectories(packageDirectory))
        {
            string include = Path.Combine(versionDirectory, "build", "native", "include");

            if (!Directory.Exists(include))
                continue;

            var version = ParseVersion(Path.GetFileName(versionDirectory));

            if (best is null || version > bestVersion)
            {
                best = include;
                bestVersion = version;
            }
        }

        return best;
    }

    private static IEnumerable<string> PackageDirectories()
    {
        if (Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } configured)
            yield return configured;

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (home.Length > 0)
            yield return Path.Combine(home, ".nuget", "packages");
    }

    private static Version ParseVersion(string name)
    {
        int dash = name.IndexOf('-');
        string number = dash < 0 ? name : name[..dash];

        return Version.TryParse(number, out var version) ? version : new Version(0, 0);
    }

    private static string? Combine(string root, string relative)
    {
        try
        {
            string full = Path.GetFullPath(Path.Combine(root, relative));
            return File.Exists(full) ? full : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (PathTooLongException)
        {
            return null;
        }
    }

    private static string? TryMakeRelative(string root, string filePath)
    {
        try
        {
            string relative = Path.GetRelativePath(root, filePath);

            if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
                return null;

            return relative.Replace(Path.DirectorySeparatorChar, '/').Replace('\\', '/');
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string ToSystemPath(string protoPath) =>
        protoPath.Replace('/', Path.DirectorySeparatorChar);

    private static string? SafeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (PathTooLongException)
        {
            return null;
        }
    }

    private static string? Parent(string directory)
    {
        string? parent = Path.GetDirectoryName(directory);
        return string.IsNullOrEmpty(parent) ? null : TrimSeparator(parent);
    }

    /// <summary>Whether a path is <paramref name="directory"/> itself or something under it.</summary>
    private static bool IsWithin(string path, string directory)
    {
        string root = TrimSeparator(directory);

        if (root.Length == 0 || path.Length < root.Length)
            return false;

        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return false;

        return path.Length == root.Length || IsSeparator(root[^1]) || IsSeparator(path[root.Length]);
    }

    private static bool PathsEqual(string? left, string? right) =>
        left is not null
        && right is not null
        && string.Equals(TrimSeparator(left), TrimSeparator(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Drops a trailing separator so that a directory has one spelling.
    /// </summary>
    /// <remarks>
    /// A volume root keeps its separator: <c>C:\</c> is the root of the drive but <c>C:</c> is the
    /// process's current directory on that drive, so trimming it would turn an absolute root into a
    /// relative one and quietly resolve every import somewhere else.
    /// </remarks>
    private static string TrimSeparator(string path)
    {
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return trimmed.Length == 0 || trimmed[^1] == Path.VolumeSeparatorChar ? path : trimmed;
    }

    private static bool IsSeparator(char c) =>
        c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;
}
