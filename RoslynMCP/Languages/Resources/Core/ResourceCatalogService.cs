using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.Resources.Core;

/// <summary>Which <c>.resx</c> files a project's catalog covers, and how their names decompose.</summary>
internal sealed record ResourceDiscoveryOptions
{
    public static ResourceDiscoveryOptions Default { get; } = new();

    /// <summary>Globs relative to the project directory. Empty means every <c>.resx</c>.</summary>
    public ImmutableArray<string> Include { get; init; } = [];

    public ImmutableArray<string> Exclude { get; init; } = [];

    /// <summary>DNN's customizations, which sit beside the base file rather than under
    /// <c>/Portals/{id}/</c>. Portal outranks Host because the portal-specific file is the one the
    /// runtime probes first.</summary>
    public ImmutableArray<ResourceOverrideRule> Overrides { get; init; } =
        [new("Portal-*", 2), new("Host", 1)];
}

/// <summary>
/// The project's resource families, and the key tables behind them.
/// </summary>
/// <remarks>
/// Two phases, because a real multi-portal site has thousands of <c>.resx</c> files. Discovery and
/// grouping are eager and open no file at all — they answer "which families exist and what is in
/// them" from names. Key tables are read one family at a time, so completion in a page materializes
/// that page's family rather than the catalog.
/// <para>
/// Invalidation splits the same way, and the distinction is load-bearing: editing a file's contents
/// rebuilds that family and <em>keeps</em> the catalog, because membership did not move; creating,
/// deleting or renaming one rebuilds the catalog.
/// </para>
/// </remarks>
internal static class ResourceCatalogService
{
    /// <summary>
    /// One file's key table, keyed on its checksum and nothing else.
    /// </summary>
    /// <remarks>
    /// <see cref="WebForms.Core.WebFormsIndex"/> needs a <c>(checksum, tree)</c> pair because
    /// markup is not self-contained — registering a tag prefix in <c>web.config</c> changes which
    /// controls a page has in scope without touching a byte of the page. A <c>.resx</c> is the
    /// whole input to its own key table, so the checksum answers the only question there is. It is
    /// also why a content edit needs no eviction here: different bytes, different checksum.
    /// </remarks>
    private sealed record FileCacheEntry(
        ImmutableArray<byte> Checksum,
        ImmutableDictionary<string, ResourceEntry> Entries,
        ImmutableArray<string> DuplicateKeys);

    private static readonly ConcurrentDictionary<string, FileCacheEntry> s_files =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Materialized families, per directory — families never cross one, so a content edit
    /// drops exactly the directory it landed in.</summary>
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ResourceFamily>> s_loaded =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, ProjectResources> s_byDirectory =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The per-snapshot fast path. A <c>.resx</c> is not a Roslyn document, so a project's
    /// snapshots come and go without the catalog meaning anything different; the entry they all
    /// resolve to is shared through <see cref="s_byDirectory"/> so that a keystroke in a
    /// <c>.cs</c> file does not cost a directory walk.
    /// </summary>
    private static readonly ConditionalWeakTable<Project, ProjectResources> s_projects = new();

    /// <summary>Every family in the project, grouped but not read.</summary>
    public static ResourceCatalog Get(Project project, ResourceDiscoveryOptions? options = null)
    {
        if (Path.GetDirectoryName(project.FilePath) is not { Length: > 0 } directory)
            return ResourceCatalog.Empty;

        return s_projects.GetValue(
            project,
            _ => Resources(directory, options ?? ResourceDiscoveryOptions.Default)).Catalog;
    }

    /// <summary>Every family under a directory, for callers that have no Roslyn project.</summary>
    public static ResourceCatalog Get(string directory, ResourceDiscoveryOptions? options = null) =>
        Resources(directory, options ?? ResourceDiscoveryOptions.Default).Catalog;

    /// <summary>
    /// The same family with its key tables read. Returns <paramref name="family"/> unchanged when
    /// it is already loaded, so a caller may pass either shape.
    /// </summary>
    public static ResourceFamily Load(ResourceFamily family)
    {
        if (family.KeysLoaded)
            return family;

        var byBaseName = s_loaded.GetOrAdd(
            family.Directory, _ => new ConcurrentDictionary<string, ResourceFamily>(StringComparer.OrdinalIgnoreCase));

        if (byBaseName.TryGetValue(family.BaseName, out var cached) && SameMembers(cached, family))
            return cached;

        var loaded = Materialize(family);
        byBaseName[family.BaseName] = loaded;
        return loaded;
    }

    /// <summary>One file's key table, read through the open buffer when there is one.</summary>
    public static ResourceFileIndex Read(ResourceFileIndex file)
    {
        if (ReadText(file.FilePath) is not { } text)
            return file;

        var checksum = text.GetChecksum();

        if (s_files.TryGetValue(file.FilePath, out var cached)
            && cached.Checksum.AsSpan().SequenceEqual(checksum.AsSpan()))
        {
            return file with { Entries = cached.Entries, DuplicateKeys = cached.DuplicateKeys };
        }

        var contents = ResxReader.Read(text);
        s_files[file.FilePath] = new FileCacheEntry(checksum, contents.Entries, contents.DuplicateKeys);

        return file with { Entries = contents.Entries, DuplicateKeys = contents.DuplicateKeys };
    }

    /// <summary>
    /// The buffer a file's spans were measured against: the open document when the user has one,
    /// the file on disk otherwise.
    /// </summary>
    /// <remarks>
    /// Public because a <see cref="ResourceEntry"/> carries offsets and a client wants lines, and
    /// the conversion is only right against the same text the reader saw — the open buffer and the
    /// file on disk disagree for as long as an edit is unsaved.
    /// </remarks>
    public static SourceText? Text(string filePath) => ReadText(filePath);

    /// <summary>A <c>.resx</c> was edited. Its family is re-read; the catalog stands, because the
    /// set of families a directory holds is a function of file names and none of them moved.</summary>
    public static void InvalidateContent(string filePath)
    {
        if (Path.GetDirectoryName(PathHelper.NormalizePath(filePath)) is { Length: > 0 } directory)
            s_loaded.TryRemove(directory, out _);
    }

    /// <summary>A <c>.resx</c> was created, deleted or renamed. Membership moved, so every catalog
    /// covering the path has to be regrouped.</summary>
    public static void InvalidateLayout(string filePath)
    {
        InvalidateContent(filePath);

        string path = PathHelper.NormalizePath(filePath);

        foreach (var (directory, resources) in s_byDirectory)
        {
            if (IsUnder(path, directory))
                resources.Invalidate();
        }
    }

    /// <summary>Segment-aware, so that a change under <c>Web.Tests</c> leaves <c>Web</c> alone.</summary>
    private static bool IsUnder(string path, string directory) =>
        path.Length > directory.Length
        && path.StartsWith(directory, StringComparison.OrdinalIgnoreCase)
        && (path[directory.Length] == Path.DirectorySeparatorChar
            || path[directory.Length] == Path.AltDirectorySeparatorChar);

    public static void InvalidateAll()
    {
        s_files.Clear();
        s_loaded.Clear();

        foreach (var resources in s_byDirectory.Values)
            resources.Invalidate();
    }

    private static ProjectResources Resources(string directory, ResourceDiscoveryOptions options) =>
        s_byDirectory.GetOrAdd(
            PathHelper.NormalizePath(directory), key => new ProjectResources(key, options));

    private static ResourceFamily Materialize(ResourceFamily family)
    {
        var files = ImmutableArray.CreateBuilder<ResourceFileIndex>(family.Files.Length);

        foreach (var file in family.Files)
            files.Add(Read(file));

        var allKeys = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var neutralKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            foreach (string key in file.Entries.Keys)
            {
                if (seen.Add(key))
                    allKeys.Add(key);

                if (file.OverrideRank == 0)
                    neutralKeys.Add(key);
            }
        }

        var overrideOnly = ImmutableArray.CreateBuilder<string>();

        foreach (string key in allKeys)
        {
            if (!neutralKeys.Contains(key))
                overrideOnly.Add(key);
        }

        return family with
        {
            Files = files.ToImmutable(),
            AllKeys = allKeys.ToImmutable(),
            OverrideOnlyKeys = overrideOnly.ToImmutable(),
            KeysLoaded = true,
        };
    }

    /// <summary>Open buffer first — otherwise hover shows the value on disk while the user is
    /// editing the <c>.resx</c>.</summary>
    private static SourceText? ReadText(string path)
    {
        if (OpenDocumentStore.TryGet(path, out var open))
            return open;

        try
        {
            if (!File.Exists(path))
                return null;

            using var stream = File.OpenRead(path);
            return SourceText.From(stream);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Whether a cached family still describes the same set of files as the shell the
    /// catalog just handed out.</summary>
    private static bool SameMembers(ResourceFamily cached, ResourceFamily shell)
    {
        if (cached.Files.Length != shell.Files.Length)
            return false;

        for (int i = 0; i < cached.Files.Length; i++)
        {
            if (!cached.Files[i].FilePath.Equals(shell.Files[i].FilePath, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static ResourceCatalog Build(string directory, ResourceDiscoveryOptions options)
    {
        var byDirectory = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (string file in Enumerate(directory, options))
        {
            if (Path.GetDirectoryName(file) is not { Length: > 0 } parent)
                continue;

            if (!byDirectory.TryGetValue(parent, out var files))
                byDirectory[parent] = files = [];

            files.Add(file);
        }

        var families = ImmutableArray.CreateBuilder<ResourceFamily>();

        foreach (string parent in byDirectory.Keys.Order(StringComparer.OrdinalIgnoreCase))
            families.AddRange(ResourceFamilyParser.Decompose(parent, byDirectory[parent], options.Overrides));

        return ResourceCatalog.Create(families.ToImmutable());
    }

    private static List<string> Enumerate(string directory, ResourceDiscoveryOptions options)
    {
        var files = new List<string>();

        if (!Directory.Exists(directory))
            return files;

        try
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*.resx", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(directory, file);

                if (IsBuildOutput(relative) || !Included(relative, options))
                    continue;

                files.Add(file);
            }
        }
        catch (IOException)
        {
            // A directory vanished mid-walk; report what was found.
        }
        catch (UnauthorizedAccessException)
        {
        }

        return files;
    }

    /// <summary>Any segment, not just the first: a DNN site has a <c>bin</c> under every module
    /// folder, and the satellite assemblies in it carry copies of the same resource names.</summary>
    private static bool IsBuildOutput(string relativePath)
    {
        foreach (string segment in relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("obj", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Included(string relativePath, ResourceDiscoveryOptions options)
    {
        string path = relativePath.Replace('\\', '/');

        foreach (string pattern in options.Exclude)
        {
            if (GlobRegex(pattern).IsMatch(path))
                return false;
        }

        if (options.Include.IsDefaultOrEmpty)
            return true;

        foreach (string pattern in options.Include)
        {
            if (GlobRegex(pattern).IsMatch(path))
                return true;
        }

        return false;
    }

    private static readonly ConcurrentDictionary<string, Regex> s_globs = new(StringComparer.Ordinal);

    private static Regex GlobRegex(string pattern) =>
        s_globs.GetOrAdd(pattern, static glob =>
        {
            var builder = new StringBuilder("^");

            for (int i = 0; i < glob.Length; i++)
            {
                switch (glob[i])
                {
                    case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                        builder.Append(".*");
                        i++;
                        // "**/" also has to match nothing at all, so that "**/*.resx" covers a file
                        // sitting in the project root.
                        if (i + 1 < glob.Length && (glob[i + 1] == '/' || glob[i + 1] == '\\'))
                            i++;
                        break;
                    case '*':
                        builder.Append("[^/]*");
                        break;
                    case '?':
                        builder.Append("[^/]");
                        break;
                    case '\\':
                    case '/':
                        builder.Append('/');
                        break;
                    default:
                        builder.Append(Regex.Escape(glob[i].ToString()));
                        break;
                }
            }

            return new Regex(builder.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        });

    private sealed class ProjectResources(string directory, ResourceDiscoveryOptions options)
    {
        private readonly Lock _gate = new();
        private ResourceCatalog? _catalog;

        public ResourceCatalog Catalog
        {
            get
            {
                lock (_gate)
                    return _catalog ??= Build(directory, options);
            }
        }

        public void Invalidate()
        {
            lock (_gate)
                _catalog = null;
        }
    }
}
