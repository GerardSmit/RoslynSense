using Microsoft.CodeAnalysis;

namespace RoslynMCP.Services.ExternalSource;

/// <summary>
/// One project of the session's solution, opened so that symbols a decompiled or downloaded file
/// declares can be asked about as that solution sees them.
/// </summary>
internal sealed class ExternalSymbolScope(Project project, Compilation compilation)
{
    /// <summary>Where a search started from this scope is anchored.</summary>
    public Project Project { get; } = project;

    /// <summary>The solution a search started from this scope runs against.</summary>
    public Solution Solution => Project.Solution;

    /// <summary>
    /// <paramref name="symbol"/> as the solution sees it, or null when it has nothing that
    /// answers to the same name — a local, a private member, a type the solution never
    /// references.
    /// </summary>
    public ISymbol? Map(ISymbol? symbol) =>
        symbol?.OriginalDefinition.GetDocumentationCommentId() is { Length: > 0 } id
            ? DocumentationCommentId.GetFirstSymbolForDeclarationId(id, compilation)
            : null;
}

/// <summary>
/// The session solution's own symbols for the ones a decompiled or downloaded file declares.
/// </summary>
/// <remarks>
/// <para>
/// A file under the external cache is opened in an ad-hoc project of its own — that one document
/// and the assembly it came from — because that is what gives it a semantic model at all. Every
/// answer from inside it is therefore an answer about a solution holding one file, which is right
/// for hover and for reading, and wrong for every question whose answer is somewhere else by
/// definition: who calls this, what implements it, what derives from it. Each of those searched
/// the decompiled file against itself and reported what it found there, which was nothing.
/// </para>
/// <para>
/// The bridge is the documentation comment id, which names a member by its signature rather than
/// by the compilation it came from. A decompiler writes the signature back out, so the id computed
/// from the decompiled declaration is the id of the metadata member it was decompiled from — and
/// resolving it against a project in the user's solution gives the symbol their own code refers
/// to. Anything the id cannot name resolves to nothing, and the caller then answers from the file
/// as before, which is where those answers are anyway.
/// </para>
/// <para>
/// The project is found once per document rather than once per symbol, because a file's members
/// all live in the same assembly: whichever project references it can answer for all of them. The
/// type is what is looked for, not the member the caret is on, so that a private member does not
/// send the search back to the one-file project when its type would have found the right one.
/// </para>
/// </remarks>
internal static class ExternalSymbolBridge
{
    /// <summary>
    /// A scope for <paramref name="document"/>, or null when it is the user's own file or when
    /// the solution has nothing the file declares.
    /// </summary>
    /// <param name="anchor">
    /// A symbol declared by the document. Its containing type is what the search looks for.
    /// </param>
    /// <param name="warmProjectsOnly">
    /// Whether to consider only projects that are compiled already. Passed by the callers that
    /// run without anybody having asked — a code lens resolving as the view scrolls, a gutter
    /// marker — for which compiling the solution to answer is far too much.
    /// </param>
    public static async Task<ExternalSymbolScope?> TryOpenAsync(
        Document document, ISymbol? anchor, Solution? session, CancellationToken ct,
        bool warmProjectsOnly = false)
    {
        if (session is null
            || anchor is null
            || document.FilePath is not { Length: > 0 } path
            || !ExternalSourceCache.IsExternalSourcePath(path))
        {
            return null;
        }

        if (Outermost(anchor).OriginalDefinition.GetDocumentationCommentId() is not { Length: > 0 } id)
            return null;

        var projects = session.Projects.ToList();

        // Warm projects first. The id resolves in any project that references the assembly, so
        // which one answers only decides where the search is anchored — and after the warm-up
        // sweep most of the solution is compiled already, which makes the common case free.
        foreach (var project in projects)
        {
            if (project.TryGetCompilation(out var compilation)
                && DocumentationCommentId.GetFirstSymbolForDeclarationId(id, compilation) is not null)
            {
                return new ExternalSymbolScope(project, compilation);
            }
        }

        if (warmProjectsOnly)
            return null;

        foreach (var project in projects)
        {
            ct.ThrowIfCancellationRequested();

            if (project.TryGetCompilation(out _))
                continue;

            // A project that cannot produce a compilation — no language service, a broken
            // reference — is one this symbol is not in either, so it is skipped rather than
            // allowed to end the search.
            Compilation? built;
            try
            {
                built = await project.GetCompilationAsync(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                ServiceLog.Warn(
                    $"Could not compile '{project.Name}' while looking for '{id}': {ex.Message}",
                    key: $"external-symbol-bridge:{project.Id}");
                continue;
            }

            if (built is not null
                && DocumentationCommentId.GetFirstSymbolForDeclarationId(id, built) is not null)
            {
                return new ExternalSymbolScope(project, built);
            }
        }

        return null;
    }

    /// <summary>
    /// <paramref name="symbol"/> as the session solution sees it, with a project to search from —
    /// or null when the document is the user's own, or the solution has no such symbol.
    /// </summary>
    public static async Task<(ISymbol Symbol, Project Project)?> TryMapAsync(
        ISymbol? symbol, Document document, Solution? session, CancellationToken ct,
        bool warmProjectsOnly = false)
    {
        var scope = await TryOpenAsync(document, symbol, session, ct, warmProjectsOnly);

        return scope?.Map(symbol) is { } mapped ? (mapped, scope.Project) : null;
    }

    /// <summary>The type a member belongs to, however deeply nested, or the symbol itself.</summary>
    private static ISymbol Outermost(ISymbol symbol)
    {
        var type = symbol as INamedTypeSymbol ?? symbol.ContainingType;
        if (type is null)
            return symbol;

        while (type.ContainingType is { } outer)
            type = outer;

        return type;
    }
}
