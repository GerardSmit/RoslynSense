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

    public static string UriToPath(string uri) => PathHelper.NormalizePath(new Uri(uri).LocalPath);

    public static Position ToPosition(LinePosition p) => new(p.Line, p.Character);

    public static LinePosition ToLinePosition(Position p) => new(p.Line, p.Character);

    public static Protocol.Range ToRange(LinePositionSpan span) =>
        new(ToPosition(span.Start), ToPosition(span.End));

    public static Protocol.Range ToRange(TextLineCollection lines, TextSpan span) =>
        ToRange(lines.GetLinePositionSpan(span));

    public static TextSpan ToTextSpan(SourceText text, Protocol.Range range) =>
        text.Lines.GetTextSpan(new LinePositionSpan(
            ToLinePosition(range.Start), ToLinePosition(range.End)));

    public static int ToOffset(SourceText text, Position position) =>
        text.Lines.GetPosition(ToLinePosition(position));

    public static LspLocation? ToLocation(Microsoft.CodeAnalysis.Location location)
    {
        if (!location.IsInSource || location.SourceTree?.FilePath is not { Length: > 0 } path)
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
