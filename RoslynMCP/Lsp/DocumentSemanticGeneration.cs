using Microsoft.CodeAnalysis;

namespace RoslynMCP.Lsp;

/// <summary>
/// What a semantic answer about one C# document depends on: the document's own text, and the
/// semantics of its project and everything that project references.
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
/// The staleness this admits is the one <see cref="Handlers.CodeLensHandler"/> already documents
/// and accepts: an edit in a project that depends on this one can leave a count stale until this
/// key next moves. Every IDE's lens makes that trade, because the alternative is re-running a
/// workspace search on every keystroke in every open file.
/// </para>
/// </remarks>
internal static class DocumentSemanticGeneration
{
    private sealed record Generation(VersionStamp Text, VersionStamp Semantics);

    /// <summary>The generation for a resolved document.</summary>
    public static async Task<object> ForAsync(Document document, CancellationToken ct) =>
        new Generation(
            await document.GetTextVersionAsync(ct),
            await document.Project.GetDependentSemanticVersionAsync(ct));

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
