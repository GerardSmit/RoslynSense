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
    /// Scores are pack-local and only ever compared with each other. Contributor hits replace the
    /// ordinary search rather than mixing into it, so there is no scale to line up with — and
    /// borrowing the generic search's tier arithmetic would tie this to constants it keeps private
    /// for good reason.
    /// </summary>
    private const int Base = -1_000_000;

    /// <summary>Every control the segments could name, best first, or empty when they name none.</summary>
    public static async Task<IReadOnlyList<SearchHit>> ResolveAsync(
        Solution solution, ClientIdSegments segments, CancellationToken ct)
    {
        var seenProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<WebFormsFileIndex>();

        foreach (var project in solution.Projects)
        {
            ct.ThrowIfCancellationRequested();

            // A multi-targeted project appears once per framework over one directory, and each of
            // them would contribute the same markup.
            if (project.FilePath is not { } path || !seenProjects.Add(path))
                continue;

            files.AddRange(await WebFormsIndex.ForProjectAsync(project, ct));
        }

        if (files.Count == 0)
            return [];

        var controls = Controls(files, segments, ct);

        // The paste ended at a container rather than at a control — `dnn_ctr1848_OrderIntake_View`
        // names a module, not something inside it. Saying so beats saying nothing.
        return controls.Count > 0 ? controls : Files(files, segments);
    }

    private static List<SearchHit> Controls(
        List<WebFormsFileIndex> files, ClientIdSegments segments, CancellationToken ct)
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

                foreach (var control in file.Controls)
                {
                    if (!control.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (Verify(file, control, left) is not { } verdict)
                        continue;

                    found.Add((verdict.Consumed, verdict.Named, Hit(file, control)));
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
    /// Whether the segments to the left of the control's own id describe where it sits.
    /// </summary>
    /// <remarks>
    /// They are consumed right to left against the markup ancestors as an ordered subsequence.
    /// Whatever is left over ran past the file's own root, and can then only be the file itself —
    /// a module control DNN loaded by name, or a page class. Left over and matching neither, the
    /// candidate is somebody else's control that happens to share a name.
    /// </remarks>
    private static (int Consumed, bool Named)? Verify(
        WebFormsFileIndex file, WebFormsControlId control, ReadOnlySpan<string> left)
    {
        ImmutableArray<string> ancestors = control.Ancestors.IsDefault ? [] : control.Ancestors;

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

        if (l < 0)
            return (consumed, Named: false);

        // What is left has to name the file. DNN's ModuleControlFactory calls a dynamically loaded
        // module control after its `.ascx`, which is where a segment run like `OrderIntake_View`
        // in the middle of an id comes from.
        string run = string.Join('_', left[..(l + 1)].ToArray());

        return Names(file, run) ? (consumed + l + 1, Named: true) : null;
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
    private static List<SearchHit> Files(List<WebFormsFileIndex> files, ClientIdSegments segments)
    {
        var kept = segments.Kept;

        for (int width = kept.Length; width >= 1; width--)
        {
            string run = string.Join('_', kept.TakeLast(width));

            var hits = files
                .Where(file => Names(file, run))
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
