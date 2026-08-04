using Microsoft.CodeAnalysis;
using RoslynMCP.Languages.Proto.Core;
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
    ILanguageReferenceContributor
{
    /// <summary>
    /// F12 on generated code, or on the <c>override</c> in a hand-written service implementation,
    /// reaching the declaration that produced it.
    /// </summary>
    public Task<IReadOnlyList<LspLocation>> DefinitionsAsync(
        ISymbol symbol, Project project, CancellationToken ct) =>
        DeclaringLinesAsync(symbol, project, ct);

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
    /// The same line, folded into find-references.
    /// </summary>
    /// <remarks>
    /// Not a duplicate of the definition contribution, because from C#'s point of view the
    /// <c>.proto</c> line is not the symbol's declaration — the generated class is — but the place
    /// the symbol is written about. Contributing it whichever way <c>includeDeclaration</c> was set
    /// is therefore right, and it is what makes the two directions agree: find-usages started in
    /// the <c>.proto</c> already lists the C#, so without this the same pair of files reports a
    /// relationship in one direction only.
    /// </remarks>
    public Task<IReadOnlyList<LspLocation>> ReferencesAsync(
        ISymbol symbol, Project project, CancellationToken ct) =>
        DeclaringLinesAsync(symbol, project, ct);

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
