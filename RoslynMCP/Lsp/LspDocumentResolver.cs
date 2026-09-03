using Microsoft.CodeAnalysis;
using RoslynMCP.Services;

namespace RoslynMCP.Lsp;

/// <summary>Resolves an LSP document URI to a Roslyn <see cref="Document"/> via the shared
/// <see cref="WorkspaceService"/> — the same path MCP tools take, so both see identical
/// snapshots (including open-buffer overlays).</summary>
/// <remarks>
/// Source-generated documents resolve here too. They have no file, so
/// <see cref="LspConverters.UriToPath"/> hands their URI through untouched and this recognises
/// it — without that, every language feature in a generated file returned nothing, and the file
/// opened as an inert buffer with no hover, no navigation and no diagnostics.
/// </remarks>
internal static class LspDocumentResolver
{
    public static async Task<Document?> ResolveAsync(string filePath, CancellationToken ct)
    {
        if (filePath.StartsWith(Handlers.VirtualDocumentHandler.GeneratedScheme + ":", StringComparison.Ordinal))
            return await ResolveGeneratedAsync(filePath, ct);

        // A request that races the keystroke's buffer reconcile would fork an overlay off the
        // stale base and build semantics the reconcile immediately orphans. Briefly meeting it
        // here lets completion → signature help → tokens for one version share one snapshot.
        await WorkspaceService.AwaitPendingReconcileAsync(filePath, ct);

        // One call, where this used to make two: find the owning project, then open that same
        // project again to take the document out of it. Every language feature starts here — hover,
        // completion, signature help, semantic tokens, folding, inlay hints, code lens, formatting,
        // rename, every navigation — so the duplicate was paid several times per keystroke.
        return await WorkspaceService.FindDocumentAsync(filePath, ct);
    }

    private static async Task<Document?> ResolveGeneratedAsync(string uri, CancellationToken ct)
    {
        if (!Handlers.VirtualDocumentHandler.TryParseUri(uri, out _, out string projectPath, out string hintName))
            return null;

        try
        {
            var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
                projectPath, cancellationToken: ct);
            var documents = await project.GetSourceGeneratedDocumentsAsync(ct);

            // Enumerating them is the only way to find one, so record the whole set while it is
            // in hand: that is what lets locations inside generated code convert back to a URI.
            GeneratedDocumentRegistry.Register(projectPath, documents);

            return documents.FirstOrDefault(d =>
                GeneratedDocumentRegistry.HintName(d).Equals(hintName, StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ServiceLog.Warn($"Could not open generated document '{hintName}': {ex.Message}",
                key: $"generated-open:{uri}");
            return null;
        }
    }
}
