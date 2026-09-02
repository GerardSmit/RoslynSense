using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.DotSettings.Core;

/// <summary>
/// One coverage exclusion, as ReSharper stores it: four <c>;</c>-separated patterns, each of which
/// may be <c>*</c>.
/// </summary>
/// <remarks>
/// <c>Zapto.Api;*;RegisterScopedAttribute;*</c> — module, namespace, type, method. The shape is
/// dotCover's, and dotCover is not the collector RoslynSense drives, so see
/// <see cref="ReSharperSettings.CoverletExcludeFilters"/> for what survives the translation.
/// </remarks>
internal readonly record struct CoverageExclusion(
    string Module, string Namespace, string Type, string Method)
{
    public static CoverageExclusion? Parse(string encoded)
    {
        var parts = DotSettingsEscaping.Decode(encoded).Split(';');

        return parts.Length == 4
            ? new CoverageExclusion(parts[0], parts[1], parts[2], parts[3])
            : null;
    }
}

/// <summary>
/// The settings a <c>.DotSettings</c> stack actually says that RoslynSense can act on.
/// </summary>
/// <remarks>
/// <para>
/// ReSharper defines a little over three thousand settings keys; this reads four of them. That is
/// not a first instalment. The rest are either about an IDE this is not — fonts, tool windows,
/// licence prompts, MRU lists — or about formatting, and formatting is the half of
/// <c>.DotSettings</c> that <c>.editorconfig</c> already carries and RoslynSense already honours
/// end to end. What is left are the keys with no <c>.editorconfig</c> equivalent, which are
/// exactly the ones teams commit.
/// </para>
/// <para>
/// Everything here is a <em>narrowing</em>: a folder that stops contributing a namespace segment,
/// a file that stops being a search result. So an empty stack has to behave exactly like no stack
/// at all, and every lookup below declines rather than matching when its set is empty.
/// </para>
/// </remarks>
internal sealed class ReSharperSettings
{
    /// <summary>The answer for a project with no layers: every predicate declines.</summary>
    public static ReSharperSettings Empty { get; } = new([], [], [], []);

    private readonly ImmutableHashSet<string> _namespaceFoldersToSkip;
    private readonly ImmutableHashSet<string> _excludedFiles;
    private readonly ImmutableArray<Regex> _fileMasksToSkip;

    private ReSharperSettings(
        ImmutableHashSet<string> namespaceFoldersToSkip,
        ImmutableHashSet<string> excludedFiles,
        ImmutableArray<Regex> fileMasksToSkip,
        ImmutableArray<CoverageExclusion> coverageExclusions)
    {
        _namespaceFoldersToSkip = namespaceFoldersToSkip;
        _excludedFiles = excludedFiles;
        _fileMasksToSkip = fileMasksToSkip;
        CoverageExclusions = coverageExclusions;
    }

    /// <summary>Whether this stack said anything at all.</summary>
    public bool IsEmpty =>
        _namespaceFoldersToSkip.IsEmpty
        && _excludedFiles.IsEmpty
        && _fileMasksToSkip.IsEmpty
        && CoverageExclusions.IsEmpty;

    public ImmutableArray<CoverageExclusion> CoverageExclusions { get; }

    /// <summary>The excluded paths, for a caller unioning several projects' stacks.</summary>
    internal ImmutableHashSet<string> ExcludedPaths => _excludedFiles;

    /// <summary>The file masks, for a caller unioning several projects' stacks.</summary>
    internal ImmutableArray<Regex> FileMasks => _fileMasksToSkip;

    /// <summary>
    /// The folders between a project and a file that contribute a namespace segment, with the ones
    /// marked "do not create a namespace" taken out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The setting is per folder, not per subtree, and ReSharper writes the full project-relative
    /// path of every marked folder — a project that skips <c>Builder\Controls\Common</c> has all
    /// three of <c>builder</c>, <c>builder\controls</c> and <c>builder\controls\common</c> stored
    /// when all three are marked. So the test is on the running prefix, not on the segment name: a
    /// project that marks <c>Extensions</c> at its root must not thereby unmark
    /// <c>Api\Extensions</c>.
    /// </para>
    /// <para>
    /// <paramref name="relativeDirectory"/> is the directory relative to the project. An empty or
    /// <c>.</c> path yields nothing, which is the file-at-project-root case.
    /// </para>
    /// </remarks>
    public IEnumerable<string> NamespaceSegments(string? relativeDirectory)
    {
        if (string.IsNullOrEmpty(relativeDirectory) || relativeDirectory == ".")
            yield break;

        var separators = new[] { '\\', '/' };
        string prefix = "";

        foreach (string segment in relativeDirectory.Split(separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
                continue;

            prefix = prefix.Length == 0 ? segment : prefix + "\\" + segment;

            if (!_namespaceFoldersToSkip.Contains(prefix))
                yield return segment;
        }
    }

    /// <summary>Whether this file is one the team excluded from analysis, by path or by mask.</summary>
    /// <remarks>
    /// A spec may name a folder rather than a file, so the walk goes up the path as well as
    /// testing it: excluding <c>Generated</c> has to exclude everything under it, not just a file
    /// of that name.
    /// </remarks>
    public bool IsExcluded(string absolutePath)
    {
        if (!_excludedFiles.IsEmpty)
        {
            string? current = PathHelper.NormalizePath(absolutePath);

            while (current is { Length: > 0 })
            {
                if (_excludedFiles.Contains(current))
                    return true;

                current = Path.GetDirectoryName(current);
            }
        }

        if (_fileMasksToSkip.IsEmpty)
            return false;

        string name = Path.GetFileName(absolutePath);

        foreach (var mask in _fileMasksToSkip)
        {
            if (mask.IsMatch(name))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The coverage exclusions in the only form the collector RoslynSense drives understands.
    /// </summary>
    /// <remarks>
    /// coverlet filters on <c>[module]type</c>, so a module, a namespace and a type translate and
    /// a method does not. An exclusion naming a single method is therefore dropped rather than
    /// widened to its whole type — excluding more code than the team asked for would move a
    /// coverage number in the flattering direction, which is the one direction a wrong answer must
    /// never move.
    /// </remarks>
    public ImmutableArray<string> CoverletExcludeFilters
    {
        get
        {
            if (CoverageExclusions.IsEmpty)
                return [];

            var filters = ImmutableArray.CreateBuilder<string>();

            foreach (var exclusion in CoverageExclusions)
            {
                if (exclusion.Method != "*")
                    continue;

                string type = exclusion.Namespace == "*"
                    ? (exclusion.Type == "*" ? "*" : "*." + exclusion.Type)
                    : exclusion.Namespace + "." + exclusion.Type;

                filters.Add("[" + exclusion.Module + "]" + type);
            }

            return filters.ToImmutable();
        }
    }

    // ---- resolution -------------------------------------------------------------------------

    private const string NamespaceFoldersKey =
        "CodeInspection/NamespaceProvider/NamespaceFoldersToSkip";
    private const string ExcludedFilesKey =
        "CodeInspection/ExcludedFiles/FilesAndFoldersToSkip2";
    private const string FileMasksKey =
        "CodeInspection/ExcludedFiles/FileMasksToSkip";
    private const string CoverageFiltersKey =
        "Environment/Filtering/ExcludeCoverageFilters";

    private static readonly ConcurrentDictionary<string, (long Stamp, ReSharperSettings Settings)> s_cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The resolved stack for a project, cached against its layers' write times so an edit to a
    /// layer is picked up without a restart.
    /// </summary>
    public static ReSharperSettings ForProject(string projectPath)
    {
        var layers = DotSettingsLayers.For(projectPath);

        if (layers.IsEmpty)
            return Empty;

        long stamp = Stamp(layers);

        if (s_cache.TryGetValue(projectPath, out var cached) && cached.Stamp == stamp)
            return cached.Settings;

        var settings = Resolve(layers, projectPath);
        s_cache[projectPath] = (stamp, settings);
        return settings;
    }

    /// <summary>Drops the resolve cache. For tests, and for a solution close.</summary>
    public static void Clear()
    {
        s_cache.Clear();
        DotSettingsDocumentCache.Clear();
    }

    private static long Stamp(ImmutableArray<string> layers)
    {
        long stamp = layers.Length;

        foreach (string layer in layers)
        {
            try
            {
                stamp = (stamp * 31) + new FileInfo(layer).LastWriteTimeUtc.Ticks;
            }
            catch (IOException)
            {
                // An unreadable layer is a cache miss, not a crash.
                stamp = (stamp * 31) + 1;
            }
        }

        return stamp;
    }

    private static ReSharperSettings Resolve(ImmutableArray<string> layers, string projectPath)
    {
        var namespaceFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excludedSpecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var masks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var coverage = new HashSet<string>(StringComparer.Ordinal);

        // Weakest first, so a stronger layer's removal lands after the addition it undoes.
        foreach (string layer in layers)
        {
            if (DotSettingsDocumentCache.Get(layer) is not { } document)
                continue;

            foreach (var entry in document.Entries)
            {
                var target = entry.Path switch
                {
                    NamespaceFoldersKey => namespaceFolders,
                    ExcludedFilesKey => excludedSpecs,
                    FileMasksKey => masks,
                    CoverageFiltersKey => coverage,
                    _ => null,
                };

                if (target is null || entry.Index is not { } index)
                    continue;

                // FilesAndFoldersToSkip2 is not a set of flags: its value names one of
                // ReSharper's states for the file, and only ExplicitlyExcluded is an exclusion.
                // ForceIncluded is the opposite — what the "include this file again" gesture
                // writes, keeping a file in analysis that a broader rule would have taken out —
                // and reading it as a boolean excluded every file a team had deliberately kept.
                if (entry.Path == ExcludedFilesKey && entry.Accessor == "EntryIndexedValue")
                {
                    if (IsExcludedState(entry.Value))
                        target.Add(index);
                    else
                        target.Remove(index);

                    continue;
                }

                if (entry.IsRemovedIndex)
                    target.Remove(index);
                else if (entry.IsPresentIndex)
                    target.Add(index);
            }
        }

        return new ReSharperSettings(
            namespaceFolders.Select(Normalize).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
            ExcludedFileResolver.Resolve(excludedSpecs, projectPath),
            masks.Select(MaskRegex).ToImmutableArray(),
            coverage.Select(CoverageExclusion.Parse).OfType<CoverageExclusion>().ToImmutableArray());
    }

    /// <summary>
    /// Whether a <c>FilesAndFoldersToSkip2</c> value means the file is out of analysis.
    /// </summary>
    /// <remarks>
    /// <c>True</c> is accepted alongside ReSharper's own <c>ExplicitlyExcluded</c> because a
    /// hand-written layer may say it that way, and it can only ever have meant "excluded".
    /// Everything else — <c>ForceIncluded</c> above all — is not an exclusion.
    /// </remarks>
    private static bool IsExcludedState(string? value) =>
        string.Equals(value, "ExplicitlyExcluded", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "True", StringComparison.OrdinalIgnoreCase);

    /// <summary>Folder paths compare as relative Windows paths, whichever separator was stored.</summary>
    private static string Normalize(string folder) =>
        folder.Replace('/', '\\').Trim('\\');

    /// <summary>
    /// A file mask (<c>*.designer.cs</c>) as a regex over the file name. Only <c>*</c> and
    /// <c>?</c> are wildcards; everything else is literal.
    /// </summary>
    private static Regex MaskRegex(string mask)
    {
        string pattern = "^" + Regex.Escape(mask)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";

        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
