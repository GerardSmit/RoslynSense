using Microsoft.CodeAnalysis;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Languages;

// The other direction. A provider answers a question about the pack's own file; a contributor
// adds to an answer about a C# file, or about the workspace as a whole. Every registered pack's
// contributors run — the question is not "whose file is this" but "does anyone have more to say"
// — so a contributor must be cheap to decline. That is what ILanguagePack.WellKnownTypeNames is
// for: resolve them against the compilation once and return immediately when none are present.

/// <summary>Which navigation verb is asking.</summary>
/// <remarks>
/// One enum rather than three interfaces: the three questions have one answer shape, and a pack
/// that only wants to answer some of them returns nothing for the rest.
/// </remarks>
internal enum NavigationKind { Definition, TypeDefinition, Implementation }

/// <summary>Everything a redirector is given, as one value.</summary>
/// <remarks>
/// A record rather than four parameters, so that a field added later is not a breaking change to
/// every implementer.
/// </remarks>
internal readonly record struct NavigationContext(
    Document Document, int Offset, ISymbol Symbol, NavigationKind Kind);

/// <summary>
/// Where a caret really points, when the symbol Roslyn bound is a dispatcher rather than a
/// destination. <c>_mediator.Send(new CreateUserRequest())</c> binds to <c>ISender.Send</c> — the
/// same metadata member every send in the solution binds to — so F12 lands somewhere no one asked
/// about, and the handler that actually runs is reachable only by reading the argument.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the <em>symbol</em> where <see cref="ILanguageDefinitionContributor"/> replaces at most
/// a location, and the two are separate interfaces because that difference is the whole content of
/// each. A contributor is handed a symbol Roslyn bound correctly and says where it was really
/// written; a dispatcher is not the symbol the caret is about at all, so no amount of extra or
/// withdrawn locations for it can name the handler.
/// </para>
/// <para>
/// Empty means "not mine", and is the normal answer: a <c>Send</c> on a socket, and every other
/// caret in the solution. It is not an error and it has to be cheap. Deciding the caret is a
/// dispatch but not being able to name the handler — a request built by a factory, or passed as
/// <c>object</c> — is also empty, because Roslyn's own answer beats a guessed one.
/// </para>
/// <para>
/// Symbols rather than locations: the editor wants a location with the fallbacks
/// <see cref="Lsp.Handlers.NavigationHandlers.DefinitionLocationsAsync"/> applies to a handler that
/// lives in a referenced assembly, and the MCP tool wants the symbol so it can render its
/// signature and docs. Returning a location would make one of the two re-derive what the other
/// already had.
/// </para>
/// </remarks>
internal interface ILanguageDefinitionRedirector
{
    Task<IReadOnlyList<ISymbol>> RedirectAsync(NavigationContext context, CancellationToken ct);
}

/// <summary>
/// The withdrawing half of a contribution: which of the results Roslyn already produced are output
/// this pack generated, and are therefore noise now that the declaration behind them has been
/// contributed.
/// </summary>
/// <remarks>
/// <para>
/// Offering both is what this exists to stop. Two targets makes the editor put up a picker whose
/// second entry is a file in <c>obj</c> that the next build overwrites — so F12 stops being a jump
/// and becomes a choice, and half of the choice is wrong. Withdrawing is safe only because it is
/// asked of a pack that contributed to <em>this</em> request: a pack that answered nothing is never
/// asked, and so can never leave a request with no results.
/// </para>
/// <para>
/// One member on one interface rather than one per verb, because a file is generated or it is not,
/// and F12 hiding it while Shift+F12 lists it is the two features disagreeing about the same pair of
/// files — the failure that is worse than either answer alone. It is inherited by both contributors
/// for that reason and not because the two questions are related.
/// </para>
/// <para>
/// A location and not a <see cref="Document"/>, because the filtering happens after the C# answer
/// has been converted for the wire and some of it — a decompiled or Source Link target — names no
/// document at all. Answering has to be free: this runs per result on every navigation in the
/// solution, so an implementation reads what the contribution already computed and computes nothing
/// of its own.
/// </para>
/// <para>
/// False by default, which is the answer for every contributor that only adds. An <c>.aspx</c>
/// naming a handler does not make the handler's own declaration noise; only the pack that knows a
/// file was generated can say that it was.
/// </para>
/// </remarks>
internal interface ILanguageSupersedingContributor
{
    bool Supersedes(LspLocation location) => false;
}

/// <summary>
/// Extra definition targets for a symbol declared in C#, folded into
/// <c>textDocument/definition</c> on the symbol itself. A generated <c>.cs</c> is not where the
/// declaration was written: F12 landing in <c>obj</c> puts the caret in a file the next build
/// overwrites, so whatever the user came to read or change is not there. The pack that knows what
/// generated the file knows the line that did.
/// </summary>
internal interface ILanguageDefinitionContributor : ILanguageSupersedingContributor
{
    Task<IReadOnlyList<LspLocation>> DefinitionsAsync(
        ISymbol symbol, Project project, CancellationToken ct);
}

/// <summary>
/// Markup references to a C# symbol, folded into <c>textDocument/references</c> on the symbol
/// itself. The <c>OnClick="Save_Click"</c> in an <c>.aspx</c> is a reference to the method that
/// Roslyn cannot see, so without this find-references from the code-behind under-reports.
/// </summary>
/// <remarks>
/// Superseding as well as adding, for the same reason go-to-definition does it: a symbol protoc
/// generated is mentioned all over the file protoc wrote, and listing those beside the hand-written
/// call sites buries the answer in code the next build replaces. It also settles a disagreement —
/// the pack's own search from a <c>.proto</c> already drops generated documents, so without this the
/// same relationship is reported one way from the schema and another way from the C#.
/// </remarks>
internal interface ILanguageReferenceContributor : ILanguageSupersedingContributor
{
    /// <param name="waitForCompleteScope">
    /// Whether the caller is a gesture the user made on purpose and can therefore afford a wait,
    /// or something incidental that must answer now.
    /// </param>
    /// <remarks>
    /// The distinction is not cosmetic, because one request reaches this method by two very
    /// different routes: <c>textDocument/references</c>, which a user pressed a key for, and a code
    /// lens resolving as the view scrolls. A pack whose answer needs projects the workspace has not
    /// loaded may block the first and must not block the second — a lens that loads a solution is
    /// how an editor stops responding on a large repository.
    /// </remarks>
    Task<IReadOnlyList<LspLocation>> ReferencesAsync(
        ISymbol symbol, Project project, CancellationToken ct, bool waitForCompleteScope = false);
}

/// <summary>
/// Hand-written implementations of a contract a pack owns, folded into
/// <c>textDocument/implementation</c> on a C# symbol.
/// </summary>
/// <remarks>
/// The counterpart of <see cref="ILanguageDefinitionContributor"/>, and what lets the two verbs
/// answer the two questions instead of one of them answering both. A caller of a gRPC service calls
/// the generated client, and the server implements the generated base: the two have no C#
/// relationship, so Roslyn's answer for Ctrl+F12 on a client call is the client call — it falls
/// through every arm of the search and lands back on the caret. Only the pack can cross from one to
/// the other, because only the <c>.proto</c> says they are the same rpc.
/// </remarks>
internal interface ILanguageImplementationContributor : ILanguageSupersedingContributor
{
    Task<IReadOnlyList<LspLocation>> ImplementationsAsync(
        ISymbol symbol, Project project, CancellationToken ct);
}

/// <summary>
/// What a pack has to say about a C# symbol it generated, appended to <c>textDocument/hover</c>.
/// </summary>
/// <remarks>
/// A generated declaration carries none of the intent behind it. The comment above
/// <c>CHANNEL_ALPHA = 1;</c> in a <c>.proto</c> is the only documentation the enum member has, and
/// a reader hovering <c>Channel.Alpha</c> in C# sees a bare qualified name — worse, the two are out
/// of step for as long as the build is, so the comment stays invisible exactly while it is being
/// written. Returning the schema's own line and its comment is what closes that, and it is the same
/// text the pack shows for a caret in its own file.
/// </remarks>
internal interface ILanguageHoverContributor
{
    /// <summary>Markdown to append, or <c>null</c> when the pack does not recognise the symbol.</summary>
    Task<string?> HoverMarkdownAsync(ISymbol symbol, Project project, CancellationToken ct);
}

/// <summary>
/// Markup call sites of a C# symbol, folded into <c>callHierarchy/incomingCalls</c> on the symbol
/// itself. The counterpart of <see cref="ILanguageReferenceContributor"/> for the other half of
/// navigation: without it a method a page calls from <c>&lt;% %&gt;</c> looks uncalled to the
/// hierarchy while find-references on the identical caret reports the very same call sites — two
/// features disagreeing silently, which is worse than either answer alone.
/// </summary>
internal interface ILanguageCallHierarchyContributor
{
    /// <summary>
    /// Calls to <paramref name="symbol"/> from the pack's own files, as items the editor can open.
    /// The C# callers are the workspace's answer and are not repeated here.
    /// </summary>
    Task<IReadOnlyList<CallHierarchyIncomingCall>> IncomingCallsAsync(
        ISymbol symbol, Project project, CancellationToken ct);
}

/// <summary>
/// Lenses over a C# document that only the pack can count. A mediator handler is called from
/// everywhere and referenced from nowhere, so the reference lens over its <c>Handle</c> reads
/// "0 references" while find-references on the same line lists a dozen — the gutter contradicting
/// the peek.
/// </summary>
/// <remarks>
/// Two methods rather than one because the cost is in the counting: a lens is emitted for every
/// member of an open document, and only the few the editor actually shows are resolved. A
/// contributed lens carries <see cref="ILanguagePack.Id"/> in
/// <see cref="CodeLensData.PackId"/>, which is how <c>codeLens/resolve</c> finds its way back —
/// the URI cannot say, because the document is C# and belongs to no pack.
/// </remarks>
internal interface ILanguageCodeLensContributor
{
    Task<IReadOnlyList<CodeLens>> CodeLensAsync(Document document, CancellationToken ct);

    /// <summary>
    /// The resolved lens, or null when the data is not this pack's to read.
    /// </summary>
    Task<CodeLens?> ResolveCodeLensAsync(CodeLens lens, CancellationToken ct);
}

/// <summary>
/// The edits a C#-side rename needs on top of Roslyn's own. Same references as
/// <see cref="ILanguageReferenceContributor"/> finds, rewritten — and not optional: leaving them
/// out turns F2 into silent corruption, an attribute naming a method that no longer exists.
/// </summary>
internal interface ILanguageRenameContributor
{
    Task<IReadOnlyList<(string Uri, TextEdit Edit)>> RenameEditsAsync(
        ISymbol symbol, Project project, string newName, CancellationToken ct);
}

// The symbol-free pair below is a different seam from the three contributors above, and the
// difference is not a detail. A contributor is handed the ISymbol a rename or a search is already
// running on and adds to that answer. These two run when there is no symbol at all and nothing has
// started: a resource key, a route segment, anything whose identity the pack itself defines.
// Faking an ISymbol for one is not an option — SymbolEqualityComparer, SymbolFinder and
// Renamer.RenameSymbolAsync all assume Roslyn's own hierarchy. And they run BEFORE the symbol
// lookup, never after: a caret inside a string literal binds to nothing, so every handler has
// already returned by the time a contributor would be reached.

/// <summary>
/// A rename whose subject is not an <see cref="ISymbol"/>. The provider owns the position or it
/// does not; there is no partial answer.
/// </summary>
internal interface ISymbolFreeRenameProvider
{
    /// <summary>
    /// The range and placeholder for a position this provider owns, or null. Null is also the
    /// answer when the position <em>is</em> owned but the rename would be a guess — refusing is a
    /// feature, since applying a rename across a file set that was inferred rather than resolved
    /// corrupts silently.
    /// </summary>
    Task<PrepareRenameResult?> PrepareAsync(string filePath, int offset, CancellationToken ct);

    /// <summary>
    /// Every edit the rename needs, across every file kind, or null when the position is not
    /// owned. <paramref name="project"/> is the caller's context when it has one; a provider
    /// reached from a file Roslyn knows nothing about resolves its own.
    /// </summary>
    Task<WorkspaceEdit?> RenameAsync(
        string filePath, int offset, string newName, Project? project, CancellationToken ct);
}

/// <summary>
/// Find-references for the same symbol-free subjects. Separate interface rather than a second
/// method on <see cref="ISymbolFreeRenameProvider"/>: references are answerable where a rename is
/// refused, because over-reporting a match is a nuisance and rewriting a guess is corruption.
/// </summary>
internal interface ISymbolFreeReferenceProvider
{
    Task<IReadOnlyList<LspLocation>?> ReferencesAsync(
        string filePath, int offset, Project? project, CancellationToken ct);
}

/// <summary>
/// Symbols the pack owns, for <c>workspace/symbol</c> — control IDs, page classes, registered
/// user controls. Roslyn's declaration search covers only its own compilations.
/// </summary>
internal interface ILanguageWorkspaceSymbolProvider
{
    Task<IReadOnlyList<SymbolInformation>> WorkspaceSymbolsAsync(
        string query, Solution solution, CancellationToken ct);
}

/// <summary>
/// The pack's files in a project, diagnosed for the workspace sweep. Without it markup problems
/// reach the Problems panel only while the file is open, because the sweep iterates
/// <see cref="Project.Documents"/> and no markup file is one.
/// </summary>
internal interface ILanguageWorkspaceDiagnosticContributor
{
    /// <summary>
    /// Reports for the pack's files in <paramref name="project"/>. Each item is a
    /// <see cref="WorkspaceFullDocumentDiagnosticReport"/> or, when the file's result id matches
    /// what the client already holds, a <see cref="WorkspaceUnchangedDocumentDiagnosticReport"/> —
    /// the union the protocol puts in one array.
    /// </summary>
    Task<IReadOnlyList<object>> DiagnoseProjectAsync(
        Project project,
        IReadOnlyDictionary<string, string> previousResultIds,
        CancellationToken ct);
}

/// <summary>
/// File operations over the pack's files: renaming <c>Default.aspx</c> has to carry its
/// code-behind and designer siblings with it and rewrite the directive that names the class.
/// The globs the server registers for come from
/// <see cref="LanguageCapabilities.FileOperationGlobs"/>; this is what runs when one matches.
/// </summary>
internal interface ILanguageFileOperationProvider
{
    Task<WorkspaceEdit?> WillRenameAsync(RenameFilesParams p, CancellationToken ct);

    Task DidCreateAsync(CreateFilesParams p, CancellationToken ct);

    Task DidDeleteAsync(DeleteFilesParams p, CancellationToken ct);
}

/// <summary>
/// A file the pack cares about changed on disk rather than in the editor — a branch switch, a
/// scaffold, another agent's edit. The pack drops whatever it had cached for it.
/// </summary>
internal interface ILanguageWatchedFileHandler
{
    /// <summary>
    /// Invalidates anything cached for <paramref name="path"/>, returning whether the path was
    /// one this pack recognised. False means "not mine", not "nothing to do".
    /// </summary>
    bool Invalidate(string path, WatchedFileChange change);
}

/// <summary>
/// How a watched file changed on disk. Mirrors LSP's <c>FileChangeType</c>, redeclared here so a
/// pack needs no protocol reference — and because the distinction is load-bearing: a content edit
/// and a membership change (create/delete/rename) invalidate different amounts of cache.
/// </summary>
internal enum WatchedFileChange
{
    Created,
    Changed,
    Deleted,
}
