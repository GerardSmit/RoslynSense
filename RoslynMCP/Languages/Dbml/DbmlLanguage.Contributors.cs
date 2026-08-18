using Microsoft.CodeAnalysis;
using RoslynMCP.Languages.Dbml.Core;
using RoslynMCP.Lsp;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Languages.Dbml;

internal sealed partial class DbmlLanguage :
    ILanguageDefinitionContributor,
    ILanguageReferenceContributor
{
    /// <summary>
    /// The <c>.dbml</c> element a generated designer member was written from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The designer is not where a table is declared — it is a transcription of the model that
    /// SqlMetal, or Visual Studio's custom tool, rewrites wholesale. F12 on <c>Product.Name</c>
    /// landing on <c>[Column(Storage="_Name", …)] public string Name</c> answers the question with a
    /// restatement of it: the column, its database type, its nullability and its key membership —
    /// everything the reader came for — are in the <c>.dbml</c>.
    /// </para>
    /// <para>
    /// The model is derived from the declaring file's path rather than searched for, the same way the
    /// WebForms pack derives a markup file, and for the same reason: that path <em>is</em> the
    /// relationship. The difference is that LINQ to SQL replaces the extension instead of appending
    /// to it, so the derivation going backwards is ambiguous with every other custom tool that writes
    /// a <c>.designer.cs</c>. Nothing is contributed on the strength of the path alone — the index
    /// has to have bound this very symbol to an element, which is a match on SqlMetal's own
    /// attributes. A wrong answer here would take the right one with it, because contributing is what
    /// arms the withdrawal below.
    /// </para>
    /// </remarks>
    public Task<IReadOnlyList<LspLocation>> DefinitionsAsync(
        ISymbol symbol, Project project, CancellationToken ct) => ModelLocationsAsync(symbol, ct);

    /// <summary>
    /// The model elements a symbol was generated from, as locations in the file the editor is
    /// showing.
    /// </summary>
    /// <remarks>
    /// One lookup behind both contributors, so the line F12 opens and the line find-references lists
    /// cannot drift apart.
    /// </remarks>
    private static async Task<IReadOnlyList<LspLocation>> ModelLocationsAsync(
        ISymbol symbol, CancellationToken ct)
    {
        if (symbol.Kind is not (SymbolKind.NamedType or SymbolKind.Property or SymbolKind.Method))
            return [];

        var locations = new List<LspLocation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            ct.ThrowIfCancellationRequested();

            if (reference.SyntaxTree.FilePath is not { Length: > 0 } declaringPath
                || DbmlSourceMappingService.ModelPathFor(declaringPath) is not { } modelPath
                || !seen.Add(modelPath))
            {
                continue;
            }

            if (await DbmlWorkspace.GetAsync(modelPath, ct) is not { } view)
                continue;

            // Bound, not matched by name. The index answers only for a symbol it built from this
            // model's own declarations, so a `Settings.Designer.cs` sitting beside a `Settings.dbml`
            // that has nothing to do with it contributes nothing and supersedes nothing.
            if (view.Index.DeclarationFor(symbol) is not { } declaration)
                continue;

            // The span comes from the parse the view holds — the editor's buffer where the file is
            // open — rather than from the index, which read whatever the compilation was built from.
            // Keying the index on the declaration's name is what makes that work: an edit that moved
            // every element in the file still resolves, because the name did not move.
            if (view.Database.Find(declaration.Key) is not { } element)
                continue;

            locations.Add(new LspLocation(
                LspConverters.PathToUri(view.FilePath),
                LspConverters.ToRange(view.Text.Lines, element.SelectionSpan)));
        }

        return locations;
    }

    /// <summary>
    /// Withdraws the designer declaration, so F12 is a jump to the model rather than a two-entry
    /// picker whose second entry is a generated file.
    /// </summary>
    /// <remarks>
    /// Only a designer some binding proved to be SqlMetal's — see
    /// <see cref="DbmlSourceMappingService.IsBoundDesignerPath"/>. Superseding is asked only of a
    /// contributor that answered, so a symbol the index does not recognise keeps whatever Roslyn
    /// found; and because the record is written by the binder rather than by the path rule, a
    /// designer generated from a <c>.resx</c> or a <c>.settings</c> is never in it.
    /// </remarks>
    public bool Supersedes(LspLocation location) =>
        DbmlSourceMappingService.IsBoundDesignerPath(LspConverters.UriToPath(location.Uri));

    /// <summary>
    /// The model line a generated member was declared by, reported in place of the designer's
    /// mentions of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>.dbml</c> names no C# symbol, so this adds no <em>call site</em> the way the markup packs
    /// do. What it adds is the declaration, and that is what makes the withdrawal below legal:
    /// superseding is only ever asked of a contributor that put something into this particular
    /// answer, so a pack returning nothing here could not remove anything either — and Shift+F12
    /// would go on listing the designer while F12 hid it, the two features disagreeing about the same
    /// pair of files.
    /// </para>
    /// <para>
    /// What is removed is worth naming, because it is most of the answer. One
    /// <c>&lt;Column Name="Name" /&gt;</c> becomes the property, its backing field, the
    /// <c>OnNameChanging</c>/<c>OnNameChanged</c> partial calls and the setter's assignment — five or
    /// six hits in a file nobody wrote, nobody may edit and the next regeneration overwrites, with
    /// the real call sites buried among them.
    /// </para>
    /// </remarks>
    public Task<IReadOnlyList<LspLocation>> ReferencesAsync(
        ISymbol symbol, Project project, CancellationToken ct, bool waitForCompleteScope = false) =>
        ModelLocationsAsync(symbol, ct);
}
