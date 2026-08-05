using Microsoft.CodeAnalysis;
using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Languages.WebForms.Lsp;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using WebFormsCore;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Languages.WebForms;

internal sealed partial class WebFormsLanguage :
    ILanguageDefinitionContributor,
    ILanguageReferenceContributor,
    ILanguageRenameContributor
{
    /// <summary>
    /// The <c>ID</c> attribute a code-behind control field was generated from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The designer file is not where a control is declared — it is a transcription of the markup
    /// that <c>regenerate_designer</c>, or Visual Studio, rewrites wholesale. F12 on
    /// <c>btnSave</c> landing on <c>protected global::System.Web.UI.WebControls.Button btnSave;</c>
    /// answers the question with a restatement of it: the tag, its properties and its handlers —
    /// everything the reader came for — are in the <c>.ascx</c>.
    /// </para>
    /// <para>
    /// The markup file is derived from the declaring file's path rather than searched for, because
    /// that path <em>is</em> the relationship: ASP.NET names a designer after the page it belongs
    /// to, and the tooling on both ends of this repository already does the same derivation in
    /// reverse (see <c>AspxDesignerGenerator.GetDesignerPath</c>). A hand-written field in a
    /// <c>.ascx.cs</c> resolves the same way, which is what keeps the answer the same for a page
    /// whose designer was deleted.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<LspLocation>> DefinitionsAsync(
        ISymbol symbol, Project project, CancellationToken ct)
    {
        if (symbol.Kind is not (SymbolKind.Field or SymbolKind.Property))
            return [];

        var locations = new List<LspLocation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            ct.ThrowIfCancellationRequested();

            if (reference.SyntaxTree.FilePath is not { Length: > 0 } declaringPath
                || AspxSourceMappingService.MarkupPathFor(declaringPath) is not { } markupPath
                || !seen.Add(markupPath))
            {
                continue;
            }

            // Bound, not matched by name. A path derivation says which page a file belongs to; it
            // does not say that this member is that page's control. A second class in the same
            // file, or a member that merely shares a name with an ID, would otherwise gain a
            // definition in markup it has nothing to do with — and because contributing is what
            // arms the withdrawal below, a wrong answer would take the right one with it.
            var document = await AspxDocumentService.GetAsync(markupPath, ct);
            if (document?.CodeBehind is not { } codeBehind
                || !SymbolEqualityComparer.Default.Equals(
                    codeBehind.GetMemberDeep(symbol.Name)?.Symbol?.OriginalDefinition,
                    symbol.OriginalDefinition))
            {
                continue;
            }

            var index = await WebFormsIndex.GetAsync(markupPath, ct);
            if (index is null)
                continue;

            foreach (var control in index.Controls)
            {
                if (string.Equals(control.Id, symbol.Name, StringComparison.Ordinal))
                    locations.Add(new LspLocation(
                        LspConverters.PathToUri(markupPath), LspConverters.ToRange(control.Span)));
            }
        }

        return locations;
    }

    /// <summary>
    /// Withdraws the designer declaration, so F12 is a jump to the markup rather than a two-entry
    /// picker whose second entry is a generated file.
    /// </summary>
    /// <remarks>
    /// Only the designer. A field the user wrote by hand in a <c>.ascx.cs</c> is a real declaration
    /// sitting in a file they maintain, and hiding it would be this pack deciding that markup is
    /// the more interesting half of a page — which is not true of a control the code-behind
    /// configures. Superseding is asked only of a contributor that answered, so a page with no
    /// matching <c>ID</c> keeps whatever Roslyn found.
    /// </remarks>
    public bool Supersedes(LspLocation location) =>
        AspxSourceMappingService.IsDesignerPath(LspConverters.UriToPath(location.Uri));

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
