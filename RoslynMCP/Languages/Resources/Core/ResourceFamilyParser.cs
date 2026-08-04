using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RoslynMCP.Languages.Resources.Core;

/// <summary>
/// Groups a directory's <c>.resx</c> file names into families: which file is the base, which are
/// translations of it, and which are customizations sitting beside it.
/// </summary>
/// <remarks>
/// Names only — no file is opened here, because the grouping is the eager half of the catalog and
/// a multi-portal site has thousands of files.
/// <para>
/// The whole trick is that a lone file name is never decomposed.
/// <see cref="CultureInfo.GetCultureInfo(string)"/> throwing is not a usable signal: on .NET with
/// ICU it returns a neutral custom culture for any well-formed unknown subtag, so
/// <c>My.Company.Strings.resx</c> would parse <c>Company</c> as a culture and invent a family. The
/// set decomposes instead — <c>View.ascx.resx</c> existing is what licenses reading
/// <c>View.ascx.nl-NL.Portal-3.resx</c> as a variant, and <c>My.Company.Strings</c> has no shorter
/// sibling stem prefixing it, so it is a base and no culture parsing is ever attempted.
/// </para>
/// </remarks>
internal static partial class ResourceFamilyParser
{
    /// <summary>
    /// The families the given files in one directory form. Families never cross directories: DNN
    /// puts customizations beside the base file, and so does .NET.
    /// </summary>
    public static ImmutableArray<ResourceFamily> Decompose(
        string directory, IReadOnlyList<string> filePaths, ImmutableArray<ResourceOverrideRule> overrides)
    {
        if (filePaths.Count == 0)
            return [];

        var stems = new List<(string Path, string Stem)>(filePaths.Count);
        foreach (string path in filePaths)
            stems.Add((path, Path.GetFileNameWithoutExtension(path)));

        // Ascending length is what makes "the longest other stem that prefixes this one" a linear
        // scan of everything already processed, and it guarantees a stem's base is decided before
        // the stem itself is.
        stems.Sort(static (a, b) => a.Stem.Length != b.Stem.Length
            ? a.Stem.Length - b.Stem.Length
            : string.CompareOrdinal(a.Stem, b.Stem));

        var baseOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var variants = new Dictionary<string, Variant>(StringComparer.OrdinalIgnoreCase);
        var selfBased = new List<string>();

        for (int i = 0; i < stems.Count; i++)
        {
            string stem = stems[i].Stem;
            string? root = Root(stems, i, baseOf);

            if (root is not null
                && TryParseTail(stem[(root.Length + 1)..], overrides, out var variant))
            {
                baseOf[stem] = root;
                variants[stem] = variant;
                continue;
            }

            baseOf[stem] = stem;
            selfBased.Add(stem);
        }

        var folded = Fold(selfBased, overrides);

        var members = new Dictionary<string, List<ResourceFileIndex>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, stem) in stems)
        {
            string root = baseOf[stem];

            if (folded.TryGetValue(root, out string? prefix)
                && TryParseTail(stem[(prefix.Length + 1)..], overrides, out var refolded))
            {
                Add(members, prefix, path, refolded);
                continue;
            }

            Add(members, root, path,
                root.Equals(stem, StringComparison.OrdinalIgnoreCase)
                    ? default
                    : variants[stem]);
        }

        var families = ImmutableArray.CreateBuilder<ResourceFamily>(members.Count);

        foreach (string baseName in members.Keys.Order(StringComparer.OrdinalIgnoreCase))
        {
            var files = members[baseName];
            files.Sort(Precedence);
            families.Add(ResourceFamily.Unread(baseName, directory, [.. files]));
        }

        return families.ToImmutable();
    }

    /// <summary>What a variant segment run says about one file.</summary>
    private readonly record struct Variant(CultureInfo? Culture, int Rank, string? Tag);

    private static void Add(
        Dictionary<string, List<ResourceFileIndex>> members, string baseName, string path, Variant variant)
    {
        if (!members.TryGetValue(baseName, out var files))
            members[baseName] = files = [];

        files.Add(ResourceFileIndex.Unread(path, variant.Culture, variant.Rank, variant.Tag));
    }

    /// <summary>
    /// The base stem the stem at <paramref name="index"/> belongs under, or null when nothing
    /// shorter prefixes it. Resolved transitively, so
    /// <c>View.ascx.nl-NL.Portal-3</c> lands on <c>View.ascx</c> rather than on the intermediate
    /// <c>View.ascx.nl-NL</c>, and its whole tail is parsed in one go.
    /// </summary>
    private static string? Root(
        IReadOnlyList<(string Path, string Stem)> stems, int index, Dictionary<string, string> baseOf)
    {
        string stem = stems[index].Stem;
        string? longest = null;

        for (int i = 0; i < index; i++)
        {
            string candidate = stems[i].Stem;

            // "B." plus at least one character of tail.
            if (candidate.Length + 1 >= stem.Length
                || stem[candidate.Length] != '.'
                || !stem.AsSpan(0, candidate.Length).Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            longest = candidate;
        }

        return longest is null ? null : baseOf[longest];
    }

    /// <summary>
    /// The orphan pass: stems that became their own base but read as a translation of something.
    /// A second stem has to agree on the prefix before a family is invented from one — a lone
    /// <c>Report.de.resx</c> could as easily be a file called <c>Report.de</c>, and a phantom
    /// family is worse than a missed one.
    /// </summary>
    private static Dictionary<string, string> Fold(
        List<string> selfBased, ImmutableArray<ResourceOverrideRule> overrides)
    {
        var byPrefix = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (string stem in selfBased)
        {
            if (!TrySplit(stem, overrides, out string prefix))
                continue;

            if (!byPrefix.TryGetValue(prefix, out var group))
                byPrefix[prefix] = group = [];

            group.Add(stem);
        }

        var folded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (prefix, group) in byPrefix)
        {
            if (group.Count < 2)
                continue;

            foreach (string stem in group)
                folded[stem] = prefix;
        }

        return folded;
    }

    /// <summary>
    /// Reads the segments after the base name, right to left: customizations first, then at most
    /// one culture. Anything left over means the tail is not a variant run at all and the stem is
    /// its own base — which is what keeps <c>My.Company.Strings</c> beside <c>My.Company</c>
    /// instead of underneath it.
    /// </summary>
    private static bool TryParseTail(
        string tail, ImmutableArray<ResourceOverrideRule> overrides, out Variant variant)
    {
        variant = default;

        var segments = tail.Split('.');
        int end = segments.Length;
        int rank = 0;
        string? tag = null;

        while (end > 0 && TryMatchOverride(segments[end - 1], overrides, out int matched))
        {
            if (matched > rank)
            {
                rank = matched;
                tag = segments[end - 1];
            }

            end--;
        }

        switch (end)
        {
            case 0:
                variant = new Variant(null, rank, tag);
                return true;
            case 1 when TryParseCulture(segments[0], out var culture):
                variant = new Variant(culture, rank, tag);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// The neutral stem a variant's own name implies — <c>Global.ascx.de-DE</c> yields
    /// <c>Global.ascx</c>, and <c>My.Company.Strings</c> yields nothing because none of its
    /// segments is a culture or a customization.
    /// </summary>
    /// <remarks>
    /// Name-only, for the callers that have one file rather than a directory listing. Grouping
    /// against siblings is still the accurate answer and is what <see cref="Decompose"/> does; this
    /// is the honest approximation for a caller that cannot see them.
    /// </remarks>
    public static bool TryStripVariant(string stem, out string neutral) =>
        TrySplit(stem, ResourceDiscoveryOptions.Default.Overrides, out neutral);

    /// <summary>The prefix a stem's own trailing segments imply, for the orphan pass.</summary>
    private static bool TrySplit(
        string stem, ImmutableArray<ResourceOverrideRule> overrides, out string prefix)
    {
        prefix = string.Empty;

        var segments = stem.Split('.');
        int end = segments.Length;
        bool stripped = false;

        while (end > 1 && TryMatchOverride(segments[end - 1], overrides, out _))
        {
            end--;
            stripped = true;
        }

        if (end > 1 && TryParseCulture(segments[end - 1], out _))
        {
            end--;
            stripped = true;
        }

        if (!stripped)
            return false;

        prefix = string.Join('.', segments, 0, end);
        return prefix.Length > 0;
    }

    private static bool TryMatchOverride(
        string segment, ImmutableArray<ResourceOverrideRule> overrides, out int rank)
    {
        rank = 0;

        foreach (var rule in overrides)
        {
            if (!MatchesPattern(segment, rule.Pattern))
                continue;

            if (rule.Rank > rank)
                rank = rule.Rank;
        }

        return rank > 0;
    }

    /// <summary>A single segment against a <c>*</c>/<c>?</c> wildcard — <c>Portal-*</c>.</summary>
    private static bool MatchesPattern(string segment, string pattern)
    {
        int s = 0, p = 0, star = -1, mark = 0;

        while (s < segment.Length)
        {
            if (p < pattern.Length
                && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(segment[s])))
            {
                s++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = s;
            }
            else if (star >= 0)
            {
                p = star + 1;
                s = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
            p++;

        return p == pattern.Length;
    }

    /// <summary>
    /// A culture segment has to pass both tests. The shape alone would take <c>Company</c>, and
    /// installed-culture membership alone would take anything ICU is willing to synthesize.
    /// </summary>
    private static bool TryParseCulture(string candidate, out CultureInfo? culture)
    {
        culture = null;

        if (candidate.Length == 0 || !CultureShape().IsMatch(candidate) || !s_installed.Contains(candidate))
            return false;

        try
        {
            culture = CultureInfo.GetCultureInfo(candidate);
        }
        catch (CultureNotFoundException)
        {
            return false;
        }

        return true;
    }

    /// <summary>Neutral first, then cultures by name, then customizations by rank.</summary>
    private static int Precedence(ResourceFileIndex left, ResourceFileIndex right)
    {
        int result = (left.OverrideRank == 0 ? 0 : 1).CompareTo(right.OverrideRank == 0 ? 0 : 1);
        if (result != 0)
            return result;

        result = left.OverrideRank.CompareTo(right.OverrideRank);
        if (result != 0)
            return result;

        result = (left.Culture is null ? 0 : 1).CompareTo(right.Culture is null ? 0 : 1);
        if (result != 0)
            return result;

        result = string.CompareOrdinal(left.Culture?.Name, right.Culture?.Name);
        return result != 0 ? result : string.CompareOrdinal(left.FilePath, right.FilePath);
    }

    private static readonly HashSet<string> s_installed = BuildInstalled();

    private static HashSet<string> BuildInstalled()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var culture in CultureInfo.GetCultures(CultureTypes.AllCultures))
        {
            if (culture.Name.Length > 0)
                names.Add(culture.Name);
        }

        return names;
    }

    [GeneratedRegex(@"^[A-Za-z]{2,3}(-[A-Za-z]{4})?(-([A-Za-z]{2}|[0-9]{3}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex CultureShape();
}
