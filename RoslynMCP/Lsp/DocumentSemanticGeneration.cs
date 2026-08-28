using Microsoft.CodeAnalysis;

namespace RoslynMCP.Lsp;

/// <summary>
/// What a solution-wide answer about one C# document depends on: the document's own text, and the
/// text of every project that could mention its symbols — the ones its project references, and the
/// ones that reference it.
/// </summary>
/// <remarks>
/// <para>
/// The key every memo over a solution-wide query is versioned by. It started inside
/// <see cref="Handlers.CodeLensHandler"/>, which is the reason a reference count in the gutter is
/// computed once and then reused until something it could depend on moves. It lives here because
/// the same query arrives by more than one route: <c>codeLens/resolve</c> and
/// <c>roslynSense/inheritanceMarkers</c> both end in the same <c>SymbolFinder</c> sweep for the
/// same symbol, and only the first of them was memoized — so the identical work was free as a lens
/// and paid in full as a gutter arrow.
/// </para>
/// <para>
/// Two deliberately wide choices, both because the queries this keys are reference-shaped. The
/// dependents are in: a new call site for this file's method is typed into a project that
/// <em>depends on</em> this one, so a key that only looked at what this project references served
/// "0 references" forever while the caller's editor showed the call. And the versions are text
/// versions rather than Roslyn's semantic ones, because a call site is a method-body edit and the
/// semantic stamps track top-level declarations only — keyed semantically, the very edit that
/// changes the count is the one that would not move the key. What this costs is recomputation on
/// body edits that changed no count; what it cannot do is hold a stale one.
/// </para>
/// </remarks>
internal static class DocumentSemanticGeneration
{
    private sealed record Generation(VersionStamp Text, VersionStamp Semantics);

    /// <summary>The generation for a resolved document.</summary>
    public static async Task<object> ForAsync(Document document, CancellationToken ct)
    {
        var project = document.Project;
        var solution = project.Solution;

        // Text of this project and everything it references, then everything that references it.
        // Dependents' dependent-versions reach their own references too, which double-counts this
        // project — a superset of the right inputs, never a subset.
        var version = await project.GetDependentVersionAsync(ct);
        foreach (var id in solution.GetProjectDependencyGraph()
                     .GetProjectsThatTransitivelyDependOnThisProject(project.Id))
        {
            if (solution.GetProject(id) is { } dependent)
                version = version.GetNewerVersion(await dependent.GetDependentVersionAsync(ct));
        }

        return new Generation(await document.GetTextVersionAsync(ct), version);
    }

    /// <summary>
    /// The generation for a document URI, or <see langword="null"/> when the URI does not resolve
    /// to a loaded document — a caller that cannot describe its inputs must not memoize against
    /// them, and passes straight through instead.
    /// </summary>
    public static async Task<object?> ForAsync(string uri, CancellationToken ct)
    {
        var document = await LspDocumentResolver.ResolveAsync(LspConverters.UriToPath(uri), ct);
        return document is null ? null : await ForAsync(document, ct);
    }
}
