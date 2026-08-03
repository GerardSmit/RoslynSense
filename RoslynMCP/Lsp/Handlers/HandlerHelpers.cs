using Microsoft.CodeAnalysis;
using RoslynMCP.Services;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp.Protocol;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Lsp.Handlers;

internal static class HandlerHelpers
{
    public static async Task<(Document Document, SourceText Text, int Offset)?> ResolveAsync(
        TextDocumentIdentifier textDocument, Position position, CancellationToken ct)
    {
        var document = await LspDocumentResolver.ResolveAsync(
            LspConverters.UriToPath(textDocument.Uri), ct);
        if (document is null)
            return null;

        var text = await document.GetTextAsync(ct);
        int offset = LspConverters.ToOffset(text, position);
        return (document, text, offset);
    }

    public static LspLocation[] ToLocations(IEnumerable<Microsoft.CodeAnalysis.Location> locations) =>
        locations.Select(LspConverters.ToLocation).Where(l => l is not null).Select(l => l!)
            .Distinct().ToArray();

    /// <summary>
    /// Converts locations, first learning the URIs of <paramref name="project"/>'s generated
    /// documents if any of them turn out to live in one.
    /// </summary>
    /// <remarks>
    /// Enumerating generated documents runs the generators, so it is done only when a result
    /// actually landed in generated code — which is also the only time the answer differs.
    /// </remarks>
    public static async Task<LspLocation[]> ToLocationsAsync(
        IEnumerable<Microsoft.CodeAnalysis.Location> locations, Project project, CancellationToken ct)
    {
        var all = locations.ToList();

        bool needsWarming = all.Any(l =>
            l.SourceTree?.FilePath is { Length: > 0 } path
            && GeneratedDocumentRegistry.LooksGenerated(path)
            && !GeneratedDocumentRegistry.TryGetUri(path, out _));

        if (needsWarming && project.FilePath is { Length: > 0 } projectPath)
        {
            try
            {
                GeneratedDocumentRegistry.Register(
                    projectPath, await project.GetSourceGeneratedDocumentsAsync(ct));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                ServiceLog.Warn(
                    $"Could not resolve generated documents of '{Path.GetFileName(projectPath)}': {ex.Message}",
                    key: $"generated-warm:{projectPath}");
            }
        }

        return ToLocations(all);
    }
}
