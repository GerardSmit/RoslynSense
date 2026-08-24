using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Lsp.Search;

namespace RoslynMCP.Languages.WebForms.Core;

/// <summary>
/// The control a runtime id names, found by matching the id's segments against real control trees.
/// </summary>
/// <remarks>
/// A <c>ClientID</c> is the one thing a user has when they are looking at a rendered page, at a
/// stack trace or at a browser's element inspector, and it is exactly what no search in this server
/// could answer: the generated segments make it match nothing, and a control inside an
/// <c>&lt;ItemTemplate&gt;</c> has no code-behind field to find in the first place.
/// <para>
/// The matching rule is an ordered subsequence in both directions, and no stricter rule survives
/// contact with a real page. Markup ancestors that are not naming containers never reach the id;
/// the item containers a data-bound control creates at runtime reach the id with nothing in the
/// markup to match them. What is left that is reliable is the order.
/// </para>
/// </remarks>
internal static class ClientIdSearch
{
    /// <summary>The panel is a picker; past this many answers it stops being one.</summary>
    private const int MaxHits = 20;

    /// <summary>
    /// How many trailing segments may be joined back together when hunting for the control's own
    /// <c>ID</c>.
    /// </summary>
    /// <remarks>
    /// Only the <c>_</c> form needs this: an <c>ID</c> may contain underscores, so
    /// <c>list_Order_Total</c> is a control called <c>Total</c>, <c>Order_Total</c> or
    /// <c>list_Order_Total</c>, and the markup decides. Four is past every id anyone writes.
    /// </remarks>
    private const int MaxIdSegments = 4;

    /// <summary>
    /// How many user controls deep the walk out of a file will go.
    /// </summary>
    /// <remarks>
    /// A bound rather than a budget: markup that registers itself, directly or round a ring, would
    /// otherwise recurse forever, and nothing is reached honestly at this depth anyway.
    /// </remarks>
    private const int MaxHostDepth = 16;

    /// <summary>
    /// Scores are pack-local and only ever compared with each other. Contributor hits replace the
    /// ordinary search rather than mixing into it, so there is no scale to line up with — and
    /// borrowing the generic search's tier arithmetic would tie this to constants it keeps private
    /// for good reason.
    /// </summary>
    private const int Base = -1_000_000;

    /// <summary>A file's summary, with the project directory its <c>~/</c> paths are relative to.</summary>
    private readonly record struct IndexedFile(WebFormsFileIndex Index, string ProjectDir)
    {
        public string FilePath => Index.FilePath;
    }

    /// <summary>A place some other file writes a tag for this one.</summary>
    private readonly record struct HostSite(IndexedFile File, WebFormsControlId Control);

    /// <summary>Every control the segments could name, best first, or empty when they name none.</summary>
    public static async Task<IReadOnlyList<SearchHit>> ResolveAsync(
        Solution solution, ClientIdSegments segments, CancellationToken ct)
    {
        var seenProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<IndexedFile>();

        foreach (var project in solution.Projects)
        {
            ct.ThrowIfCancellationRequested();

            // A multi-targeted project appears once per framework over one directory, and each of
            // them would contribute the same markup.
            if (project.FilePath is not { } path || !seenProjects.Add(path))
                continue;

            string dir = Path.GetDirectoryName(path) ?? string.Empty;

            foreach (var index in await WebFormsIndex.ForProjectAsync(project, ct))
                files.Add(new IndexedFile(index, dir));
        }

        if (files.Count == 0)
            return [];

        // Built once and shared by both readings: it is a pass over every registration in the
        // solution, and the reading only decides which segments are matched against it.
        var hosts = Hosts(files, ct);
        var readings = Readings(segments);

        foreach (var reading in readings)
        {
            if (Controls(files, reading, hosts, ct, lenient: false) is { Count: > 0 } controls)
                return controls;
        }

        // The paste ended at a container rather than at a control — `dnn_ctr1848_OrderIntake_View`
        // names a module, not something inside it. Saying so beats saying nothing.
        foreach (var reading in readings)
        {
            if (Files(files, reading) is { Count: > 0 } named)
                return named;
        }

        // Nothing explained the id whole. Ask again with the unexplainable segments skipped: a
        // real id carries containers this server cannot see — a control a page added in code, a
        // module DNN loaded by a name no markup writes — and one such segment is enough to sink
        // an id whose other segments name the right control exactly.
        foreach (var reading in readings)
        {
            if (Controls(files, reading, hosts, ct, lenient: true) is { Count: > 0 } guessed)
                return guessed;
        }

        return [];
    }

    /// <summary>
    /// The segmentations to try, in the order they deserve to win.
    /// </summary>
    /// <remarks>
    /// The id as written first. A control really called <c>btnSave_2</c> is far rarer than a
    /// repeater row, but where the markup declares one it is the right answer and the row-number
    /// reading would walk straight past it — so the markup decides which reading was meant, the
    /// same way it decides where an underscored id begins.
    /// </remarks>
    private static List<ClientIdSegments> Readings(ClientIdSegments segments) =>
        segments.WithoutRowNumbers() is { } trimmed ? [segments, trimmed] : [segments];

    private static List<SearchHit> Controls(
        List<IndexedFile> files, ClientIdSegments segments,
        Dictionary<string, List<HostSite>> hosts, CancellationToken ct, bool lenient)
    {
        var kept = segments.Kept;
        var found = new List<(int Consumed, bool Named, SearchHit Hit)>();

        // Anchored on the right, because that is the end of the id the user actually cares about
        // and the only segment guaranteed to be a control's own name. The `$` form knows exactly
        // where that segment starts; the `_` form has to try.
        int widest = segments.Exact ? 1 : Math.Min(MaxIdSegments, kept.Length);

        for (int width = 1; width <= widest; width++)
        {
            string id = string.Join('_', kept.TakeLast(width));
            var left = kept.AsSpan()[..^width];

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                foreach (var control in file.Index.Controls)
                {
                    if (!control.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (Verify(file, control, left, hosts, depth: 0, lenient) is not { } verdict)
                        continue;

                    found.Add((verdict.Consumed, verdict.Named, Hit(file.Index, control)));
                }
            }

            // Widening past a width that matched would only ever find a control whose name
            // swallowed one of the containers the user handed us.
            if (found.Count > 0)
                break;
        }

        return
        [
            .. found
                .OrderByDescending(f => f.Consumed)
                .ThenByDescending(f => f.Named)
                .ThenBy(f => f.Hit.FilePath.Length)
                .ThenBy(f => f.Hit.FilePath, StringComparer.OrdinalIgnoreCase)
                .Take(MaxHits)
                .Select((f, rank) => f.Hit with { Score = Base + rank }),
        ];
    }

    /// <summary>
    /// For each markup file, the tags other files write for it.
    /// </summary>
    /// <remarks>
    /// This is the edge that makes the containers of a real id resolvable at all. A page nests user
    /// controls several files deep — a module's <c>.ascx</c> writes a <c>&lt;uc:Filter&gt;</c> and
    /// that file writes a <c>&lt;uc:GenericFilter&gt;</c> — so the ancestors an id names are spread
    /// across the files, and a match confined to one of them sees only the innermost run.
    /// <para>
    /// A <c>Src</c> is resolved against the index rather than against the disk: every markup file
    /// worth reaching is already in this list, so the answer is a dictionary lookup instead of a
    /// <c>File.Exists</c> per registration per file — which on a site with a thousand pages is the
    /// difference between a keystroke and a pause.
    /// </para>
    /// </remarks>
    private static Dictionary<string, List<HostSite>> Hosts(
        List<IndexedFile> files, CancellationToken ct)
    {
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
            known.TryAdd(Full(file.FilePath), file.FilePath);

        var hosts = new Dictionary<string, List<HostSite>>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            if (file.Index.Registrations.IsDefaultOrEmpty || file.Index.Controls.IsDefaultOrEmpty)
                continue;

            // Resolved once per directive rather than once per tag written under it: a page that
            // repeats a user control twenty times registers it once.
            Dictionary<string, string>? targets = null;

            foreach (var registration in file.Index.Registrations)
            {
                if (registration.SourcePath is not { Length: > 0 } src)
                    continue;

                if (Resolve(file, src) is { } full && known.TryGetValue(full, out string? target))
                {
                    targets ??= new(StringComparer.OrdinalIgnoreCase);
                    targets[Tag(registration.Prefix, registration.TagName)] = target;
                }
            }

            if (targets is null)
                continue;

            foreach (var control in file.Index.Controls)
            {
                if (control.Prefix is not { Length: > 0 } prefix || control.Id.Length == 0)
                    continue;

                if (!targets.TryGetValue(Tag(prefix, control.TagName), out string? target))
                    continue;

                if (!hosts.TryGetValue(target, out var sites))
                    hosts[target] = sites = [];

                sites.Add(new HostSite(file, control));
            }
        }

        return hosts;
    }

    private static string Tag(string prefix, string tagName) => $"{prefix}:{tagName}";

    private static string Full(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    /// <summary>A <c>Src</c> as a full path — <c>~/</c> against the project, anything else against
    /// the file's own directory — without asking whether it exists.</summary>
    private static string? Resolve(IndexedFile file, string src)
    {
        string relative = src.Replace('/', Path.DirectorySeparatorChar).Trim();
        bool rooted = relative.StartsWith("~" + Path.DirectorySeparatorChar, StringComparison.Ordinal);

        string baseDir = rooted
            ? file.ProjectDir
            : Path.GetDirectoryName(file.FilePath) ?? string.Empty;

        if (rooted)
            relative = relative[2..];

        if (baseDir.Length == 0 || relative.Length == 0)
            return null;

        try
        {
            return Path.GetFullPath(Path.Combine(baseDir, relative));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the segments to the left of the control's own id describe where it sits.
    /// </summary>
    /// <remarks>
    /// They are consumed right to left against the markup ancestors as an ordered subsequence.
    /// Whatever is left over ran past the file's own root, and from there the id can only be
    /// continuing in whoever writes this file's tag — walked recursively, since a page nests user
    /// controls several deep — or naming the file itself, which is where a module control DNN
    /// loaded by name ends. Left over and matching neither, the candidate is somebody else's
    /// control that happens to share a name.
    /// <para>
    /// <paramref name="lenient"/> is the second pass, run only once the first has answered
    /// nothing: it lets the id skip a segment as well, so that one container this server cannot
    /// see does not sink an id whose other segments are exact. It still needs a segment that did
    /// match — see <see cref="Skipping"/>.
    /// </para>
    /// </remarks>
    private static (int Consumed, bool Named)? Verify(
        IndexedFile file, WebFormsControlId control, ReadOnlySpan<string> left,
        Dictionary<string, List<HostSite>> hosts, int depth, bool lenient)
    {
        ImmutableArray<string> ancestors = control.Ancestors.IsDefault ? [] : control.Ancestors;

        var (consumed, unmatched) = lenient
            ? Skipping(left, ancestors)
            : Strictly(left, ancestors);

        if (unmatched == 0)
            return (consumed, Named: false);

        // What is left may name the file. DNN's ModuleControlFactory calls a dynamically loaded
        // module control after its `.ascx`, which is where a segment run like `OrderIntake_View`
        // in the middle of an id comes from.
        var rest = left[..unmatched];

        if (Names(file.Index, string.Join('_', rest.ToArray())))
            return (consumed + rest.Length, Named: true);

        if (Outward(file, rest, hosts, depth, lenient) is { } outer)
            return (consumed + outer.Consumed, outer.Named);

        // Leftovers that reached neither a file name nor a host are dropped rather than fatal —
        // but only alongside segments that did match. Nothing matched means nothing tied this
        // candidate to the id but its own name, which is the same answer for every control in the
        // solution that shares it.
        return lenient && consumed > 0 ? (consumed, Named: false) : null;
    }

    /// <summary>
    /// The ancestor segments matched right to left, and how much of the left the walk never
    /// reached.
    /// </summary>
    /// <remarks>
    /// Every segment of <paramref name="left"/> has to land, in order, somewhere in
    /// <paramref name="ancestors"/>; extra ancestors are skipped freely, because a markup ancestor
    /// that is not a naming container never reaches the id.
    /// </remarks>
    private static (int Consumed, int Unmatched) Strictly(
        ReadOnlySpan<string> left, ImmutableArray<string> ancestors)
    {
        int l = left.Length - 1;
        int a = ancestors.Length - 1;
        int consumed = 0;

        while (l >= 0 && a >= 0)
        {
            if (left[l].Equals(ancestors[a], StringComparison.OrdinalIgnoreCase))
            {
                l--;
                consumed++;
            }

            a--;
        }

        return (consumed, l + 1);
    }

    /// <summary>
    /// The same walk with the id allowed to skip too: the longest run of segments that appears in
    /// both, in order, and the prefix of <paramref name="left"/> in front of the first of them.
    /// </summary>
    /// <remarks>
    /// Skipping on the ancestor side alone is what the id's shape justifies, and it is why
    /// <see cref="Strictly"/> is the first answer. Skipping on the id's side as well is a guess,
    /// made only once the strict reading has found nothing: a segment the index cannot account for
    /// — a container a page added in code, a naming container from a base class — otherwise stops
    /// the walk dead and takes every segment to its left with it, and those are the segments that
    /// say which of the solution's four <c>Amount</c>s the paste is about.
    /// <para>
    /// Longest-common-subsequence rather than another greedy pass, because greed picks the wrong
    /// one where a segment repeats: an id under <c>pnl / list / pnl</c> would spend its single
    /// <c>pnl</c> on the innermost and then find nothing for <c>list</c>. Both sides are a handful
    /// of segments, so the table is smaller than the string comparisons it saves.
    /// </para>
    /// </remarks>
    private static (int Consumed, int Unmatched) Skipping(
        ReadOnlySpan<string> left, ImmutableArray<string> ancestors)
    {
        int n = left.Length;
        int m = ancestors.Length;

        if (n == 0 || m == 0)
            return (0, n);

        // run[i, j] is the longest run shared by left[i..] and ancestors[j..].
        var run = new int[n + 1, m + 1];

        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                run[i, j] = left[i].Equals(ancestors[j], StringComparison.OrdinalIgnoreCase)
                    ? run[i + 1, j + 1] + 1
                    : Math.Max(run[i + 1, j], run[i, j + 1]);
            }
        }

        if (run[0, 0] == 0)
            return (0, n);

        // Where the run starts is where the leftovers end: everything in front of the first
        // matched segment is still the id's outer context, and still has a file name or a host to
        // be explained by.
        int first = 0;

        for (int j = 0; first < n && j < m;)
        {
            if (left[first].Equals(ancestors[j], StringComparison.OrdinalIgnoreCase))
                break;

            if (run[first + 1, j] >= run[first, j + 1])
                first++;
            else
                j++;
        }

        return (run[0, 0], first);
    }

    /// <summary>
    /// The same question asked of whoever writes this file's tag.
    /// </summary>
    /// <remarks>
    /// A file may be written in several places and they do not agree: the same user control under
    /// two different pages makes the leftover segments resolvable through one and not the other.
    /// The best answer wins rather than the first found, on the rule the hits themselves are ranked
    /// by — most segments explained, and an id that reached a file it can name over one that merely
    /// ran out of containers.
    /// </remarks>
    private static (int Consumed, bool Named)? Outward(
        IndexedFile file, ReadOnlySpan<string> left,
        Dictionary<string, List<HostSite>> hosts, int depth, bool lenient)
    {
        if (depth >= MaxHostDepth || left.Length == 0)
            return null;

        if (!hosts.TryGetValue(Full(file.FilePath), out var sites))
            return null;

        (int Consumed, bool Named)? best = null;

        foreach (var site in sites)
        {
            if (!site.Control.Id.Equals(left[^1], StringComparison.OrdinalIgnoreCase))
                continue;

            if (Verify(site.File, site.Control, left[..^1], hosts, depth + 1, lenient)
                is not { } verdict)
            {
                continue;
            }

            var candidate = (Consumed: verdict.Consumed + 1, verdict.Named);

            if (best is not { } incumbent
                || candidate.Consumed > incumbent.Consumed
                || (candidate.Consumed == incumbent.Consumed && candidate.Named && !incumbent.Named))
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>Whether a run of segments is this file's own name.</summary>
    private static bool Names(WebFormsFileIndex file, string run) =>
        Path.GetFileNameWithoutExtension(file.FilePath).Equals(run, StringComparison.OrdinalIgnoreCase)
        || (file.InheritsName is { Length: > 0 } page
            && page.Equals(run, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The files a run of segments names, for an id that stopped at a container.
    /// </summary>
    /// <remarks>
    /// Suffix runs, longest first: the leading segments of such an id are the containers above the
    /// module, which have no markup of their own to match.
    /// </remarks>
    private static List<SearchHit> Files(List<IndexedFile> files, ClientIdSegments segments)
    {
        var kept = segments.Kept;

        for (int width = kept.Length; width >= 1; width--)
        {
            string run = string.Join('_', kept.TakeLast(width));

            var hits = files
                .Where(file => Names(file.Index, run))
                .OrderBy(file => file.FilePath, StringComparer.OrdinalIgnoreCase)
                .Take(MaxHits)
                .Select((file, rank) => new SearchHit(
                    SearchItemKind.File,
                    Path.GetFileName(file.FilePath),
                    Path.GetDirectoryName(file.FilePath),
                    file.FilePath,
                    Line: 0, Character: 0, EndLine: 0, EndCharacter: 0,
                    LspSymbolKind.File,
                    Base + rank))
                .ToList();

            if (hits.Count > 0)
                return hits;
        }

        return [];
    }

    /// <summary>
    /// The <c>ID</c> attribute in the markup, which is the one answer that always exists.
    /// </summary>
    /// <remarks>
    /// Not the code-behind field. A control inside a template has none — which is the shape this
    /// whole path exists for — and where there is one the ordinary search already finds it by
    /// name, since it is a real C# declaration.
    /// </remarks>
    private static SearchHit Hit(WebFormsFileIndex file, WebFormsControlId control) =>
        new(SearchItemKind.Member,
            control.Id,
            file.InheritsName ?? Path.GetFileName(file.FilePath),
            file.FilePath,
            control.Span.Start.Line,
            control.Span.Start.Character,
            control.Span.End.Line,
            control.Span.End.Character,
            LspSymbolKind.Field,
            Base);
}
