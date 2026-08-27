using Microsoft.CodeAnalysis;

namespace RoslynMCP.Services.ExternalSource;

/// <summary>
/// The session solution's own symbol for one that a decompiled or downloaded file declares.
/// </summary>
/// <remarks>
/// <para>
/// A file under the external cache is opened in an ad-hoc project of its own — that one document
/// and the assembly it came from — because that is what gives it a semantic model at all. Every
/// answer from inside it is therefore an answer about a solution holding one file, which is right
/// for hover and F12, since both stay inside the type, and wrong for the one question whose answer
/// is somewhere else by definition: who uses this. Find references in decompiled source searched
/// the decompiled file against itself, so a framework method every project calls came back with
/// the uses in its own body and nothing more.
/// </para>
/// <para>
/// The bridge is the documentation comment id, which names a member by its signature rather than
/// by the compilation it came from. A decompiler writes the signature back out, so the id computed
/// from the decompiled declaration is the id of the metadata member it was decompiled from — and
/// resolving it against a project in the user's solution gives the symbol its own code refers to.
/// A caret on something the id cannot name — a local, a parameter, a private member the solution
/// cannot see — resolves to nothing, and the caller then answers from the file as before, which is
/// where those references are anyway.
/// </para>
/// </remarks>
internal static class ExternalSymbolBridge
{
    /// <summary>
    /// <paramref name="symbol"/> as <paramref name="session"/> sees it, with a project to search
    /// from — or null when the document is the user's own, or the solution has no such symbol.
    /// </summary>
    public static async Task<(ISymbol Symbol, Project Project)?> TryMapAsync(
        ISymbol symbol, Document document, Solution? session, CancellationToken ct)
    {
        if (session is null
            || document.FilePath is not { Length: > 0 } path
            || !ExternalSourceCache.IsExternalSourcePath(path))
        {
            return null;
        }

        if (symbol.OriginalDefinition.GetDocumentationCommentId() is not { Length: > 0 } id)
            return null;

        // Warm projects first. The id resolves in any project that references the assembly, so
        // which one answers only decides where the search is anchored — and after the warm-up
        // sweep most of the solution is compiled already, which makes the common case free.
        var projects = session.Projects.ToList();

        foreach (var project in projects)
        {
            if (project.TryGetCompilation(out var compilation)
                && Resolve(id, compilation) is { } warm)
            {
                return (warm, project);
            }
        }

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

            if (built is not null && Resolve(id, built) is { } resolved)
                return (resolved, project);
        }

        return null;
    }

    private static ISymbol? Resolve(string id, Compilation compilation) =>
        DocumentationCommentId.GetFirstSymbolForDeclarationId(id, compilation);
}
