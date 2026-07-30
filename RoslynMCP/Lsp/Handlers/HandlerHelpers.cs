using Microsoft.CodeAnalysis;
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
}
