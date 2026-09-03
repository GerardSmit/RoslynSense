using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMCP.Languages.Proto.Core;

/// <summary>An identifier as it was written, with the span it occupies.</summary>
/// <remarks>
/// The span covers the identifier alone and never the statement around it, because every editor
/// feature that addresses a declaration addresses it through this span: semantic tokens colour it,
/// rename replaces it, go-to-definition parks the caret on it, and the outline highlights it.
/// Widening it to the statement would point all four at punctuation.
/// </remarks>
internal readonly record struct ProtoName(string Value, TextSpan Span);

/// <summary>Which dialect the file declared.</summary>
internal enum ProtoSyntaxLevel
{
    /// <summary><c>syntax = "proto2"</c>, or no syntax statement at all — protoc assumes proto2
    /// when the statement is missing, so this parser assumes it too.</summary>
    Proto2,

    /// <summary><c>syntax = "proto3"</c>.</summary>
    Proto3,

    /// <summary><c>edition = "2023"</c> and later. The edition string itself is kept on
    /// <see cref="ProtoFile.Edition"/>.</summary>
    Edition,
}

/// <summary>What a <see cref="ProtoDeclaration"/> declares.</summary>
internal enum ProtoDeclarationKind
{
    Message,
    Field,
    Oneof,
    Enum,
    EnumValue,
    Service,
    Rpc,
    Extend,
}

/// <summary>How a field was labelled. <see cref="None"/> is the proto3 default.</summary>
internal enum ProtoFieldLabel
{
    None,
    Optional,
    Required,
    Repeated,
}

/// <summary>The 15 built-in scalar types, plus <see cref="None"/> for a named type.</summary>
internal enum ProtoScalarKind
{
    None,
    Double,
    Float,
    Int32,
    Int64,
    UInt32,
    UInt64,
    SInt32,
    SInt64,
    Fixed32,
    Fixed64,
    SFixed32,
    SFixed64,
    Bool,
    String,
    Bytes,
}

/// <summary>How severe a <see cref="ProtoParseDiagnostic"/> is.</summary>
internal enum ProtoDiagnosticSeverity
{
    Error,
    Warning,
    Information,
}

/// <summary>The identifiers the parser and the lexer report problems under.</summary>
/// <remarks>
/// Stable strings rather than an enum: they surface in the editor's problems list beside Roslyn's
/// own <c>CS….</c> codes, and a user who suppresses one expects it to stay suppressed.
/// </remarks>
internal static class ProtoDiagnosticIds
{
    public const string UnexpectedToken = "PROTO001";
    public const string IdentifierExpected = "PROTO002";
    public const string TokenExpected = "PROTO003";
    public const string UnterminatedString = "PROTO004";
    public const string UnterminatedComment = "PROTO005";
    public const string InvalidNumber = "PROTO006";
    public const string UnclosedBrace = "PROTO007";
    public const string MissingSyntax = "PROTO008";
    public const string UnknownSyntax = "PROTO009";
    public const string NotModelled = "PROTO010";
}

/// <summary>Maps a scalar type's proto name to its <see cref="ProtoScalarKind"/>.</summary>
internal static class ProtoScalars
{
    /// <summary>
    /// <see cref="ProtoScalarKind.None"/> for anything that is not one of the 15 built-ins,
    /// including a leading-dot name — <c>.int32</c> is a fully-qualified reference to a message
    /// someone unwisely called <c>int32</c>, not the scalar.
    /// </summary>
    public static ProtoScalarKind FromName(string name) => name switch
    {
        "double" => ProtoScalarKind.Double,
        "float" => ProtoScalarKind.Float,
        "int32" => ProtoScalarKind.Int32,
        "int64" => ProtoScalarKind.Int64,
        "uint32" => ProtoScalarKind.UInt32,
        "uint64" => ProtoScalarKind.UInt64,
        "sint32" => ProtoScalarKind.SInt32,
        "sint64" => ProtoScalarKind.SInt64,
        "fixed32" => ProtoScalarKind.Fixed32,
        "fixed64" => ProtoScalarKind.Fixed64,
        "sfixed32" => ProtoScalarKind.SFixed32,
        "sfixed64" => ProtoScalarKind.SFixed64,
        "bool" => ProtoScalarKind.Bool,
        "string" => ProtoScalarKind.String,
        "bytes" => ProtoScalarKind.Bytes,
        _ => ProtoScalarKind.None,
    };
}

/// <summary>A type named somewhere: a field's type, an rpc's request or response, a map's key or
/// value, or the target of an <c>extend</c>.</summary>
/// <param name="Text">The name exactly as written, leading dot included.</param>
/// <param name="Span">The dotted name and nothing else — not the field around it.</param>
/// <param name="Scalar">Which built-in this is, or <see cref="ProtoScalarKind.None"/>.</param>
/// <remarks>
/// Resolution is deliberately not attempted here. Proto name lookup walks outward through the
/// enclosing scopes and then through the transitive imports, which needs every file in the import
/// graph — knowledge the parser of a single file does not have and must not pretend to.
/// </remarks>
internal sealed record ProtoTypeRef(string Text, TextSpan Span, ProtoScalarKind Scalar)
{
    public bool IsScalar => Scalar != ProtoScalarKind.None;

    /// <summary>Whether the name is rooted, which skips relative lookup entirely.</summary>
    public bool IsFullyQualified => Text.StartsWith('.');
}

/// <summary>One <c>import</c> statement.</summary>
/// <param name="Path">The decoded path, always with forward slashes as protoc requires.</param>
/// <param name="PathSpan">The quoted literal, quotes included — what a document link underlines.</param>
/// <param name="Span">The whole statement, so a caret anywhere on the line still finds it.</param>
internal sealed record ProtoImport(
    string Path,
    TextSpan PathSpan,
    TextSpan Span,
    bool IsPublic,
    bool IsWeak);

/// <summary>One <c>option</c>, whether it is on the file, on a declaration, or in a field's
/// <c>[ … ]</c> list.</summary>
/// <param name="Name">The name as written, so a custom option keeps its parentheses —
/// <c>(my.custom.opt)</c>. Matching a known option is therefore an ordinary string compare
/// against the spelling protoc uses.</param>
/// <param name="Value">The literal's decoded text, or <c>null</c> when the value was missing or
/// unparseable. An aggregate value keeps its braces and its raw text.</param>
internal sealed record ProtoOption(
    string Name,
    TextSpan NameSpan,
    string? Value,
    TextSpan ValueSpan,
    TextSpan Span);

/// <summary>Something the parser could not make sense of, or understood but does not model.</summary>
internal sealed record ProtoParseDiagnostic(
    string Id,
    string Message,
    TextSpan Span,
    ProtoDiagnosticSeverity Severity);

/// <summary>One named thing declared in a <c>.proto</c> file.</summary>
/// <remarks>
/// <para>
/// <see cref="Parent"/>, <see cref="FullName"/> and <see cref="DeclarationIndex"/> are filled in
/// by a single walk once the whole file is parsed, because none of them can be known while the
/// declaration is being read: a message's index among its siblings depends on what follows it.
/// </para>
/// </remarks>
internal abstract class ProtoDeclaration
{
    protected ProtoDeclaration(ProtoName name, TextSpan span, string? documentation)
    {
        Name = name;
        Span = span;
        Documentation = documentation;
    }

    public ProtoName Name { get; }

    /// <summary>The whole declaration, from its first keyword to its closing brace or semicolon.
    /// This is what folding collapses and what selection-expansion grows to.</summary>
    public TextSpan Span { get; }

    /// <summary>The <c>{ … }</c> braces, or the default span for a declaration that has no body.
    /// A field, an enum value and a body-less rpc all have none.</summary>
    public TextSpan BodySpan { get; init; }

    public abstract ProtoDeclarationKind Kind { get; }

    /// <summary>
    /// The declaration this one is written inside, or <c>null</c> at file level. A field declared
    /// in a <c>oneof</c> points at the enclosing <b>message</b>, not at the oneof — that is where
    /// protobuf scopes it, and <see cref="ProtoField.Oneof"/> is how the oneof is reached.
    /// </summary>
    public ProtoDeclaration? Parent { get; internal set; }

    /// <summary>
    /// The fully-qualified proto name: package, enclosing scopes and this name, dot separated and
    /// with no leading dot. A file with no package yields an unprefixed name.
    /// </summary>
    /// <remarks>
    /// Enclosing <i>scopes</i>, which is not the same as enclosing declarations: an enum value and
    /// a oneof member are both named as if they were declared one level further out, because that
    /// is where protobuf scopes them. <c>Kind.K_A</c> inside message <c>Outer</c> is
    /// <c>Outer.K_A</c>, and it is why two enums in one message may not share a value name.
    /// </remarks>
    public string FullName { get; internal set; } = string.Empty;

    /// <summary>
    /// The 0-based position among siblings <b>of the same kind</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per-kind rather than overall, so that an enum written after a message still counts from zero
    /// among the enums. It is a position within a run of like declarations, which is what the two
    /// callers need: <see cref="ProtoGeneratedIndex"/> falls back to it to pick an enum whose member
    /// names fingerprinted nothing, and a field's index lines up with the property-name array in
    /// protoc's reflection descriptor because a <c>oneof</c>'s members keep counting through the
    /// message's fields rather than restarting.
    /// </para>
    /// <para>
    /// It is deliberately <b>not</b> the descriptor index, and nothing may use it as one: protoc
    /// inserts a synthetic <c>&lt;Field&gt;Entry</c> message per <c>map</c> field, which takes a slot
    /// in the parent's <c>NestedTypes</c> and generates no class. A message declaring one <c>map</c>
    /// and one nested message has that message at index 0 here and at <c>NestedTypes[1]</c> there.
    /// <see cref="ProtoGeneratedIndex"/> binds messages by rank among the classes protoc actually
    /// emitted — see its <c>BindMessages</c> — for exactly that reason.
    /// </para>
    /// </remarks>
    public int DeclarationIndex { get; internal set; }

    /// <summary>The comment block written immediately above the declaration, with its markers
    /// stripped, or <c>null</c> when there is none.</summary>
    public string? Documentation { get; }

    /// <summary>
    /// Every declaration written directly inside this one, in source order.
    /// </summary>
    /// <remarks>
    /// Source order rather than grouped by kind, because the flattened
    /// <see cref="ProtoFile.AllDeclarations"/> and every offset lookup built on it depend on it:
    /// a pre-order walk of source-ordered children has non-decreasing start offsets, which is what
    /// lets a lookup stop at the first declaration starting past the caret instead of scanning the
    /// whole file on every hover.
    /// </remarks>
    public ImmutableArray<ProtoDeclaration> ChildDeclarations { get; init; } = [];
}

/// <summary>A <c>message</c>, whether top-level or nested.</summary>
internal sealed class ProtoMessage(ProtoName name, TextSpan span, string? documentation)
    : ProtoDeclaration(name, span, documentation)
{
    public override ProtoDeclarationKind Kind => ProtoDeclarationKind.Message;

    /// <summary>The fields written directly in the body — the members of a <c>oneof</c> are on the
    /// oneof, and reachable together with these through <see cref="AllFields"/>.</summary>
    public ImmutableArray<ProtoField> Fields { get; init; } = [];

    public ImmutableArray<ProtoOneof> Oneofs { get; init; } = [];
    public ImmutableArray<ProtoMessage> Messages { get; init; } = [];
    public ImmutableArray<ProtoEnum> Enums { get; init; } = [];
    public ImmutableArray<ProtoExtend> Extends { get; init; } = [];
    public ImmutableArray<ProtoOption> Options { get; init; } = [];

    /// <summary>
    /// Every field of the message in declaration order, oneof members included.
    /// </summary>
    /// <remarks>
    /// This is the order protoc emits, and the reason the flattened view exists at all: the
    /// generated reflection descriptor lists a message's property names in one array with the
    /// oneof members inline — <c>new[]{ "Id", "Uuid", "Label", "Channel", "CreatedAt", "ImageUrl",
    /// "ImageHash" }</c> for a message whose last two fields are inside a oneof — so binding a
    /// proto field to its generated property is an index into this sequence. <see cref="Fields"/>
    /// alone would be off by however many oneof members precede.
    /// </remarks>
    public ImmutableArray<ProtoField> AllFields { get; init; } = [];
}

/// <summary>One field, in a message, in a <c>oneof</c>, or in an <c>extend</c> block.</summary>
internal sealed class ProtoField(ProtoName name, TextSpan span, string? documentation)
    : ProtoDeclaration(name, span, documentation)
{
    public override ProtoDeclarationKind Kind => ProtoDeclarationKind.Field;

    /// <summary>The wire number. This is the identity of a field: renaming it is safe, renumbering
    /// it is a breaking change, and it is what the generated <c>…FieldNumber</c> constant carries
    /// back to the proto.</summary>
    public int Number { get; init; }

    public TextSpan NumberSpan { get; init; }

    public ProtoFieldLabel Label { get; init; }

    /// <summary>The field's type. For a map this is the <b>value</b> type; the key is on
    /// <see cref="MapKeyType"/>.</summary>
    public required ProtoTypeRef Type { get; init; }

    /// <summary>The key type of a <c>map&lt;K, V&gt;</c>, and <c>null</c> for every other field.</summary>
    public ProtoTypeRef? MapKeyType { get; init; }

    public bool IsMap => MapKeyType is not null;

    /// <summary>The <c>oneof</c> this field belongs to, or <c>null</c> when it stands on its own.</summary>
    public ProtoOneof? Oneof { get; internal set; }

    public ImmutableArray<ProtoOption> Options { get; init; } = [];
}

/// <summary>A <c>oneof</c> group. It names no scope of its own: its fields are scoped to the
/// enclosing message, which is why their <see cref="ProtoDeclaration.FullName"/> skips it.</summary>
internal sealed class ProtoOneof(ProtoName name, TextSpan span, string? documentation)
    : ProtoDeclaration(name, span, documentation)
{
    public override ProtoDeclarationKind Kind => ProtoDeclarationKind.Oneof;

    public ImmutableArray<ProtoField> Fields { get; init; } = [];

    /// <summary>The <c>option</c> statements written in the body. Editions carry
    /// <c>features.*</c> here, and a oneof has no other place to say anything about itself, so
    /// dropping them would leave hover with nothing to show beyond the name.</summary>
    public ImmutableArray<ProtoOption> Options { get; init; } = [];
}

/// <summary>An <c>enum</c>, whether top-level or nested in a message.</summary>
internal sealed class ProtoEnum(ProtoName name, TextSpan span, string? documentation)
    : ProtoDeclaration(name, span, documentation)
{
    public override ProtoDeclarationKind Kind => ProtoDeclarationKind.Enum;

    public ImmutableArray<ProtoEnumValue> Values { get; init; } = [];
    public ImmutableArray<ProtoOption> Options { get; init; } = [];
}

/// <summary>One member of an <c>enum</c>. Its
/// <see cref="ProtoDeclaration.FullName"/> is scoped to the enum's parent, not to the enum.</summary>
internal sealed class ProtoEnumValue(ProtoName name, TextSpan span, string? documentation)
    : ProtoDeclaration(name, span, documentation)
{
    public override ProtoDeclarationKind Kind => ProtoDeclarationKind.EnumValue;

    /// <summary>May be negative: proto allows it, and the generated C# enum keeps the sign.</summary>
    public int Number { get; init; }

    public TextSpan NumberSpan { get; init; }

    public ImmutableArray<ProtoOption> Options { get; init; } = [];
}

/// <summary>A gRPC <c>service</c>.</summary>
internal sealed class ProtoService(ProtoName name, TextSpan span, string? documentation)
    : ProtoDeclaration(name, span, documentation)
{
    public override ProtoDeclarationKind Kind => ProtoDeclarationKind.Service;

    public ImmutableArray<ProtoRpc> Rpcs { get; init; } = [];
    public ImmutableArray<ProtoOption> Options { get; init; } = [];
}

/// <summary>One <c>rpc</c> in a service.</summary>
internal sealed class ProtoRpc(ProtoName name, TextSpan span, string? documentation)
    : ProtoDeclaration(name, span, documentation)
{
    public override ProtoDeclarationKind Kind => ProtoDeclarationKind.Rpc;

    public required ProtoTypeRef RequestType { get; init; }
    public required ProtoTypeRef ResponseType { get; init; }

    /// <summary><c>stream</c> on the request side.</summary>
    public bool ClientStreaming { get; init; }

    /// <summary><c>stream</c> on the response side.</summary>
    public bool ServerStreaming { get; init; }

    public ImmutableArray<ProtoOption> Options { get; init; } = [];
}

/// <summary>An <c>extend</c> block. Its <see cref="ProtoDeclaration.Name"/> is the target's name,
/// because the block itself declares none.</summary>
internal sealed class ProtoExtend(ProtoName name, TextSpan span, string? documentation)
    : ProtoDeclaration(name, span, documentation)
{
    public override ProtoDeclarationKind Kind => ProtoDeclarationKind.Extend;

    public required ProtoTypeRef Target { get; init; }
    public ImmutableArray<ProtoField> Fields { get; init; } = [];
}

/// <summary>One parsed <c>.proto</c> file.</summary>
/// <remarks>
/// A pure syntactic model. Nothing here is bound to a Roslyn symbol and nothing here reads another
/// file: a proto's meaning depends on its transitive imports, so resolution belongs to whatever
/// owns the import graph, and keeping it out of the parse is what makes the parse cheap enough to
/// run on every keystroke.
/// </remarks>
internal sealed class ProtoFile(string filePath, SourceText text)
{
    private Dictionary<string, ProtoDeclaration>? _byFullName;

    public string FilePath { get; } = filePath;

    public SourceText Text { get; } = text;

    public ProtoSyntaxLevel SyntaxLevel { get; init; }

    /// <summary>The edition string, when <see cref="SyntaxLevel"/> is
    /// <see cref="ProtoSyntaxLevel.Edition"/>.</summary>
    public string? Edition { get; init; }

    /// <summary>The <c>syntax</c> or <c>edition</c> statement, which is where a diagnostic about
    /// the dialect belongs. Default when the file declares neither.</summary>
    public TextSpan SyntaxSpan { get; init; }

    /// <summary>The declared package, or the empty string when the file declares none.</summary>
    public string Package { get; init; } = string.Empty;

    public TextSpan PackageSpan { get; init; }

    /// <summary>The <c>csharp_namespace</c> option. When absent, protoc derives the namespace from
    /// the package instead, which is a naming rule this model deliberately does not encode.</summary>
    public string? CSharpNamespace { get; init; }

    public ImmutableArray<ProtoImport> Imports { get; init; } = [];
    public ImmutableArray<ProtoOption> Options { get; init; } = [];

    /// <summary>Top-level messages in declaration order — the order <c>MessageTypes[N]</c> indexes.</summary>
    public ImmutableArray<ProtoMessage> Messages { get; init; } = [];

    public ImmutableArray<ProtoEnum> Enums { get; init; } = [];
    public ImmutableArray<ProtoService> Services { get; init; } = [];
    public ImmutableArray<ProtoExtend> Extends { get; init; } = [];
    public ImmutableArray<ProtoParseDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>Every type reference in the file, in source order.</summary>
    public ImmutableArray<ProtoTypeRef> TypeReferences { get; init; } = [];

    /// <summary>Every declaration in the file, flattened pre-order in declaration order.</summary>
    public ImmutableArray<ProtoDeclaration> AllDeclarations { get; init; } = [];

    /// <summary>
    /// The innermost declaration whose <see cref="ProtoDeclaration.Span"/> contains
    /// <paramref name="offset"/>, or <c>null</c> when the offset is between declarations.
    /// </summary>
    /// <remarks>
    /// End-inclusive, because a caret sits between characters: with the caret just past a field's
    /// semicolon the user is still on that field. The scan can stop at the first declaration that
    /// starts past the offset — a pre-order walk of source-ordered children has non-decreasing
    /// starts — and the last match wins because children always follow their parent.
    /// </remarks>
    public ProtoDeclaration? DeclarationAt(int offset)
    {
        ProtoDeclaration? innermost = null;

        foreach (var declaration in AllDeclarations)
        {
            if (offset < declaration.Span.Start)
                break;

            if (offset <= declaration.Span.End)
                innermost = declaration;
        }

        return innermost;
    }

    /// <summary>The declaration whose <see cref="ProtoDeclaration.Name"/> the offset is on, which
    /// is the question rename and go-to-definition ask — being anywhere inside a message is not
    /// the same as being on the word that names it.</summary>
    public ProtoDeclaration? DeclarationNamedAt(int offset)
    {
        foreach (var declaration in AllDeclarations)
        {
            if (offset < declaration.Span.Start)
                break;

            var name = declaration.Name.Span;
            if (offset >= name.Start && offset <= name.End)
                return declaration;
        }

        return null;
    }

    public ProtoTypeRef? TypeReferenceAt(int offset)
    {
        foreach (var reference in TypeReferences)
        {
            if (offset < reference.Span.Start)
                break;

            if (offset <= reference.Span.End)
                return reference;
        }

        return null;
    }

    /// <summary>The import statement the offset is on, path or keyword alike.</summary>
    public ProtoImport? ImportAt(int offset)
    {
        foreach (var import in Imports)
        {
            if (offset >= import.Span.Start && offset <= import.Span.End)
                return import;
        }

        return null;
    }

    /// <summary>
    /// The declaration with this fully-qualified proto name, or <c>null</c> when the file declares
    /// no such thing.
    /// </summary>
    /// <remarks>
    /// The lookup table is built on first use rather than during the parse: most parses answer no
    /// name lookups at all, and the ones that do — resolving an import's target, binding a service
    /// to its generated class — ask for many at once. <c>extend</c> blocks stay out of it: the name
    /// on one is the name of the thing it extends, and letting it in would shadow that thing.
    /// </remarks>
    public ProtoDeclaration? FindByFullName(string fullName)
    {
        var map = _byFullName;

        if (map is null)
        {
            map = new Dictionary<string, ProtoDeclaration>(AllDeclarations.Length, StringComparer.Ordinal);

            foreach (var declaration in AllDeclarations)
            {
                if (declaration.Kind != ProtoDeclarationKind.Extend)
                    map.TryAdd(declaration.FullName, declaration);
            }

            _byFullName = map;
        }

        return map.TryGetValue(fullName, out var found) ? found : null;
    }
}
