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
    /// Converts locations, first learning the URIs of the generated documents any of them turn out
    /// to live in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Enumerating generated documents runs the generators, so it is done only when a result
    /// actually landed in generated code — which is also the only time the answer differs.
    /// </para>
    /// <para>
    /// The project asked is the one that owns the tree and not <paramref name="project"/>, because
    /// the two differ exactly when the request followed a project reference: a mediator's Send
    /// resolves to an extension method generated in the project that declares the request, and
    /// enumerating the caller's generated documents finds nothing that maps it. The result was a
    /// link under the callee's <c>obj\</c> that opens nothing.
    /// </para>
    /// </remarks>
    public static async Task<LspLocation[]> ToLocationsAsync(
        IEnumerable<Microsoft.CodeAnalysis.Location> locations, Project project, CancellationToken ct)
    {
        var all = locations.ToList();
        var unwarmed = new HashSet<ProjectId>();
        var considered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var location in all)
        {
            if (location.SourceTree is not { } tree
                || tree.FilePath is not { Length: > 0 } path
                || !considered.Add(path)
                || GeneratedDocumentRegistry.TryGetUri(path, out _)
                || !GeneratedDocumentRegistry.LooksGenerated(path))
            {
                continue;
            }

            unwarmed.Add(project.Solution.GetDocumentId(tree)?.ProjectId ?? project.Id);
        }

        foreach (var id in unwarmed)
        {
            if (project.Solution.GetProject(id) is not { } owner
                || owner.FilePath is not { Length: > 0 } projectPath)
            {
                continue;
            }

            try
            {
                GeneratedDocumentRegistry.Register(
                    projectPath, await owner.GetSourceGeneratedDocumentsAsync(ct));
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
