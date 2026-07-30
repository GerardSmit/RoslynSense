using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

public sealed record ReferenceParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("position")] Position Position,
    [property: JsonPropertyName("context")] ReferenceContext Context);

public sealed record ReferenceContext(
    [property: JsonPropertyName("includeDeclaration")] bool IncludeDeclaration);

public sealed record Hover(
    [property: JsonPropertyName("contents")] MarkupContent Contents,
    [property: JsonPropertyName("range")] Range? Range);

public sealed record DocumentSymbolParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument);

/// <summary>Hierarchical documentSymbol result (the modern form; clients we target all
/// support <c>hierarchicalDocumentSymbolSupport</c>).</summary>
public sealed record DocumentSymbol(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("kind")] int Kind,
    [property: JsonPropertyName("range")] Range Range,
    [property: JsonPropertyName("selectionRange")] Range SelectionRange,
    [property: JsonPropertyName("children")] DocumentSymbol[] Children);

public sealed record WorkspaceSymbolParams(
    [property: JsonPropertyName("query")] string Query);

public sealed record SymbolInformation(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] int Kind,
    [property: JsonPropertyName("location")] Location Location,
    [property: JsonPropertyName("containerName")] string? ContainerName);

public sealed record DocumentHighlight(
    [property: JsonPropertyName("range")] Range Range,
    [property: JsonPropertyName("kind")] int Kind); // 1 text, 2 read, 3 write

/// <summary>LSP SymbolKind constants (the protocol enum, 1-based).</summary>
public static class LspSymbolKind
{
    public const int File = 1;
    public const int Module = 2;
    public const int Namespace = 3;
    public const int Package = 4;
    public const int Class = 5;
    public const int Method = 6;
    public const int Property = 7;
    public const int Field = 8;
    public const int Constructor = 9;
    public const int Enum = 10;
    public const int Interface = 11;
    public const int Function = 12;
    public const int Variable = 13;
    public const int Constant = 14;
    public const int String = 15;
    public const int Number = 16;
    public const int Boolean = 17;
    public const int Array = 18;
    public const int Object = 19;
    public const int Key = 20;
    public const int Null = 21;
    public const int EnumMember = 22;
    public const int Struct = 23;
    public const int Event = 24;
    public const int Operator = 25;
    public const int TypeParameter = 26;
}
