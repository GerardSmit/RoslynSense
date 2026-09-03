using System.Text;
using Microsoft.CodeAnalysis;
using RoslynMCP.Languages.Proto.Core;
using RoslynMCP.Languages.Proto.Lsp;
using RoslynMCP.Lsp;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Languages.Proto;

/// <summary>
/// What the pack adds to a request that started in C#: the <c>.proto</c> line a generated symbol
/// was generated from.
/// </summary>
/// <remarks>
/// <para>
/// This is the half that makes navigation symmetrical. From a <c>.proto</c> caret the pack already
/// reaches the implementation and the call sites; without this the way back does not exist, and
/// F12 on a generated message class, a generated property, a service base or an rpc method lands
/// in <c>obj</c> — a file the next build rewrites, so nothing the user came to read or change is
/// there.
/// </para>
/// <para>
/// There is no <c>ILanguageRenameContributor</c> here, and that is a decision rather than a gap. A
/// proto name is part of the contract: an rpc name is the path a client calls, a message name is
/// what a descriptor pool is keyed on, and a field name is what JSON mapping serialises — so
/// rewriting one from an F2 in C# would change what the service answers to, across every language
/// that consumes the <c>.proto</c> and with nothing in the C# diff to show it. Renaming the
/// generated C# alone is meaningless in the other direction, because protoc regenerates the file
/// on the next build and the edit disappears. F2 therefore behaves exactly as it does with the
/// pack switched off.
/// </para>
/// </remarks>
internal sealed partial class ProtoLanguage :
    ILanguageDefinitionContributor,
    ILanguageImplementationContributor,
    ILanguageReferenceContributor,
    ILanguageHoverContributor
{
    /// <summary>
    /// The schema behind a generated symbol, under the C# signature: the declaration as it is
    /// written, its fully-qualified proto name, and the comment above it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The comment is the point. protoc does copy leading comments into its output as XML doc, so
    /// this is not filling a permanent gap — but it is filling the one that matters, because the
    /// generated file is only as current as the last build and a comment is at its most useful in
    /// the minutes after it is written. Reading it out of the <c>.proto</c> makes it visible from C#
    /// as soon as it is saved.
    /// </para>
    /// <para>
    /// The documentation is dropped when the symbol's own XML summary already carries it, which is
    /// what a build makes true. The signature and the proto name stay either way: neither is in the
    /// XML doc, and where the declaration lives is the other half of what this answers.
    /// </para>
    /// </remarks>
    public async Task<string?> HoverMarkdownAsync(ISymbol symbol, Project project, CancellationToken ct)
    {
        if (!InterestingSymbolKinds.Contains(symbol.Kind))
            return null;

        if (!await ProtoReferenceService.HostsProtobufAsync(project, ct))
            return null;

        if ((await ProtoReferenceService.ProtoReferencesToAsync(symbol, project, ct))
            .FirstOrDefault() is not { Declaration: { } declaration } reference)
        {
            return null;
        }

        var markdown = new StringBuilder("```proto\n")
            .Append(ProtoDeclarationText.Signature(declaration))
            .Append("\n```\n\n`")
            .Append(declaration.FullName)
            .Append("` — ")
            .Append(Path.GetFileName(reference.FilePath));

        if (declaration.Documentation is { Length: > 0 } documentation
            && !AlreadyDocumented(symbol, documentation, ct))
        {
            markdown.Append("\n\n").Append(documentation);
        }

        return markdown.ToString();
    }

    /// <summary>Whether the generated symbol already says what the <c>.proto</c> says, which it does
    /// once a build has copied the comment across.</summary>
    private static bool AlreadyDocumented(ISymbol symbol, string documentation, CancellationToken ct) =>
        symbol.GetDocumentationCommentXml(cancellationToken: ct) is { Length: > 0 } xml
        && xml.Contains(documentation.Trim(), StringComparison.Ordinal);

    /// <summary>
    /// F12 on generated code, or on the <c>override</c> in a hand-written service implementation:
    /// the declaration that produced it.
    /// </summary>
    /// <remarks>
    /// The schema line and nothing else, which is what makes F12 a jump rather than a picker. The
    /// other thing a caret on <c>client.UpdateWidgetAsync(…)</c> might be asking — where the rpc is
    /// actually implemented — is a different question and has its own key;
    /// <see cref="ImplementationsAsync"/> answers it. Offering both here meant every F12 on
    /// generated code opened a menu, and the entry the user wanted was a coin toss.
    /// </remarks>
    public Task<IReadOnlyList<LspLocation>> DefinitionsAsync(
        ISymbol symbol, Project project, CancellationToken ct) =>
        DeclaringLinesAsync(symbol, project, ct);

    /// <summary>
    /// Ctrl+F12 on generated code: the hand-written code honouring the contract behind it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Roslyn cannot answer this one at all. A caller calls the generated <em>client</em> and the
    /// server derives from the generated <em>base</em>, so a search from the client method finds no
    /// implementations, no overrides and no derived types — it falls through to the fallback that
    /// lands Ctrl+F12 on the caret it was pressed on. Crossing from one to the other needs the
    /// <c>.proto</c>, which is the one thing that says they are the same rpc.
    /// </para>
    /// <para>
    /// The caret's own symbol is dropped: a caret already on the <c>override</c> is offered the
    /// other implementations of the rpc, not the line it is sitting on.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<LspLocation>> ImplementationsAsync(
        ISymbol symbol, Project project, CancellationToken ct)
    {
        if (!InterestingSymbolKinds.Contains(symbol.Kind))
            return [];

        if (!await ProtoReferenceService.HostsProtobufAsync(project, ct))
            return [];

        var implementations =
            (await ProtoReferenceService.ImplementationsOfAsync(
                symbol, project, ct, ProtoReferenceService.ExplicitSearchBudget))
            .Where(implementation => !IsSelf(implementation, symbol))
            .ToArray();

        return implementations.Length == 0
            ? []
            : await ProtoNavigationHandler.SymbolLocationsAsync(implementations, project, ct);
    }

    /// <summary>
    /// Whether an implementation is the symbol the caret is on.
    /// </summary>
    /// <remarks>
    /// By documentation comment id rather than by <see cref="SymbolEqualityComparer"/>: the
    /// implementation search runs against the solution the contract's project belongs to, which is
    /// not always the snapshot the caret's symbol came from, and symbols do not compare equal across
    /// compilations.
    /// </remarks>
    private static bool IsSelf(ISymbol implementation, ISymbol symbol) =>
        implementation.OriginalDefinition.GetDocumentationCommentId() is { Length: > 0 } id
        && string.Equals(id, symbol.OriginalDefinition.GetDocumentationCommentId(), StringComparison.Ordinal)
        && string.Equals(
            implementation.ContainingAssembly?.Name,
            symbol.ContainingAssembly?.Name,
            StringComparison.Ordinal);

    /// <summary>
    /// Everything protoc wrote, withdrawn once the <c>.proto</c> line behind it has been offered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing is lost by it. A generated file states the same declaration the <c>.proto</c> does,
    /// in a form the user may not edit and the next build replaces, so leaving it beside the real
    /// one turns F12 into a picker whose second entry is never the wanted one — which is the
    /// complaint this answers. Whether it holds the <em>whole</em> answer is why this is asked only
    /// of a request one of the two contributions above already added to.
    /// </para>
    /// <para>
    /// Read off the indexes the contribution just built, by the same recording
    /// <see cref="ProtoReferenceService.FindUsagesAsync"/> filters on, so the file F12 hides and the
    /// file find-usages drops are one set rather than two rules that can drift. No lookup builds
    /// anything: past the <see cref="InterestingSymbolKinds"/> and <see cref="WellKnownTypeNames"/>
    /// gates or not, a solution with no protobuf has built no index and this costs a test on an
    /// empty dictionary.
    /// </para>
    /// </remarks>
    public bool Supersedes(LspLocation location) =>
        ProtoGeneratedIndex.IsKnownGenerated(LspConverters.UriToPath(location.Uri));

    /// <summary>
    /// The <c>.proto</c> line, and every call site of the contract it declares.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The line is not a duplicate of the definition contribution: from C#'s point of view the
    /// <c>.proto</c> is not the symbol's declaration — the generated class is — but the place the
    /// symbol is written about. Contributing it whichever way <c>includeDeclaration</c> was set is
    /// therefore right.
    /// </para>
    /// <para>
    /// The call sites are the half that cannot be got any other way. A caller of a gRPC service
    /// calls the generated <em>client</em>, so a search started from the hand-written
    /// <c>override</c> on the server finds nobody — the two have no C# relationship, and only the
    /// <c>.proto</c> knows they are the same rpc. Reporting the schema line alone reads as "nothing
    /// calls this", which is the opposite of true. Definitions are left out because Roslyn has
    /// already reported the one the caret is on, and the ones it has not are what
    /// go-to-implementation is for.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<LspLocation>> ReferencesAsync(
        ISymbol symbol, Project project, CancellationToken ct, bool waitForCompleteScope = false)
    {
        var declaring = await DeclaringLinesAsync(symbol, project, ct);
        if (declaring.Count == 0)
            return declaring;

        // Only when the user asked. This method is reached both by textDocument/references and by
        // a C# code lens resolving as the view scrolls, and the second must not wait for a
        // contract's consumers to load — that is a solution-wide load behind a gutter number.
        var usages = await ProtoReferenceService.UsagesOfAsync(
            symbol,
            project,
            ct,
            waitForCompleteScope ? ProtoReferenceService.ExplicitSearchBudget : null);
        if (usages.IsEmpty)
            return declaring;

        var results = new List<LspLocation>(declaring);
        results.AddRange(await ProtoNavigationHandler.UsageLocationsAsync(
            usages, project, includeDefinitions: false, ct));

        return results;
    }

    /// <summary>
    /// The <c>.proto</c> declaration behind a C# symbol, as a location, or nothing at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both gates run before anything is read. <see cref="InterestingSymbolKinds"/> is free and so
    /// goes first: a caret on a local, a parameter, an event or a namespace cannot be on generated
    /// protobuf code, and most carets are. What survives that costs one metadata lookup against a
    /// compilation the request already forced — a project that cannot resolve <c>IMessage</c>
    /// references no protobuf runtime and therefore holds no generated code — so a solution with no
    /// protobuf in it pays exactly that and never touches the file system. Only past both does the
    /// binder load, and only then does anything enumerate <c>.proto</c> files.
    /// </para>
    /// <para>
    /// One of the two names in <see cref="WellKnownTypeNames"/> rather than both, and the cheap
    /// half deliberately: the stubs protoc's gRPC plugin emits marshal through
    /// <c>Google.Protobuf.MessageParser</c>, so a project that resolves <c>ClientBase</c> resolves
    /// <c>IMessage</c> too, and asking for the second name would only ever add a lookup to the
    /// projects that were about to decline anyway.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<LspLocation>> DeclaringLinesAsync(
        ISymbol symbol, Project project, CancellationToken ct)
    {
        if (!InterestingSymbolKinds.Contains(symbol.Kind))
            return [];

        if (!await ProtoReferenceService.HostsProtobufAsync(project, ct))
            return [];

        var results = new List<LspLocation>();

        foreach (var reference in await ProtoReferenceService.ProtoReferencesToAsync(symbol, project, ct))
        {
            // The span and the text come from one parse, so no clamping is needed: the reference
            // is measured against the buffer it will be shown in.
            results.Add(new LspLocation(
                LspConverters.PathToUri(reference.FilePath),
                LspConverters.ToRange(reference.Text.Lines, reference.Span)));
        }

        return results;
    }
}
