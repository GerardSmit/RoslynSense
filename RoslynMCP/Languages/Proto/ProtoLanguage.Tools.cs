using RoslynMCP.Languages.Proto.Core;
using RoslynMCP.Languages.Proto.Tools;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.Proto;

/// <summary>
/// The MCP side of the pack. The tools ask for <c>IEnumerable&lt;I*Handler&gt;</c> and know nothing
/// about packs, so the pack implements those interfaces and forwards; that way one registration
/// gate — <c>settings.Proto</c> — governs the editor features and the AI tools together instead of
/// each having its own.
/// </summary>
/// <remarks>
/// <para>
/// No <c>IRenameHandler</c>, deliberately. That interface has no <c>CanHandle</c> — every
/// registered handler runs on every C# rename, because one rename may touch several file types at
/// once — so implementing it would put this pack in the path of every F2 in the solution. There is
/// nothing for it to do there and two reasons not to try: renaming a proto declaration to follow a
/// renamed C# member rewrites the wire contract, which is a decision no rename can make on the
/// user's behalf; and editing the generated C# instead is pointless, since the next build
/// overwrites it from the <c>.proto</c> that was not changed.
/// </para>
/// <para>
/// The four handlers below are thin by construction. Each holds one formatter over
/// <c>Proto/Core</c>, and the formatter is the same engine the LSP providers drive — a caret comes
/// from <see cref="ProtoSymbolResolver"/> and references come from
/// <see cref="ProtoReferenceService"/> on both sides, so the answer an AI gets and the answer the
/// editor shows cannot drift apart.
/// </para>
/// </remarks>
internal sealed partial class ProtoLanguage :
    IGoToDefinitionHandler,
    IFindUsagesHandler,
    IOutlineHandler,
    IDiagnosticsHandler
{
    private ProtoGoToDefinition _goToDefinition = null!;
    private ProtoFindUsages _findUsages = null!;
    private readonly ProtoOutline _outline = new();
    private readonly ProtoDiagnostics _diagnostics = new();

    private void InitializeToolHandlers(IOutputFormatter formatter)
    {
        _goToDefinition = new ProtoGoToDefinition(formatter);
        _findUsages = new ProtoFindUsages(formatter);
    }

    public bool CanHandle(string filePath) => ProtoDocumentService.IsProtoFile(filePath);

    public Task<string> ResolveAsync(
        string systemPath, string markupSnippet, int contextLines, CancellationToken cancellationToken) =>
        _goToDefinition.ResolveAsync(systemPath, markupSnippet, contextLines, cancellationToken);

    public Task<string> FindUsagesAsync(
        string systemPath, string markupSnippet, int maxResults,
        CancellationToken cancellationToken, int? hintLine = null) =>
        _findUsages.FindUsagesAsync(systemPath, markupSnippet, maxResults, cancellationToken, hintLine);

    public Task<string> GetOutlineAsync(string systemPath, CancellationToken cancellationToken) =>
        _outline.GetOutlineAsync(systemPath, cancellationToken);

    public Task<string> ValidateAsync(
        string systemPath, IOutputFormatter fmt, CancellationToken cancellationToken) =>
        _diagnostics.ValidateAsync(systemPath, fmt, cancellationToken);
}
