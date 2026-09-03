using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages;

// One interface per LSP request a pack can own outright. Signatures mirror the C# handlers in
// Lsp/Handlers so the dispatch in LspServer is a straight either/or: the pack answers about its
// own files, Roslyn answers about everything else, and neither knows about the other. A pack
// implements only what it can answer — an unimplemented request falls through to C#.
//
// Requests that carry no document are the exception and cannot be routed this way. Rule for
// resolve endpoints: a pack's completion items, code actions and code lenses must either be
// self-contained, or carry enough in their data payload (the pack's Id, or a uri as
// CodeLensData already does) for the resolve handler to find its way back here.

/// <summary>textDocument/definition and textDocument/typeDefinition.</summary>
internal interface ILanguageDefinitionProvider
{
    Task<Location[]> DefinitionAsync(
        TextDocumentPositionParams p, bool typeDefinition, CancellationToken ct);
}

/// <summary>textDocument/implementation.</summary>
internal interface ILanguageImplementationProvider
{
    Task<Location[]> ImplementationAsync(TextDocumentPositionParams p, CancellationToken ct);
}

/// <summary>textDocument/references, for a caret inside the pack's own file. Merging markup hits
/// into an answer about a <em>C#</em> file is the other direction and belongs to
/// <see cref="ILanguageReferenceContributor"/>.</summary>
internal interface ILanguageReferencesProvider
{
    Task<Location[]> ReferencesAsync(ReferenceParams p, CancellationToken ct);
}

/// <summary>textDocument/hover.</summary>
internal interface ILanguageHoverProvider
{
    Task<Hover?> HoverAsync(TextDocumentPositionParams p, CancellationToken ct);
}

/// <summary>textDocument/documentHighlight.</summary>
internal interface ILanguageDocumentHighlightProvider
{
    Task<DocumentHighlight[]> DocumentHighlightAsync(
        TextDocumentPositionParams p, CancellationToken ct);
}

/// <summary>textDocument/documentSymbol.</summary>
internal interface ILanguageDocumentSymbolProvider
{
    Task<DocumentSymbol[]> DocumentSymbolAsync(DocumentSymbolParams p, CancellationToken ct);
}

/// <summary>textDocument/foldingRange.</summary>
internal interface ILanguageFoldingRangeProvider
{
    Task<FoldingRange[]> FoldingRangeAsync(FoldingRangeParams p, CancellationToken ct);
}

/// <summary>
/// Diagnostics for one of the pack's files. Keyed on a path rather than on request parameters
/// because both the push publisher and the pull endpoint need it, and neither shares a parameter
/// shape with the other.
/// </summary>
internal interface ILanguageDiagnosticProvider
{
    Task<Diagnostic[]> DiagnosticsAsync(string filePath, CancellationToken ct);
}

/// <summary>textDocument/prepareRename and textDocument/rename.</summary>
internal interface ILanguageRenameProvider
{
    Task<PrepareRenameResult?> PrepareRenameAsync(
        TextDocumentPositionParams p, CancellationToken ct);

    Task<WorkspaceEdit?> RenameAsync(RenameParams p, CancellationToken ct);
}

/// <summary>textDocument/signatureHelp.</summary>
internal interface ILanguageSignatureHelpProvider
{
    Task<SignatureHelp?> SignatureHelpAsync(SignatureHelpParams p, CancellationToken ct);
}

/// <summary>
/// textDocument/completion, plus the resolve hook for the items it produced.
/// </summary>
/// <remarks>
/// The resolve request carries no document, so the LSP server cannot route it by URI. A pack
/// whose items need resolution has to stamp <see cref="ILanguagePack.Id"/> into
/// <c>CompletionItem.Data</c> and the resolve handler routes on that; a pack whose items are
/// complete as sent returns the item unchanged and never sees a resolve at all.
/// </remarks>
internal interface ILanguageCompletionProvider
{
    Task<CompletionList> CompletionAsync(
        CompletionParams p, LspResolveCache cache, CancellationToken ct);

    Task<CompletionItem> ResolveCompletionAsync(
        CompletionItem item, LspResolveCache cache, CancellationToken ct);
}

/// <summary>textDocument/codeAction, plus the resolve hook. Same data-payload rule as
/// <see cref="ILanguageCompletionProvider"/>.</summary>
internal interface ILanguageCodeActionProvider
{
    Task<CodeAction[]> CodeActionsAsync(CodeActionParams p, CancellationToken ct);

    Task<CodeAction> ResolveCodeActionAsync(CodeAction action, CancellationToken ct);
}

/// <summary>
/// Call and type hierarchy. One interface for all six requests because they are one feature: a
/// pack that can produce an item must also answer what that item calls and derives from, and the
/// item's URI has to be a file the user can open rather than the pack's projection.
/// </summary>
internal interface ILanguageHierarchyProvider
{
    Task<HierarchyItem[]> PrepareCallHierarchyAsync(
        TextDocumentPositionParams p, CancellationToken ct);

    Task<CallHierarchyIncomingCall[]> IncomingCallsAsync(
        CallHierarchyCallsParams p, CancellationToken ct);

    Task<CallHierarchyOutgoingCall[]> OutgoingCallsAsync(
        CallHierarchyCallsParams p, CancellationToken ct);

    Task<HierarchyItem[]> PrepareTypeHierarchyAsync(
        TextDocumentPositionParams p, CancellationToken ct);

    Task<HierarchyItem[]> SupertypesAsync(TypeHierarchyItemParams p, CancellationToken ct);

    Task<HierarchyItem[]> SubtypesAsync(TypeHierarchyItemParams p, CancellationToken ct);
}

/// <summary>textDocument/linkedEditingRange.</summary>
internal interface ILanguageLinkedEditingProvider
{
    Task<LinkedEditingRanges?> LinkedEditingRangesAsync(
        TextDocumentPositionParams p, CancellationToken ct);
}

/// <summary>textDocument/selectionRange.</summary>
internal interface ILanguageSelectionRangeProvider
{
    Task<SelectionRange[]> SelectionRangesAsync(SelectionRangeParams p, CancellationToken ct);
}

/// <summary>
/// textDocument/semanticTokens, full and range.
/// </summary>
/// <remarks>
/// Both methods take the session because the legend is per-connection: the token type and
/// modifier numbers a pack emits depend on which other packs are enabled alongside it, so the
/// pack asks the session for its own offsets rather than holding them. Delta is optional — a
/// pack that declines it answers full every time, which the protocol allows and clients handle.
/// </remarks>
internal interface ILanguageSemanticTokensProvider
{
    bool SupportsDelta { get; }

    Task<SemanticTokens> SemanticTokensFullAsync(
        SemanticTokensParams p, LanguageSession session, CancellationToken ct);

    Task<SemanticTokens> SemanticTokensRangeAsync(
        SemanticTokensRangeParams p, LanguageSession session, CancellationToken ct);
}

/// <summary>textDocument/codeLens. The resolve hook routes on <c>CodeLensData.Uri</c>, which
/// every lens already carries.</summary>
internal interface ILanguageCodeLensProvider
{
    Task<CodeLens[]> CodeLensAsync(CodeLensParams p, CancellationToken ct);

    Task<CodeLens> ResolveCodeLensAsync(CodeLens lens, CancellationToken ct);
}

/// <summary>
/// Opts a code-lens pack into having its resolved lenses kept — see
/// <see cref="Lsp.CodeLensResolveMemo"/>. Implement it when resolving a lens is expensive enough
/// that answering the same question twice is worth avoiding.
/// </summary>
/// <remarks>
/// <para>
/// The one thing a pack has to supply is what its answers depend on, because only the pack knows.
/// Return a value whose <b>equality</b> is the staleness test: two calls returning equal values
/// promise that every lens in that file would resolve identically. A record over the immutable
/// snapshots the answers came from is the usual shape — the buffer, whatever generated artefact was
/// consulted, the solution — since those compare by reference and a new one appears exactly when
/// something moved.
/// </para>
/// <para>
/// Err towards a generation that changes too often. A spurious change costs a recomputation, which
/// is what would have happened anyway; a missed one puts a stale number in the gutter, and a wrong
/// count is worse than a slow one.
/// </para>
/// <para>
/// Return <see langword="null"/> when the state cannot be described yet — no view for that file,
/// nothing built — and the resolve runs uncached rather than being refused.
/// </para>
/// </remarks>
internal interface ILanguageCodeLensGeneration
{
    ValueTask<object?> LensGenerationAsync(string uri, CancellationToken ct);
}

/// <summary>textDocument/documentLink: the file paths a markup document names — a master page,
/// a user control's <c>Src</c>, a script or stylesheet — made openable.</summary>
internal interface ILanguageDocumentLinkProvider
{
    Task<DocumentLink[]> DocumentLinksAsync(DocumentLinkParams p, CancellationToken ct);
}

/// <summary>roslynSense/onAutoInsert: the character just typed completes into something longer,
/// such as a closing tag.</summary>
internal interface ILanguageAutoInsertProvider
{
    Task<OnAutoInsertResult?> OnAutoInsertAsync(OnAutoInsertParams p, CancellationToken ct);
}

/// <summary>textDocument/formatting and textDocument/rangeFormatting.</summary>
internal interface ILanguageFormattingProvider
{
    Task<TextEdit[]> FormatAsync(DocumentFormattingParams p, CancellationToken ct);

    Task<TextEdit[]> FormatRangeAsync(DocumentRangeFormattingParams p, CancellationToken ct);
}

/// <summary>
/// workspace/executeCommand for the commands a pack declared in
/// <see cref="LanguageCapabilities.Commands"/>. A command has no document, so it is dispatched by
/// name across every registered pack rather than resolved from a URI.
/// </summary>
internal interface ILanguageCommandProvider
{
    bool CanExecute(string command);

    Task<object> ExecuteCommandAsync(ExecuteCommandParams p, CancellationToken ct);
}
