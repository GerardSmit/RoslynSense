using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using RoslynMCP.Languages;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Services.ExternalSource;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>prepareRename / rename. Returns a <see cref="WorkspaceEdit"/> — the editor applies
/// it to its buffers; the server NEVER writes renamed files to disk (the user may have unsaved
/// edits, and undo must stay in the editor).</summary>
internal static class RenameHandler
{
    private static readonly ConditionalWeakTable<Workspace, SingleFlight> s_hierarchyLoads = new();

    public static async Task<PrepareRenameResult?> PrepareRenameAsync(
        TextDocumentPositionParams p, CancellationToken ct, LanguageSession? languages = null)
    {
        if (await HandlerHelpers.ResolveAsync(p.TextDocument, p.Position, ct) is not
            var (document, text, offset) || document is null)
            return null;

        string path = LspConverters.UriToPath(p.TextDocument.Uri);

        // Nothing under the external cache can be renamed. The file is a decompilation or a
        // download, written read-only, and the declaration in it is a copy of one that lives in an
        // assembly — but Roslyn sees an ordinary source symbol there and would produce edits
        // against a file the editor cannot write, for a name nothing else in the solution reads.
        if (ExternalSourceCache.IsExternalSourcePath(path))
            return null;

        // Before the symbol lookup, never after: a caret inside a string literal binds to nothing,
        // so by the time a contributor would be reached this method has already returned null. What
        // these providers rename is not an ISymbol at all — a resource key has no declaration
        // Roslyn can bind to, and the pack that owns it performs the whole rename rather than
        // adding edits to one Roslyn is already performing.
        foreach (var provider in
                 LanguageScope.Of(languages).Contributors<ISymbolFreeRenameProvider>())
        {
            if (await provider.PrepareAsync(path, offset, ct) is { } prepared)
                return prepared;
        }

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, offset, ct);
        if (symbol is null || symbol.Locations.All(l => !l.IsInSource))
            return null; // metadata symbols can't be renamed

        // The name, not whatever token the caret's offset happens to land in: with the caret at
        // the end of a name the offset belongs to the token after it, and answering with that one
        // opens the rename box over a paren, prefilled with it.
        var root = await document.GetSyntaxRootAsync(ct);
        if (root is null || CaretTokens.Touching(root, offset, IsNameToken) is not { } t)
            return null;

        return new PrepareRenameResult(LspConverters.ToRange(text.Lines, t.Span), t.ValueText);
    }

    /// <summary>
    /// What a rename can be anchored to. Contextual keywords bind as identifiers, so this is a
    /// kind check rather than a list of words.
    /// </summary>
    private static bool IsNameToken(SyntaxToken token) =>
        token.IsKind(SyntaxKind.IdentifierToken);

    public static async Task<WorkspaceEdit?> RenameAsync(
        RenameParams p, CancellationToken ct, LanguageSession? languages = null)
    {
        var timing = RunwayTrace.Begin("rename");
        if (await HandlerHelpers.ResolveAsync(p.TextDocument, p.Position, ct) is not
            var (document, originalText, offset) || document is null)
            return null;

        string filePath = LspConverters.UriToPath(p.TextDocument.Uri);

        // The same refusal prepareRename makes, for the clients that do not ask it first.
        if (ExternalSourceCache.IsExternalSourcePath(filePath))
            return null;

        // Ahead of the symbol lookup for the same reason prepareRename is.
        foreach (var provider in
                 LanguageScope.Of(languages).Contributors<ISymbolFreeRenameProvider>())
        {
            if (await provider.RenameAsync(filePath, offset, p.NewName, document.Project, ct) is { } edit)
                return edit;
        }

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, offset, ct);
        if (symbol is null || symbol.Locations.All(l => !l.IsInSource))
            return null;
        var originalSymbolKey = SymbolKey.Create(symbol, ct);
        timing?.Mark("resolve symbol");

        // A library's lazy dependency closure omits consumers. Member renames can also cascade
        // through an interface or base member to independent implementations, which need not
        // reference the original assembly at all. Load the owning solution for those renames.
        // Lexically scoped names cannot have consumers in other projects. Parameters are
        // deliberately excluded: a public method can have named-argument callers elsewhere.
        bool localName = symbol.Kind is SymbolKind.Local or SymbolKind.Label
            or SymbolKind.RangeVariable or SymbolKind.TypeParameter
            || symbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction };
        IReadOnlyList<string> hierarchyProjects = [];
        if (!localName)
        {
            if (symbol is IMethodSymbol or IPropertySymbol or IEventSymbol or IParameterSymbol)
                hierarchyProjects = await LoadRenameHierarchyAsync(document.Project, ct, symbol);
            // Loose projects may have no validated solution list. Retain the normal consumer
            // search in that case, as well as for names that do not cascade through a hierarchy.
            if (hierarchyProjects.Count == 0)
                await SearchScopeService.WidenForSymbolAsync(
                    symbol, document.Project, SearchScopeService.ExplicitSearchBudget, ct);
        }
        timing?.Mark("load search scope");

        // Loading changes the solution snapshot and may reconcile open buffers along the way.
        // Bind the symbol again in that final snapshot so both the rename and its edit ranges use
        // the same current source, including unsaved text in newly loaded consumers.
        if (await HandlerHelpers.ResolveAsync(p.TextDocument, p.Position, ct) is not
            var (currentDocument, currentText, currentOffset) || currentDocument is null)
            return null;
        // The request position belongs to the original source. If typing changed that source
        // while consumer loading awaited, the same position may now name a different property.
        // Consumer-only edits are safe: their latest text still participates in the rename.
        if (!originalText.ContentEquals(currentText))
            return null;
        document = currentDocument;
        // The batch loader can skip projects that fail evaluation. Do not return a partial rename
        // whose missing implementation or caller would be left with the old contract name.
        if (hierarchyProjects.Count != 0)
        {
            var loaded = LoadedProjectPaths(document.Project.Solution);
            if (hierarchyProjects.Any(path => !loaded.Contains(path)))
                return null;
        }
        symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, currentOffset, ct);
        if (symbol is null || symbol.Locations.All(l => !l.IsInSource))
            return null;
        // The caller text can stay identical while a declaration elsewhere changes its binding,
        // for example removing Derived.Name exposes Base.Name at the same value.Name expression.
        if (!SymbolKey.GetComparer().Equals(originalSymbolKey, SymbolKey.Create(symbol, ct)))
            return null;

        var solution = document.Project.Solution;
        timing?.Mark("revalidate symbol");
        Solution renamed;
        // Reference discovery cascades through projects serially. Prepare cold compilations
        // alongside that walk, on this exact snapshot, without changing Roslyn's search scope.
        var ownerPaths = hierarchyProjects.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var primeIds = solution.Projects.Where(project => project.FilePath is { } path
            && ownerPaths.Contains(path)).Select(project => project.Id).ToHashSet();
        using (ColdCompilationPrimer.Start(solution, primeIds, ct))
        {
            renamed = await Renamer.RenameSymbolAsync(
                solution, symbol, new SymbolRenameOptions(), p.NewName, ct);
        }
        timing?.Mark("Roslyn rename");

        var changes = new Dictionary<string, List<TextEdit>>(StringComparer.OrdinalIgnoreCase);

        void Add(string uri, TextEdit edit)
        {
            if (!changes.TryGetValue(uri, out var list))
                changes[uri] = list = [];
            if (!list.Contains(edit))
                list.Add(edit);
        }

        foreach (var projectChange in renamed.GetChanges(solution).GetProjectChanges())
        {
            foreach (var docId in projectChange.GetChangedDocuments())
            {
                var oldDoc = solution.GetDocument(docId);
                var newDoc = renamed.GetDocument(docId);
                if (oldDoc?.FilePath is not { Length: > 0 } path || newDoc is null)
                    continue;

                var oldText = await oldDoc.GetTextAsync(ct);
                foreach (var c in await newDoc.GetTextChangesAsync(oldDoc, ct))
                {
                    Add(LspConverters.PathToUri(path),
                        new TextEdit(LspConverters.ToRange(oldText.Lines, c.Span), c.NewText ?? ""));
                }
            }
        }

        timing?.Mark("compute text edits");
        // The enabled packs' edits, for the same reason AllReferencesAsync asks them: an OnClick=
        // naming this method is a reference Roslyn cannot see, and a rename that skips it leaves
        // the attribute pointing at a method that no longer exists. On a project with no markup a
        // contributor declines after one metadata lookup.
        foreach (var contributor in LanguageScope.Of(languages).Contributors<ILanguageRenameContributor>())
        {
            foreach (var (uri, edit) in
                     await contributor.RenameEditsAsync(symbol, document.Project, p.NewName, ct))
            {
                Add(uri, edit);
            }
        }

        timing?.Mark("language contributors");
        return changes.Count == 0
            ? null
            : new WorkspaceEdit(changes.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));
    }

    internal static async Task<IReadOnlyList<string>> LoadRenameHierarchyAsync(Project origin, CancellationToken ct,
        ISymbol? symbol = null)
    {
        if (origin.FilePath is not { Length: > 0 } projectPath)
            return [];

        // Match workspace ownership: a selected solution takes precedence over a nearer sibling
        // solution, but only when it actually contains the initiating project. A session can also
        // have loose projects open, whose unrelated project sets must not be merged into this one.
        projectPath = Path.GetFullPath(projectPath);
        string? solutionPath = WorkspaceService.BoundSolutionPath;
        var projects = solutionPath is { Length: > 0 }
            ? PathHelper.GetProjectsFromSolution(solutionPath)
            : [];
        if (!projects.Contains(projectPath, StringComparer.OrdinalIgnoreCase))
        {
            solutionPath = PathHelper.FindNearestSolution(projectPath);
            projects = solutionPath is { Length: > 0 }
                ? PathHelper.GetProjectsFromSolution(solutionPath)
                : [];
        }
        if (!projects.Contains(projectPath, StringComparer.OrdinalIgnoreCase))
            return [];

        var loaded = LoadedProjectPaths(origin.Solution);
        if (symbol is not null && projects.Any(path => !loaded.Contains(path))
            && RenameScopeIndex.TryNarrow(origin, symbol, solutionPath!, projects, ct) is { } narrowed)
            projects = narrowed.ToList();
        var missing = projects.Where(path => !loaded.Contains(path)).ToList();
        if (missing.Count == 0)
            return projects;

        // One batch preserves already loaded projects and anchors new ones in the origin's
        // workspace. Passing only missing projects also avoids re-evaluating the warm solution.
        // Like references search, caller cancellation abandons only its wait; the shared load
        // finishes for the next request. Weak workspace ownership cannot retain evicted solutions.
        ct.ThrowIfCancellationRequested();
        var loads = s_hierarchyLoads.GetValue(origin.Solution.Workspace, static _ => new SingleFlight());
        string loadKey = solutionPath + "|" + string.Join("|", missing.Order(StringComparer.OrdinalIgnoreCase));
        await loads.Start(loadKey, _ =>
            WorkspaceService.EnsureProjectsLoadedAsync([projectPath, .. missing], CancellationToken.None)).WaitAsync(ct);
        return projects;
    }

    private static HashSet<string> LoadedProjectPaths(Solution solution) => solution.Projects
        .Where(project => project.FilePath is { Length: > 0 })
        .Select(project => Path.GetFullPath(project.FilePath!))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

}
