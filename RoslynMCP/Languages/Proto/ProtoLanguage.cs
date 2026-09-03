using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.Proto;

/// <summary>
/// Protocol Buffers — <c>.proto</c> — as one pack owning both front-ends: the LSP features the
/// editor asks for and the MCP tools an AI session calls.
/// </summary>
/// <remarks>
/// Split across partial files by feature, so the pack can grow a provider at a time without every
/// change landing in the same file. Each part forwards into <c>Proto/Core</c> and
/// <c>Proto/Lsp</c>; nothing decides anything here.
/// </remarks>
internal sealed partial class ProtoLanguage : ILanguagePack
{
    public ProtoLanguage(IOutputFormatter formatter) => InitializeToolHandlers(formatter);

    public string Id => "proto";

    public string DisplayName => "Protocol Buffers";

    public ImmutableArray<string> FileExtensions { get; } = [".proto"];

    /// <summary>
    /// Where a proto name can begin. <c>.</c> continues a dotted reference — a package-qualified
    /// type, and the leading dot that forces resolution from the root rather than the innermost
    /// scope. A space is what ends a field's label and its type, so it is what opens the type
    /// position and then the name position on a line that is still being written. <c>"</c> and
    /// <c>/</c> are the import path and nothing else: the quote opens it, and every segment after
    /// that is a directory under the proto root.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No signature help. Nothing in the grammar takes an argument list — an <c>rpc</c> names one
    /// request type and one response type — so there is no arity or parameter position to report,
    /// and advertising the capability would only make the editor ask.
    /// </para>
    /// <para>
    /// No file-operation globs either, because the pack implements no
    /// <see cref="ILanguageFileOperationProvider"/> to answer them. Renaming a <c>.proto</c> should
    /// rewrite the <c>import</c> in every file that names it — the path is relative to a proto root
    /// and every importer breaks silently until the next build — but nothing here does that yet,
    /// and a glob registers the client to send notifications no one acts on.
    /// </para>
    /// </remarks>
    public LanguageCapabilities Capabilities { get; } = new(
        CompletionTriggerCharacters: [".", " ", "\"", "/"],
        SignatureHelpTriggerCharacters: [],
        Commands: [],
        FileOperationGlobs: [],
        SemanticTokenTypes: [.. SemanticTokenTypeNames],
        SemanticTokenModifiers: [],
        SupportsBreakpoints: false);

    /// <summary>
    /// The two runtime base types every generated file needs: a message implements
    /// <c>IMessage</c>, and a service's client derives from <c>ClientBase</c>. Neither resolving
    /// means the project references neither runtime, so protoc generated nothing into it and the
    /// contributors can decline before touching the file system.
    /// </summary>
    /// <remarks>
    /// Both, not one. A contracts assembly that carries only messages never references gRPC, and a
    /// project generating service stubs from a <c>.proto</c> with no <c>message</c> in it is rare
    /// but legal; requiring both would drop the first and requiring only <c>IMessage</c> is a
    /// weaker gate than it looks, since <c>Google.Protobuf</c> flows transitively to anything
    /// consuming the contracts.
    /// </remarks>
    public ImmutableArray<string> WellKnownTypeNames { get; } =
        ["Google.Protobuf.IMessage", "Grpc.Core.ClientBase"];

    /// <summary>
    /// What a proto declaration becomes in C#: a message or a service is a type, an <c>rpc</c> is
    /// a method on the base and the client, a field is a property, and an enum value is a field.
    /// A symbol of any other kind has no declaration in a <c>.proto</c> to correspond to.
    /// </summary>
    public ImmutableArray<SymbolKind> InterestingSymbolKinds { get; } =
        [SymbolKind.NamedType, SymbolKind.Method, SymbolKind.Property, SymbolKind.Field];

    /// <summary>
    /// Never. Grpc.Tools writes real <c>.cs</c> into <c>obj</c> and MSBuild hands them to Roslyn as
    /// ordinary <c>Compile</c> items, so the C# behind a <c>.proto</c> is already in the
    /// compilation and the pack has nothing to project. Every generated document is a document the
    /// C# handlers should answer about, which is exactly what returning <c>false</c> arranges.
    /// </summary>
    public bool IsProjectionPath(string? filePath) => false;
}
