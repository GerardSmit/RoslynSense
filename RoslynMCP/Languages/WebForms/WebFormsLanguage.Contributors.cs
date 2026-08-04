using Microsoft.CodeAnalysis;
using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Languages.WebForms.Lsp;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Languages.WebForms;

internal sealed partial class WebFormsLanguage :
    ILanguageReferenceContributor,
    ILanguageRenameContributor
{
    public async Task<IReadOnlyList<LspLocation>> ReferencesAsync(
        ISymbol symbol, Project project, CancellationToken ct, bool waitForCompleteScope = false)
    {
        var results = new List<LspLocation>();

        foreach (var reference in await AspxReferenceService.FindAsync(symbol, project, ct))
        {
            int length = reference.Text.Length;
            int start = Math.Clamp(reference.Span.Start, 0, length);
            int end = Math.Clamp(reference.Span.End, start, length);

            results.Add(new LspLocation(
                LspConverters.PathToUri(reference.FilePath),
                LspConverters.ToRange(
                    reference.Text.Lines,
                    Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(start, end))));
        }

        return results;
    }

    public Task<IReadOnlyList<(string Uri, TextEdit Edit)>> RenameEditsAsync(
        ISymbol symbol, Project project, string newName, CancellationToken ct) =>
        AspxLanguageHandler.RenameEditsAsync(symbol, project, newName, ct);
}
