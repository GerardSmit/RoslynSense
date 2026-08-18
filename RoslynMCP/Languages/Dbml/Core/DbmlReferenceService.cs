using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.Dbml.Core;

/// <summary>One place a model declaration is used, or one place it is declared.</summary>
/// <param name="Document">The C# document the span is in, or <c>null</c> for a row standing in for
/// the <c>.dbml</c> line itself.</param>
internal readonly record struct DbmlUsage(
    Document? Document, string FilePath, SourceText? Text, TextSpan Span, bool IsDefinition)
{
    public static DbmlUsage In(Document document, TextSpan span, bool isDefinition) =>
        new(document, document.FilePath ?? string.Empty, Text: null, span, isDefinition);

    /// <summary>The model line a generated declaration came from, reported in its place.</summary>
    public static DbmlUsage Declaring(string filePath, SourceText text, TextSpan span) =>
        new(Document: null, filePath, text, span, IsDefinition: true);
}

/// <summary>
/// Navigation from a caret in a <c>.dbml</c> into the C# SqlMetal built from it.
/// </summary>
/// <remarks>
/// <para>
/// There is no projection in this pack, so every feature here is plain Roslyn once a model
/// declaration has been turned into a symbol. Unlike the protobuf pack, one declaration is exactly
/// one symbol — a column is a property, an rpc is not — so there is no symbol set to assemble and
/// the whole of the work is the two filters below.
/// </para>
/// <para>
/// The search runs against the widened solution rather than the declaring project. A
/// <c>DataContext</c> lives in a data-access project and is queried from everywhere else, so a
/// project-scoped search would find the designer and nothing more — which is precisely the answer
/// the designer is excluded for producing.
/// </para>
/// <para>
/// A reference inside the designer is not a reference. One <c>&lt;Column Name="Name" /&gt;</c>
/// becomes the property, its backing field, the <c>OnNameChanging</c>/<c>OnNameChanged</c> partials
/// and the assignment in the setter — five or six mentions of code nobody wrote, nobody may edit and
/// the next regeneration overwrites. Without the filter a lens over a column reads six before anyone
/// has used it once. A <em>declaration</em> in the designer is reported as the model line instead, so
/// find-usages and F12 open the same place.
/// </para>
/// </remarks>
internal static class DbmlReferenceService
{
    /// <inheritdoc cref="Proto.Core.ProtoReferenceService.ExplicitSearchBudget"/>
    public static readonly TimeSpan ExplicitSearchBudget = SearchScopeService.ExplicitSearchBudget;

    /// <summary>
    /// Every use of the caret's declaration across the solution, with the designer kept out.
    /// </summary>
    /// <param name="budget">
    /// How long this caller may wait for the context's consumers to be loaded. Left at nothing by the
    /// incidental callers — a code lens resolving as the view scrolls, a hover — and set only by a
    /// gesture the user made on purpose.
    /// </param>
    public static async Task<ImmutableArray<DbmlUsage>> FindUsagesAsync(
        DbmlHit hit, DbmlView view, CancellationToken ct, TimeSpan? budget = null)
    {
        if (hit.Symbol is not { } symbol || view.Project is not { } project)
            return [];

        var solution = await SearchScopeService.WidenAsync(project, budget, ct);

        // The symbol came off an index keyed on the project's dependent semantic version, which
        // survives a method-body edit while the compilation underneath it does not. Roslyn resolves a
        // symbol's originating project by compilation identity and answers nothing — silently — for a
        // foreign snapshot's symbol, so the search re-anchors before it starts.
        symbol = await AnchorAsync(symbol, view.Index, solution, project, ct);

        var results = ImmutableArray.CreateBuilder<DbmlUsage>();
        var seen = new HashSet<(DocumentId, TextSpan)>();
        bool declaredReported = false;

        foreach (var referenced in await SymbolFinder.FindReferencesAsync(symbol, solution, ct))
        {
            ct.ThrowIfCancellationRequested();

            foreach (var location in referenced.Locations)
            {
                if (!location.Location.IsInSource || view.Index.IsGenerated(location.Document))
                    continue;

                if (seen.Add((location.Document.Id, location.Location.SourceSpan)))
                    results.Add(DbmlUsage.In(location.Document, location.Location.SourceSpan, isDefinition: false));
            }

            foreach (var location in referenced.Definition.Locations)
            {
                if (!location.IsInSource
                    || location.SourceTree is null
                    || solution.GetDocument(location.SourceTree) is not { } document)
                {
                    continue;
                }

                if (!view.Index.IsGenerated(document))
                {
                    if (seen.Add((document.Id, location.SourceSpan)))
                        results.Add(DbmlUsage.In(document, location.SourceSpan, isDefinition: true));

                    continue;
                }

                // Once, however many generated members stand for the declaration.
                if (!declaredReported)
                {
                    declaredReported = true;
                    results.Add(DbmlUsage.Declaring(
                        view.FilePath, view.Text, hit.Declaration.SelectionSpan));
                }
            }
        }

        return results.ToImmutable();
    }

    /// <summary>
    /// The symbol as the compilation about to be searched holds it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The compilation is fetched rather than the semantic version compared, because a body edit
    /// forks the compilation without moving the version — which is exactly the case the index
    /// survives and a search does not. It costs nothing: the caller is on its way into a
    /// <c>SymbolFinder</c> sweep, which needs the compilation regardless.
    /// </para>
    /// <para>
    /// Re-resolution goes through <c>SymbolKey</c>, which names a symbol by its declaration rather
    /// than by its instance and so reads the same from either snapshot. The original is returned when
    /// the re-resolve finds nothing, which is no worse than not having tried: a declaration that has
    /// genuinely gone is one no search would have found either way.
    /// </para>
    /// </remarks>
    private static async Task<ISymbol> AnchorAsync(
        ISymbol symbol, DbmlGeneratedIndex index, Solution solution, Project project,
        CancellationToken ct)
    {
        if (index.Compilation is null)
            return symbol;

        // The project as the searched solution holds it, not as the caller holds it: the two
        // snapshots differ whenever widening the scope loaded a project.
        var searched = solution.GetProject(project.Id) ?? project;

        if (await searched.GetCompilationAsync(ct) is not { } compilation
            || ReferenceEquals(compilation, index.Compilation))
        {
            return symbol;
        }

        return SymbolFinder.FindSimilarSymbols(symbol.OriginalDefinition, compilation, ct).FirstOrDefault()
               ?? symbol;
    }
}
