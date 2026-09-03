using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Lsp;

/// <summary>URI/path and position/range conversions between LSP and Roslyn. The server
/// advertises <c>positionEncoding: utf-16</c>, which matches Roslyn's <see cref="LinePosition"/>
/// character offsets exactly — no re-encoding anywhere.</summary>
internal static class LspConverters
{
    public static string PathToUri(string path) => new Uri(PathHelper.NormalizePath(path)).AbsoluteUri;

    /// <summary>
    /// A URI the client sent, in the exact spelling this server produces for the same file.
    /// </summary>
    /// <remarks>
    /// Only for comparing a client's URI against one of ours as a string — never for anything that
    /// then goes back out, which should carry the client's own spelling. A file URI has many legal
    /// spellings of one path: VS Code percent-encodes the drive colon, its <c>skipEncoding</c>
    /// serialisation leaves a space in a file name raw where <see cref="Uri.AbsoluteUri"/> writes
    /// <c>%20</c>, and the drive letter's case is free. Comparing the raw strings therefore reports
    /// "different file" for a file whose name merely contains a space — which the workspace sweep
    /// read as "the client is holding no result for this file", so it re-sent that file in full on
    /// every pass for the life of the session. Routing both sides through the path collapses all of
    /// those spellings onto one.
    /// </remarks>
    public static string NormalizeUri(string uri)
    {
        if (IsVirtual(uri))
            return uri;

        try
        {
            return PathToUri(UriToPath(uri));
        }
        catch (UriFormatException)
        {
            return uri;
        }
        catch (ArgumentException)
        {
            return uri;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException)
        {
            return uri;
        }
    }

    /// <summary>
    /// The file behind a document URI — or the URI itself, when there is no file.
    /// </summary>
    /// <remarks>
    /// A source-generated document exists only inside the compilation, and its URI carries the
    /// project and hint name in a shape <c>Uri.LocalPath</c> would throw away. Passing it
    /// through unchanged lets <see cref="LspDocumentResolver"/> recognise it, which is what
    /// gives generated files the same language features as any other.
    /// </remarks>
    public static string UriToPath(string uri) =>
        IsVirtual(uri) ? uri : PathHelper.NormalizePath(LocalPathOf(uri));

    /// <summary>
    /// The file a <c>file:</c> URI names, including when the drive-letter colon is percent-encoded.
    /// </summary>
    /// <remarks>
    /// <c>file:///c%3A/src/Program.cs</c> is how VS Code serialises a Windows path by default, and
    /// it is a perfectly ordinary URI — but <see cref="Uri.LocalPath"/> looks for the drive letter
    /// before unescaping, does not find one, and answers <c>/c:/src/Program.cs</c>: still rooted at
    /// <c>/</c>, so <c>Path.GetFullPath</c> then reads it as relative to the current drive and
    /// produces <c>D:\c:\src\Program.cs</c>. A path under no project, no solution and no directory
    /// that exists — which is why the Solution Explorer's "Focus Current File" could not find any
    /// file in any solution. The extension's own <c>code2Protocol</c> converter avoids the shape
    /// for the requests that go through it, but the server has to read what the protocol allows,
    /// not only what one client happens to send.
    /// </remarks>
    private static string LocalPathOf(string uri)
    {
        string local = new Uri(uri).LocalPath;

        // VS Code can send a Windows file URI to a server-side normalization path while tests
        // and remote tooling run on another OS. Preserve its drive syntax before GetFullPath.
        return local.Length >= 3
               && local[0] == '/'
               && char.IsAsciiLetter(local[1])
               && local[2] == ':'
            ? local[1..].Replace('/', '\\')
            : local;
    }

    private static bool IsVirtual(string uri) =>
        uri.StartsWith(Handlers.VirtualDocumentHandler.GeneratedScheme + ":", StringComparison.Ordinal)
        || uri.StartsWith(Handlers.VirtualDocumentHandler.MetadataScheme + ":", StringComparison.Ordinal);

    public static Position ToPosition(LinePosition p) => new(p.Line, p.Character);

    public static LinePosition ToLinePosition(Position p) => new(p.Line, p.Character);

    public static Protocol.Range ToRange(LinePositionSpan span) =>
        new(ToPosition(span.Start), ToPosition(span.End));

    public static Protocol.Range ToRange(TextLineCollection lines, TextSpan span) =>
        ToRange(lines.GetLinePositionSpan(span));

    public static TextSpan ToTextSpan(SourceText text, Protocol.Range range) =>
        text.Lines.GetTextSpan(new LinePositionSpan(
            ToLinePosition(range.Start), ToLinePosition(range.End)));

    /// <summary>
    /// <inheritdoc cref="ToTextSpan"/> Returns <see langword="null"/> for a range the text cannot
    /// hold instead of throwing.
    /// </summary>
    /// <remarks>
    /// For the didChange path. <c>GetTextSpan</c> clamps nothing, so a range past the end of the
    /// buffer is an <see cref="ArgumentOutOfRangeException"/> — thrown inside a JSON-RPC
    /// notification handler, where StreamJsonRpc has nowhere to send it and swallows it. The edit
    /// is then silently dropped and the server's mirror of the document diverges from the editor's
    /// permanently, because didSave carries no text to resynchronize from. Answering null lets the
    /// caller say so and drop the document rather than keep serving answers about text that exists
    /// nowhere.
    /// </remarks>
    public static TextSpan? TryToTextSpan(SourceText text, Protocol.Range range)
    {
        try
        {
            return ToTextSpan(text, range);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public static int ToOffset(SourceText text, Position position) =>
        text.Lines.GetPosition(ToLinePosition(position));

    public static LspLocation? ToLocation(Microsoft.CodeAnalysis.Location location)
    {
        if (!location.IsInSource || location.SourceTree?.FilePath is not { Length: > 0 } path)
            return null;

        // Generated code has a synthetic path; converting it to a file URI produces a link to
        // a file that does not exist. The registry knows the URI that does open it.
        if (GeneratedDocumentRegistry.TryGetUri(path, out string generated))
            return new LspLocation(generated, ToRange(location.GetLineSpan().Span));

        // An unregistered synthetic path would become a broken file:// link; dropping it keeps
        // a results list honest rather than filling it with entries that open nothing.
        if (GeneratedDocumentRegistry.LooksGenerated(path))
            return null;

        return new LspLocation(PathToUri(path), ToRange(location.GetLineSpan().Span));
    }

    public static int ToLspSymbolKind(ISymbol symbol) => symbol switch
    {
        INamespaceSymbol => LspSymbolKind.Namespace,
        ITypeParameterSymbol => LspSymbolKind.TypeParameter,
        ITypeSymbol t => t.TypeKind switch
        {
            TypeKind.Interface => LspSymbolKind.Interface,
            TypeKind.Struct => LspSymbolKind.Struct,
            TypeKind.Enum => LspSymbolKind.Enum,
            TypeKind.Delegate => LspSymbolKind.Function,
            _ => LspSymbolKind.Class,
        },
        IMethodSymbol m => m.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor
            ? LspSymbolKind.Constructor
            : LspSymbolKind.Method,
        IPropertySymbol => LspSymbolKind.Property,
        IFieldSymbol f => f.ContainingType?.TypeKind == TypeKind.Enum
            ? LspSymbolKind.EnumMember
            : f.IsConst ? LspSymbolKind.Constant : LspSymbolKind.Field,
        IEventSymbol => LspSymbolKind.Event,
        IParameterSymbol or ILocalSymbol => LspSymbolKind.Variable,
        _ => LspSymbolKind.Object,
    };

    public static int ToLspSeverity(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Error => 1,
        DiagnosticSeverity.Warning => 2,
        DiagnosticSeverity.Info => 3,
        _ => 4,
    };
}
