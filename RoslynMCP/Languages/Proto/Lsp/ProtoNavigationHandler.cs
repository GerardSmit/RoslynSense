using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Proto.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;
using LspRange = RoslynMCP.Lsp.Protocol.Range;

namespace RoslynMCP.Languages.Proto.Lsp;

/// <summary>
/// Navigation and document structure for a caret in a <c>.proto</c>. The LSP dispatch in
/// <c>LspServer</c> routes a request here when the document is a <c>.proto</c>; everything else
/// still goes to the C# handlers.
/// </summary>
/// <remarks>
/// <para>
/// There is no projection in this pack and so nothing to map back.
/// <see cref="ProtoSymbolResolver"/> answers what the caret is on and carries both readings of it at
/// once — the proto declaration and the <see cref="ISymbol"/> protoc generated for it — so every
/// method here chooses between the two rather than resolving a second time.
/// </para>
/// <para>
/// Which reading wins is fixed per request, and go-to-definition is the one worth stating: on a type
/// reference it answers the <c>.proto</c> that declares the name, because that is what the author of
/// a proto is asking for, and typeDefinition is the gesture that reaches the generated class. On a
/// declaration the generated symbol wins instead, since the declaration is already under the caret.
/// </para>
/// <para>
/// Every proto-only answer works without the workspace. A project that has never been built binds no
/// symbol at all, and a type reference still navigates to its declaration and an import still opens
/// its file, because both are read out of the parse and the import graph rather than out of a
/// compilation.
/// </para>
/// </remarks>
internal static class ProtoNavigationHandler
{
    // ---- Navigation ------------------------------------------------------------------------

    public static async Task<LspLocation[]> DefinitionAsync(
        TextDocumentPositionParams p, bool typeDefinition, CancellationToken ct)
    {
        if (await ResolveAsync(p.TextDocument, p.Position, ct) is not var (view, offset))
            return [];

        if (ProtoSymbolResolver.ResolveAt(view, offset) is not { } hit)
            return [];

        // An import names a file and nothing in it, so the top of that file is the whole answer.
        if (hit.Kind == ProtoHitKind.Import)
            return hit.TargetPath is { Length: > 0 } target ? [FileStart(target)] : [];

        if (!typeDefinition && hit.IsReference && DeclarationLocation(hit) is { } declared)
            return [declared];

        return await GeneratedLocationsAsync(view, hit, typeDefinition, ct);
    }

    public static async Task<LspLocation[]> ImplementationAsync(
        TextDocumentPositionParams p, CancellationToken ct)
    {
        if (await ResolveAsync(p.TextDocument, p.Position, ct) is not var (view, offset))
            return [];

        if (ProtoSymbolResolver.ResolveAt(view, offset) is not { } hit
            || view.Project is not { } project)
        {
            return [];
        }

        // No fallback to the symbol itself, which is where the C# handler ends up. Only a service
        // and an rpc have implementations; sending a caret on a message to the generated class it is
        // already bound to would dress an empty answer up as a result.
        return await SymbolLocationsAsync(
            await ProtoReferenceService.FindImplementationsAsync(hit, view.Index, project, ct),
            project, ct);
    }

    /// <summary>Where a set of symbols is declared, as locations the editor can open.</summary>
    internal static Task<LspLocation[]> SymbolLocationsAsync(
        IEnumerable<ISymbol> symbols, Project project, CancellationToken ct) =>
        HandlerHelpers.ToLocationsAsync(
            symbols.SelectMany(symbol => symbol.Locations).Where(location => location.IsInSource),
            project, ct);

    /// <summary>
    /// Every use of the caret's declaration, across the whole solution.
    /// </summary>
    /// <remarks>
    /// Solution-wide by construction rather than by choice: the point of the pack is to get from a
    /// contract to the code implementing or calling it, and that code is in another assembly —
    /// the <c>.proto</c> is in a shared contracts project, the server implementation is in the web
    /// project and the callers are in whatever consumes it.
    /// </remarks>
    public static async Task<LspLocation[]> ReferencesAsync(ReferenceParams p, CancellationToken ct)
    {
        if (await ResolveAsync(p.TextDocument, p.Position, ct) is not var (view, offset))
            return [];

        if (ProtoSymbolResolver.ResolveAt(view, offset) is not { } hit
            || view.Project is not { } project)
        {
            return [];
        }

        return await UsageLocationsAsync(
            await ProtoReferenceService.FindUsagesAsync(hit, view.Index, project, ct),
            project, p.Context.IncludeDeclaration, ct);
    }

    /// <summary>
    /// Usages as locations the editor can open.
    /// </summary>
    /// <remarks>
    /// Through the usage's syntax tree rather than its file path, and then through
    /// <see cref="HandlerHelpers.ToLocationsAsync"/>: a result that landed in a source-generated
    /// document has no file to open, and that helper is what registers it under the URI scheme the
    /// client can fetch. A row standing in for a <c>.proto</c> declaration has no document to take
    /// a tree from and needs neither, being a file on disk already. The pack's code lens uses this
    /// too, so a peek from the gutter and a Shift+F12 cannot disagree about where a result is.
    /// </remarks>
    internal static async Task<LspLocation[]> UsageLocationsAsync(
        IEnumerable<ProtoUsage> usages, Project project, bool includeDefinitions, CancellationToken ct)
    {
        var locations = new List<Microsoft.CodeAnalysis.Location>();
        var declarations = new List<LspLocation>();

        foreach (var usage in usages)
        {
            if (usage.IsDefinition && !includeDefinitions)
                continue;

            if (usage.Document is { } document)
            {
                if (await document.GetSyntaxTreeAsync(ct) is { } tree)
                    locations.Add(Microsoft.CodeAnalysis.Location.Create(tree, usage.Span));
            }
            else if (usage.Text is { } text)
            {
                declarations.Add(new LspLocation(
                    LspConverters.PathToUri(usage.FilePath), ToRange(text, usage.Span)));
            }
        }

        return [.. await HandlerHelpers.ToLocationsAsync(locations, project, ct), .. declarations];
    }

    /// <summary>
    /// Every place in this file that names what the caret names.
    /// </summary>
    /// <remarks>
    /// Same-file and proto-only. This runs on cursor moves, and the generated code the declaration
    /// binds to is in another file by construction — a solution-wide search filtered back down to
    /// one <c>.proto</c> would be the most expensive request in the server answering the cheapest
    /// question in it, and would answer nothing, because no C# result is in a <c>.proto</c>.
    /// </remarks>
    public static async Task<DocumentHighlight[]> DocumentHighlightAsync(
        TextDocumentPositionParams p, CancellationToken ct)
    {
        if (await ResolveAsync(p.TextDocument, p.Position, ct) is not var (view, offset))
            return [];

        if (ProtoSymbolResolver.ResolveAt(view, offset) is not { } hit)
            return [];

        var spans = new List<TextSpan> { hit.Span };

        if (hit.Target is { } target)
        {
            var file = view.Parse;

            if (hit.TargetFile is null
                || string.Equals(hit.TargetFile.FilePath, file.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                spans.Add(target.Name.Span);
            }

            var scope = view.CreateScope();

            foreach (var reference in file.TypeReferences)
            {
                if (reference.IsScalar)
                    continue;

                // Matched on the resolved full name rather than on the declaration object: a
                // fully-qualified proto name is unique across a descriptor pool by protobuf's own
                // rule, so two names that resolve to it resolve to the same thing.
                if (scope.Resolve(reference, file.DeclarationAt(reference.Span.Start)) is { } resolution
                    && string.Equals(resolution.FullName, target.FullName, StringComparison.Ordinal))
                {
                    spans.Add(reference.Span);
                }
            }
        }

        return spans
            .Select(span => new DocumentHighlight(ToRange(view.Text, span), 1))
            .DistinctBy(highlight => highlight.Range)
            .ToArray();
    }

    /// <summary>
    /// Where the C# behind the caret is declared: what the binder bound the declaration to, or —
    /// for one of protoc's own types — the class in the runtime assembly instead, which has no
    /// generated file in the project because it is compiled into <c>Google.Protobuf</c>.
    /// </summary>
    /// <remarks>
    /// Empty rather than approximate when the binder has nothing. A project that has never been
    /// built has no generated code to open, and the file protoc would have written is not a file
    /// that exists.
    /// </remarks>
    private static async Task<LspLocation[]> GeneratedLocationsAsync(
        ProtoProjectView view, ProtoHit hit, bool typeDefinition, CancellationToken ct)
    {
        if (view.Project is not { } project)
            return [];

        var symbols = DefinitionSymbols(view, hit);

        if (symbols.IsEmpty && hit.WellKnown is { } wellKnown)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation?.GetTypeByMetadataName(wellKnown.ClrTypeName) is { } runtimeType)
                symbols = [runtimeType];
        }

        var results = new List<LspLocation>();

        foreach (var symbol in symbols)
        {
            results.AddRange(
                await NavigationHandlers.DefinitionLocationsAsync(symbol, project, typeDefinition, ct));
        }

        return results.Distinct().ToArray();
    }

    /// <summary>
    /// The generated symbols one caret is the definition of.
    /// </summary>
    /// <remarks>
    /// Keyed on the hit's kind and not on the declaration it carries, because a type reference
    /// carries the declaration it was <i>written in</i> — the request type of an <c>rpc</c> reports
    /// the rpc — and switching on that would answer the enclosing declaration's definition for a
    /// caret on a name inside it.
    /// </remarks>
    private static ImmutableArray<ISymbol> DefinitionSymbols(ProtoProjectView view, ProtoHit hit) => hit switch
    {
        // Both halves of a service. The holder class is what the name means, and the base beside it
        // is what a server implementation derives from, which is the reason to be here at all.
        { Kind: ProtoHitKind.ServiceName, Declaration: ProtoService service } =>
            Present(view.Index.ServiceTypeFor(service), view.Index.ServiceBaseFor(service)),

        // Both halves of a oneof, for the same reason: the property is what the code reads and the
        // enum beside it is what the code switches on.
        { Kind: ProtoHitKind.OneofName, Declaration: ProtoOneof oneof } =>
            Present(hit.Symbol, OneofCaseEnum(view.Index, oneof)),

        _ => Present(hit.Symbol),
    };

    /// <summary>
    /// The <c>…OneofCase</c> enum a oneof generates beside its property.
    /// </summary>
    /// <remarks>
    /// Predicted from the proto name, which is what <see cref="ProtoReferenceService"/> does for the
    /// same members and for the same reason: a oneof leaves protoc no anchor in its own output — no
    /// descriptor index, no <c>…FieldNumber</c> constant, no <c>OriginalName</c> attribute — so
    /// there is nothing to read the C# name back off.
    /// </remarks>
    private static ISymbol? OneofCaseEnum(ProtoGeneratedIndex index, ProtoOneof oneof) =>
        oneof.Parent is ProtoMessage owner
            ? index.TypeFor(owner)?.GetMembers(ProtoNaming.OneofCaseEnumName(oneof)).FirstOrDefault()
            : null;

    /// <summary>The <c>.proto</c> declaration a reference names, which is very often not in the file
    /// the reference was written in.</summary>
    private static LspLocation? DeclarationLocation(ProtoHit hit) =>
        hit is { ResolvedProtoTarget: { } target, TargetFile: { } file }
            ? new LspLocation(LspConverters.PathToUri(file.FilePath), ToRange(file.Text, target.Name.Span))
            : null;

    /// <summary>The symbols that are actually there, in order and without the duplicate a service
    /// generating no separate base would otherwise produce.</summary>
    private static ImmutableArray<ISymbol> Present(ISymbol? first, ISymbol? second = null)
    {
        if (first is null)
            return second is null ? [] : [second];

        return second is null || SymbolEqualityComparer.Default.Equals(first, second)
            ? [first]
            : [first, second];
    }

    // ---- Hover -----------------------------------------------------------------------------

    public static async Task<Hover?> HoverAsync(TextDocumentPositionParams p, CancellationToken ct)
    {
        if (await ResolveAsync(p.TextDocument, p.Position, ct) is not var (view, offset))
            return null;

        if (ProtoSymbolResolver.ResolveAt(view, offset) is not { } hit)
            return null;

        return Markdown(view, hit, ct) is { Length: > 0 } markdown
            ? new Hover(new MarkupContent("markdown", markdown), ToRange(view.Text, hit.Span))
            : null;
    }

    /// <summary>
    /// The proto side of what the caret is on, and the C# side under it when the declaration is
    /// bound to one.
    /// </summary>
    /// <remarks>
    /// An <c>option</c> has nothing to show. Its name is already on screen and
    /// <see cref="ProtoHit"/> carries no value to put beside it, so the empty string here becomes no
    /// hover at all rather than a tooltip repeating the word under the pointer.
    /// </remarks>
    private static string Markdown(ProtoProjectView view, ProtoHit hit, CancellationToken ct) => hit.Kind switch
    {
        ProtoHitKind.Import => hit.TargetPath is { Length: > 0 } path
            ? $"`{path}`"
            : $"`{hit.Name}` — not found",

        ProtoHitKind.Syntax => Fence("proto", view.Parse.SyntaxLevel == ProtoSyntaxLevel.Edition
            ? $"edition = \"{hit.Name}\";"
            : $"syntax = \"{hit.Name}\";"),

        ProtoHitKind.Package => Fence("proto", $"package {hit.Name};") + "\n\n" + NamespaceNote(view.Parse),

        ProtoHitKind.OptionName => string.Empty,

        _ => DeclarationMarkdown(view, hit, ct),
    };

    /// <summary>Where the package puts the generated code, which is the one thing about a package a
    /// C# reader cannot see from the <c>.proto</c>.</summary>
    private static string NamespaceNote(ProtoFile file) =>
        ProtoNaming.Namespace(file) is { Length: > 0 } name
            ? $"Generated into namespace `{name}`."
            : "Generated into the global namespace.";

    private static string DeclarationMarkdown(ProtoProjectView view, ProtoHit hit, CancellationToken ct)
    {
        if (hit.Target is not { } declaration)
            return UnresolvedMarkdown(hit);

        var builder = new StringBuilder(
            Fence("proto", ProtoDeclarationText.Signature(declaration)));
        builder.Append("\n\n`").Append(declaration.FullName).Append('`');

        if (declaration.Documentation is { Length: > 0 } documentation)
            builder.Append("\n\n").Append(documentation);

        if (hit.Symbol is { } symbol)
            builder.Append("\n\n").Append(HoverHandler.Describe(symbol, ct));
        else if (hit.WellKnown is { } wellKnown)
            builder.Append("\n\n").Append(Fence("csharp", wellKnown.ClrTypeName));

        return builder.ToString();
    }

    /// <summary>
    /// What there is to say about a name that reached no declaration: a scalar, which declares
    /// nothing anywhere; one of protoc's own types, whose <c>.proto</c> is not on this machine but
    /// whose C# is in the runtime regardless; or a name that does not resolve, which is worth saying
    /// out loud.
    /// </summary>
    private static string UnresolvedMarkdown(ProtoHit hit)
    {
        if (hit.TypeRef is not { } reference)
            return string.Empty;

        if (reference.IsScalar)
            return Fence("proto", reference.Text);

        return hit.WellKnown is { } wellKnown
            ? Fence("proto", wellKnown.FullName) + "\n\n" + Fence("csharp", wellKnown.ClrTypeName)
            : $"`{reference.Text}` — not found";
    }

    private static string Fence(string language, string code) => $"```{language}\n{code}\n```";

    // ---- Outline ---------------------------------------------------------------------------

    public static Task<DocumentSymbol[]> DocumentSymbolAsync(
        DocumentSymbolParams p, CancellationToken ct)
    {
        if (Parse(p.TextDocument) is not { } file)
            return Task.FromResult(Array.Empty<DocumentSymbol>());

        // The file's own four lists rather than the flattened walk, because the tree below is built
        // from ChildDeclarations and only these four can start one.
        var roots = new List<ProtoDeclaration>();
        roots.AddRange(file.Messages);
        roots.AddRange(file.Enums);
        roots.AddRange(file.Services);
        roots.AddRange(file.Extends);
        roots.Sort((left, right) => left.Span.Start.CompareTo(right.Span.Start));

        return Task.FromResult(roots.Select(declaration => ToSymbol(file, declaration)).ToArray());
    }

    /// <summary>
    /// One declaration and everything written inside it.
    /// </summary>
    /// <remarks>
    /// <see cref="ProtoDeclaration.ChildDeclarations"/> and not the parent links: a
    /// <c>oneof</c>'s fields are parented on the enclosing <b>message</b>, because that is where
    /// protobuf scopes them, and walking parents would list them twice — once under the oneof they
    /// are written in and once beside it.
    /// </remarks>
    private static DocumentSymbol ToSymbol(ProtoFile file, ProtoDeclaration declaration) =>
        new(OutlineName(declaration),
            Detail(declaration),
            OutlineKind(declaration),
            ToRange(file.Text, declaration.Span),
            ToRange(file.Text, NameSpan(declaration)),
            [.. declaration.ChildDeclarations.Select(child => ToSymbol(file, child))]);

    /// <summary>
    /// The declaration's own name, or the keyword that opened it while the name is still being
    /// typed.
    /// </summary>
    /// <remarks>
    /// A nameless entry rather than no entry, because everything already written inside the
    /// declaration hangs off it: dropping it would empty the outline of a file from the moment
    /// someone types <c>message</c> at the top of it.
    /// </remarks>
    private static string OutlineName(ProtoDeclaration declaration) =>
        declaration.Name.Value is { Length: > 0 } name ? name : Keyword(declaration.Kind);

    /// <summary>
    /// The name range a client selects when the entry is picked.
    /// </summary>
    /// <remarks>
    /// Clamped into the declaration, because the protocol requires the selection range to be
    /// contained by the full range and a declaration whose name never arrived has an empty span
    /// wherever the parser gave up.
    /// </remarks>
    private static TextSpan NameSpan(ProtoDeclaration declaration) =>
        declaration.Span.Contains(declaration.Name.Span)
            ? declaration.Name.Span
            : new TextSpan(declaration.Span.Start, 0);

    private static int OutlineKind(ProtoDeclaration declaration) => declaration.Kind switch
    {
        ProtoDeclarationKind.Message => LspSymbolKind.Class,
        ProtoDeclarationKind.Field => LspSymbolKind.Field,

        // A oneof is neither of the two things it generates. It groups the fields written inside it,
        // and the outline is the one place that grouping is the whole point of it.
        ProtoDeclarationKind.Oneof => LspSymbolKind.Object,

        ProtoDeclarationKind.Enum => LspSymbolKind.Enum,
        ProtoDeclarationKind.EnumValue => LspSymbolKind.EnumMember,

        // A service is the contract a hand-written class implements, which is what the pack's
        // find-implementations goes looking for and what the icon should say.
        ProtoDeclarationKind.Service => LspSymbolKind.Interface,

        ProtoDeclarationKind.Rpc => LspSymbolKind.Method,
        _ => LspSymbolKind.Object,
    };

    private static string? Detail(ProtoDeclaration declaration) => declaration switch
    {
        // The name is the entry's own label, so the detail beside it is everything else the
        // declaration says: its type and its wire number, or an rpc's two message types.
        ProtoField field =>
            $"{ProtoDeclarationText.Label(field)}{ProtoDeclarationText.TypeText(field)} = {field.Number}",
        ProtoRpc rpc => ProtoDeclarationText.Parameters(rpc),
        ProtoEnumValue value => $"= {value.Number}",

        // An extend block is named after what it extends, so without this the outline shows a
        // message's name and no sign that the entry is not that message.
        ProtoExtend => "extend",

        _ => null,
    };

    private static string Keyword(ProtoDeclarationKind kind) => kind switch
    {
        ProtoDeclarationKind.Message => "message",
        ProtoDeclarationKind.Field => "field",
        ProtoDeclarationKind.Oneof => "oneof",
        ProtoDeclarationKind.Enum => "enum",
        ProtoDeclarationKind.EnumValue => "value",
        ProtoDeclarationKind.Service => "service",
        ProtoDeclarationKind.Rpc => "rpc",
        _ => "extend",
    };

    public static Task<FoldingRange[]> FoldingRangeAsync(FoldingRangeParams p, CancellationToken ct)
    {
        if (Parse(p.TextDocument) is not { } file)
            return Task.FromResult(Array.Empty<FoldingRange>());

        var lines = file.Text.Lines;
        var ranges = new List<FoldingRange>();

        foreach (var declaration in file.AllDeclarations)
        {
            // From the opening brace and not from the keyword, so the declaration's own line stays
            // on screen when it is collapsed. A field and a body-less rpc have no braces at all.
            if (declaration.BodySpan.IsEmpty)
                continue;

            int start = lines.GetLinePosition(declaration.BodySpan.Start).Line;
            int end = lines.GetLinePosition(declaration.BodySpan.End).Line;

            if (end > start)
                ranges.Add(new FoldingRange(start, end, Kind: null));
        }

        AddCommentRuns(file.Text, lines, ranges);

        return Task.FromResult(ranges
            .DistinctBy(range => (range.StartLine, range.EndLine))
            .OrderBy(range => range.StartLine)
            .ToArray());
    }

    /// <summary>
    /// Consecutive comment lines, folded as one — the file header and the block above a declaration
    /// are what this is for.
    /// </summary>
    /// <remarks>
    /// A run rather than a range per comment, because a licence header written as twenty <c>//</c>
    /// lines is one thing to a reader and twenty foldable ranges would be unusable. Adjacent means
    /// on the next line: a blank line between two blocks separates them the same way it separates
    /// documentation from the declaration below it.
    /// </remarks>
    private static void AddCommentRuns(
        SourceText text, TextLineCollection lines, List<FoldingRange> ranges)
    {
        int runStart = -1;
        int runEnd = -1;

        void Flush()
        {
            if (runStart >= 0 && runEnd > runStart)
                ranges.Add(new FoldingRange(runStart, runEnd, FoldingRangeKind.Comment));

            runStart = -1;
            runEnd = -1;
        }

        foreach (var comment in ProtoLexer.Comments(text))
        {
            int start = lines.GetLinePosition(comment.Start).Line;
            int end = lines.GetLinePosition(comment.End).Line;

            if (runStart >= 0 && start <= runEnd + 1)
            {
                runEnd = Math.Max(runEnd, end);
                continue;
            }

            Flush();
            runStart = start;
            runEnd = end;
        }

        Flush();
    }

    // ---- Shared plumbing -------------------------------------------------------------------

    private static async Task<(ProtoProjectView View, int Offset)?> ResolveAsync(
        TextDocumentIdentifier textDocument, Position position, CancellationToken ct)
    {
        string path = LspConverters.UriToPath(textDocument.Uri);
        if (await ProtoWorkspace.GetAsync(path, ct) is not { } view)
            return null;

        return (view, LspConverters.ToOffset(view.Text, position));
    }

    /// <summary>
    /// The parse alone, which is everything the outline and the folding are built from.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="ProtoWorkspace.GetAsync"/>: neither answer names a C# symbol, and
    /// going through the workspace would make opening a <c>.proto</c> wait on a solution load to
    /// draw its own outline.
    /// </remarks>
    private static ProtoFile? Parse(TextDocumentIdentifier textDocument) =>
        ProtoDocumentService.GetParse(LspConverters.UriToPath(textDocument.Uri));

    private static LspRange ToRange(SourceText text, TextSpan span) =>
        LspConverters.ToRange(text.Lines, Clamp(text, span));

    /// <summary>
    /// A span measured against text that has since been edited still has to produce a range inside
    /// the buffer, or the client drops the whole response.
    /// </summary>
    private static TextSpan Clamp(SourceText text, TextSpan span)
    {
        int start = Math.Clamp(span.Start, 0, text.Length);
        int end = Math.Clamp(span.End, start, text.Length);
        return TextSpan.FromBounds(start, end);
    }

    private static LspLocation FileStart(string path) =>
        new(LspConverters.PathToUri(path), new LspRange(new Position(0, 0), new Position(0, 0)));
}
