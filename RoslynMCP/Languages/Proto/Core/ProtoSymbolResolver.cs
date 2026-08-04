using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMCP.Languages.Proto.Core;

/// <summary>What the caret is sitting on in a <c>.proto</c> file.</summary>
internal enum ProtoHitKind
{
    /// <summary>The name in a <c>package</c> statement.</summary>
    Package,

    /// <summary>An <c>import</c> statement, keyword or path.</summary>
    Import,

    /// <summary>The name a <c>message</c> declares.</summary>
    MessageName,

    /// <summary>The name a field declares.</summary>
    FieldName,

    /// <summary>A field's type, or either half of a <c>map&lt;K, V&gt;</c>.</summary>
    FieldType,

    /// <summary>The name a <c>oneof</c> declares.</summary>
    OneofName,

    /// <summary>The name an <c>enum</c> declares.</summary>
    EnumName,

    /// <summary>The name an enum member declares.</summary>
    EnumValueName,

    /// <summary>The name a <c>service</c> declares.</summary>
    ServiceName,

    /// <summary>The name an <c>rpc</c> declares.</summary>
    RpcName,

    /// <summary>An rpc's request type.</summary>
    RpcRequestType,

    /// <summary>An rpc's response type.</summary>
    RpcResponseType,

    /// <summary>The type an <c>extend</c> block targets.</summary>
    ExtendTarget,

    /// <summary>The name of an <c>option</c>, on the file, on a declaration or in a field's
    /// <c>[ … ]</c> list.</summary>
    OptionName,

    /// <summary>The <c>syntax</c> or <c>edition</c> statement.</summary>
    Syntax,
}

/// <summary>One resolved caret position in a <c>.proto</c> file.</summary>
/// <param name="Kind">What kind of thing the caret is on.</param>
/// <param name="Span">The source span of the token under the caret.</param>
/// <param name="Declaration">The declaration the caret is on, for a hit that names one.</param>
/// <param name="Symbol">The generated C# symbol, when the project has been built and the binding
/// was found.</param>
/// <param name="TypeRef">The reference under the caret, for the four type-reference kinds.</param>
/// <param name="Import">The import statement, for <see cref="ProtoHitKind.Import"/>.</param>
/// <param name="ResolvedProtoTarget">
/// The declaration a reference names.
/// </param>
/// <param name="TargetFile">The file <paramref name="ResolvedProtoTarget"/> was declared in. A
/// <see cref="ProtoDeclaration"/> carries no path of its own and a reference resolves across the
/// import graph, so without this a resolved target cannot be navigated to.</param>
/// <param name="WellKnown">Set when the reference names one of protoc's own types, whose C# lives
/// in the <c>Google.Protobuf</c> runtime and so has no generated file to open.</param>
/// <param name="Name">The token's own text.</param>
/// <param name="TargetPath">The imported file, for <see cref="ProtoHitKind.Import"/>.</param>
/// <remarks>
/// Both halves of the answer are carried, always. A proto author who invokes go-to-definition on
/// <c>widgets.Widget</c> expects the <c>message Widget</c>, and a C# author who got to the same
/// caret from generated code expects the class — neither is a wrong answer, and which one a
/// front-end should offer depends on the front-end. Resolving one and making the caller re-derive
/// the other would mean re-walking the import graph or the generated trees a second time.
/// </remarks>
internal sealed record ProtoHit(
    ProtoHitKind Kind,
    TextSpan Span,
    ProtoDeclaration? Declaration = null,
    ISymbol? Symbol = null,
    ProtoTypeRef? TypeRef = null,
    ProtoImport? Import = null,
    ProtoDeclaration? ResolvedProtoTarget = null,
    ProtoFile? TargetFile = null,
    ProtoWellKnownType? WellKnown = null,
    string? Name = null,
    string? TargetPath = null)
{
    /// <summary>Whether the caret is on a name that points at a declaration rather than on one
    /// that makes one.</summary>
    public bool IsReference =>
        Kind is ProtoHitKind.FieldType or ProtoHitKind.RpcRequestType
            or ProtoHitKind.RpcResponseType or ProtoHitKind.ExtendTarget;

    /// <summary>
    /// The declaration the caret is about: the one it names, or — for a reference — the one it
    /// points at. A caret on <c>Widget</c> in <c>repeated Widget widgets = 1;</c> and a caret on
    /// the <c>message Widget</c> it names are the same question.
    /// </summary>
    /// <remarks>
    /// Keyed on <see cref="Kind"/> and not on <see cref="Declaration"/>, which for a reference is
    /// the declaration it was <i>written in</i>: an rpc's request type carries the rpc, so reading
    /// the declaration directly would answer about the rpc for a caret on the message it names.
    /// Null for a reference that resolved to nothing, which is a real answer — the name is there
    /// and what it means is not.
    /// </remarks>
    public ProtoDeclaration? Target => IsReference ? ResolvedProtoTarget : Declaration;
}

/// <summary>
/// Maps a caret offset in a <c>.proto</c> to what it means — the proto counterpart of
/// <c>SymbolFinder.FindSymbolAtPositionAsync</c>, and the single entry point behind every
/// navigation feature in the pack.
/// </summary>
/// <remarks>
/// Classification runs outermost-construct first and declaration names last, because the file-level
/// statements each own a whole line and nothing else can be inside them, while a declaration's name
/// sits inside its own span and inside its parent's. Type references are tested ahead of
/// declaration names for one construct in particular: an <c>extend</c> block's name <i>is</i> its
/// target's name, occupying the same span, and only the reference reading of it is useful.
/// </remarks>
internal static class ProtoSymbolResolver
{
    public static ProtoHit? ResolveAt(ProtoProjectView document, int offset) =>
        ResolveAt(document.Parse, offset, document.CreateScope(), document.Index, document.ProjectDirectory);

    /// <summary>
    /// The parse-only form, for a <c>.proto</c> that belongs to no project or a caller that has
    /// neither a scope nor an index to give.
    /// </summary>
    /// <param name="projectDirectory">The proto root an <c>import</c> in this file is resolved
    /// against. <c>null</c> leaves <see cref="ProtoImportResolver"/> to find one from the file's
    /// own location, which is right for everything except a file linked in from outside the
    /// project that compiles it.</param>
    /// <remarks>
    /// Degrades rather than refuses: without a <paramref name="scope"/> a reference still reports
    /// its own kind and span but resolves to nothing, and without an <paramref name="index"/> every
    /// hit still names its declaration but binds no symbol. That is what lets syntax highlighting
    /// and the outline work in a file that has never been built.
    /// </remarks>
    public static ProtoHit? ResolveAt(
        ProtoFile file, int offset, ProtoScope? scope = null, ProtoGeneratedIndex? index = null,
        string? projectDirectory = null)
    {
        if (offset < 0 || offset > file.Text.Length)
            return null;

        if (Contains(file.SyntaxSpan, offset))
        {
            return new ProtoHit(ProtoHitKind.Syntax, file.SyntaxSpan,
                Name: file.Edition ?? (file.SyntaxLevel == ProtoSyntaxLevel.Proto3 ? "proto3" : "proto2"));
        }

        if (Contains(file.PackageSpan, offset))
            return new ProtoHit(ProtoHitKind.Package, file.PackageSpan, Name: file.Package);

        if (file.ImportAt(offset) is { } import)
        {
            // The path when the caret is on it, the whole statement otherwise. A caret on the
            // `import` keyword is still on the import — that is what ImportAt is for — but
            // reporting the path's span for it would hand every caller a highlight range that does
            // not contain the caret, and a document link that jumps the selection.
            return new ProtoHit(ProtoHitKind.Import,
                Contains(import.PathSpan, offset) ? import.PathSpan : import.Span,
                Import: import,
                Name: import.Path,
                TargetPath: ProtoImportResolver.Resolve(import.Path, file.FilePath, projectDirectory));
        }

        if (OptionAt(file, offset) is { } option)
            return new ProtoHit(ProtoHitKind.OptionName, option.NameSpan, Name: option.Name);

        if (file.TypeReferenceAt(offset) is { } reference)
            return ResolveReference(file, reference, offset, scope, index);

        if (file.DeclarationNamedAt(offset) is { } declaration)
            return ResolveDeclaration(declaration, index);

        return null;
    }

    // ---- Declarations -----------------------------------------------------------------------

    /// <summary>
    /// The hit for a caret on the word a declaration is named by, or <c>null</c> for a declaration
    /// that names nothing of its own.
    /// </summary>
    /// <remarks>
    /// Only an <c>extend</c> block reaches the null: its name <i>is</i> its target's name on the
    /// same span, so it is a type reference and is claimed as one before this runs. Answering some
    /// other kind for it would tell a caller the caret is on a declaration it does not declare.
    /// </remarks>
    private static ProtoHit? ResolveDeclaration(ProtoDeclaration declaration, ProtoGeneratedIndex? index)
    {
        var (kind, symbol) = declaration switch
        {
            ProtoMessage message => ((ProtoHitKind?)ProtoHitKind.MessageName, (ISymbol?)index?.TypeFor(message)),
            ProtoEnum @enum => (ProtoHitKind.EnumName, index?.TypeFor(@enum)),
            ProtoEnumValue value => (ProtoHitKind.EnumValueName, index?.MemberFor(value)),
            ProtoField field => (ProtoHitKind.FieldName, index?.PropertyFor(field)),
            ProtoOneof oneof => (ProtoHitKind.OneofName, CaseProperty(oneof, index)),

            // The static holder rather than the base: it is the class the whole service maps to,
            // and the base and the client both hang off it as nested types.
            ProtoService service => (ProtoHitKind.ServiceName,
                index?.ServiceTypeFor(service) ?? (ISymbol?)index?.ServiceBaseFor(service)),

            // The base's virtual method, because that is what an implementation overrides and what
            // a solution-wide search cascades from. The client's overloads reach it through
            // ProtoReferenceService rather than through the caret.
            ProtoRpc rpc => (ProtoHitKind.RpcName,
                index?.BaseMethodFor(rpc) ?? (ISymbol?)index?.ClientMethodFor(rpc)),

            _ => ((ProtoHitKind?)null, null),
        };

        return kind is null
            ? null
            : new ProtoHit(kind.Value, declaration.Name.Span,
                Declaration: declaration, Symbol: symbol, Name: declaration.Name.Value);
    }

    /// <summary>
    /// The <c>…Case</c> property a <c>oneof</c> generates.
    /// </summary>
    /// <remarks>
    /// The one binding derived from protoc's naming rules rather than read from an anchor in its
    /// output. A oneof leaves no anchor: it produces no descriptor index expression, no
    /// <c>…FieldNumber</c> constant and no <c>OriginalName</c> attribute — only a property and a
    /// nested enum whose names follow from its own. Getting it wrong costs the hover on a oneof
    /// name and nothing else, which is why predicting is acceptable here and nowhere else.
    /// </remarks>
    private static ISymbol? CaseProperty(ProtoOneof oneof, ProtoGeneratedIndex? index)
    {
        if (index is null || oneof.Parent is not ProtoMessage message)
            return null;

        return index.TypeFor(message)
            ?.GetMembers(ProtoNaming.OneofCasePropertyName(oneof))
            .FirstOrDefault();
    }

    // ---- References -------------------------------------------------------------------------

    private static ProtoHit ResolveReference(
        ProtoFile file, ProtoTypeRef reference, int offset,
        ProtoScope? scope, ProtoGeneratedIndex? index)
    {
        var containing = file.DeclarationAt(offset);

        var kind = containing switch
        {
            ProtoRpc rpc when ReferenceEquals(rpc.RequestType, reference) => ProtoHitKind.RpcRequestType,
            ProtoRpc rpc when ReferenceEquals(rpc.ResponseType, reference) => ProtoHitKind.RpcResponseType,
            ProtoExtend => ProtoHitKind.ExtendTarget,
            _ => ProtoHitKind.FieldType,
        };

        // A scalar names a built-in, not a declaration: there is nothing to resolve and nothing to
        // navigate to, but the hit is still reported so hover can name the wire type.
        var resolution = reference.IsScalar ? null : scope?.Resolve(reference, containing);

        ISymbol? symbol = resolution?.Declaration switch
        {
            ProtoMessage message => index?.TypeFor(message),
            ProtoEnum @enum => index?.TypeFor(@enum),
            _ => null,
        };

        return new ProtoHit(kind, reference.Span,
            Declaration: containing,
            Symbol: symbol,
            TypeRef: reference,
            ResolvedProtoTarget: resolution?.Declaration,
            TargetFile: resolution?.File,
            WellKnown: resolution?.WellKnown,
            Name: reference.Text);
    }

    // ---- Options ----------------------------------------------------------------------------

    /// <summary>
    /// The option whose name the caret is on.
    /// </summary>
    /// <remarks>
    /// Options are not declarations and so are absent from <see cref="ProtoFile.AllDeclarations"/>;
    /// they hang off whichever construct carries them. The walk is a linear scan of a set that is
    /// tiny in every real file — a handful per file, rarely more than one per field.
    /// </remarks>
    private static ProtoOption? OptionAt(ProtoFile file, int offset)
    {
        foreach (var option in file.Options)
        {
            if (Contains(option.NameSpan, offset))
                return option;
        }

        foreach (var declaration in file.AllDeclarations)
        {
            if (offset < declaration.Span.Start)
                break;

            if (offset > declaration.Span.End)
                continue;

            var options = declaration switch
            {
                ProtoMessage message => message.Options,
                ProtoField field => field.Options,
                ProtoEnum @enum => @enum.Options,
                ProtoEnumValue value => value.Options,
                ProtoService service => service.Options,
                ProtoRpc rpc => rpc.Options,
                ProtoOneof oneof => oneof.Options,
                _ => [],
            };

            foreach (var option in options)
            {
                if (Contains(option.NameSpan, offset))
                    return option;
            }
        }

        return null;
    }

    // ---- Helpers ----------------------------------------------------------------------------

    /// <summary>
    /// End-inclusive, because the caret sits between characters: with the caret just past the last
    /// character of a message's name the user is still on that name.
    /// </summary>
    /// <remarks>
    /// A default span is never a hit. <see cref="ProtoFile.SyntaxSpan"/> and
    /// <see cref="ProtoFile.PackageSpan"/> are both default when the file declares neither, and an
    /// empty span at offset 0 would otherwise claim the caret at the top of every such file.
    /// </remarks>
    private static bool Contains(TextSpan span, int offset) =>
        !span.IsEmpty && offset >= span.Start && offset <= span.End;
}
