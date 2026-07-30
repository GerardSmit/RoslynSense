using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

public sealed record CompletionParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("position")] Position Position);

public sealed record CompletionList(
    [property: JsonPropertyName("isIncomplete")] bool IsIncomplete,
    [property: JsonPropertyName("items")] CompletionItem[] Items);

public sealed record CompletionItem(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("kind")] int Kind,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("sortText")] string? SortText,
    [property: JsonPropertyName("filterText")] string? FilterText,
    [property: JsonPropertyName("textEdit")] TextEdit? TextEdit);

/// <summary>LSP CompletionItemKind constants (1-based protocol enum).</summary>
public static class LspCompletionItemKind
{
    public const int Text = 1;
    public const int Method = 2;
    public const int Function = 3;
    public const int Constructor = 4;
    public const int Field = 5;
    public const int Variable = 6;
    public const int Class = 7;
    public const int Interface = 8;
    public const int Module = 9;
    public const int Property = 10;
    public const int Unit = 11;
    public const int Value = 12;
    public const int Enum = 13;
    public const int Keyword = 14;
    public const int Snippet = 15;
    public const int Color = 16;
    public const int File = 17;
    public const int Reference = 18;
    public const int Folder = 19;
    public const int EnumMember = 20;
    public const int Constant = 21;
    public const int Struct = 22;
    public const int Event = 23;
    public const int Operator = 24;
    public const int TypeParameter = 25;
}

public sealed record CodeActionParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("range")] Range Range,
    [property: JsonPropertyName("context")] CodeActionContext Context);

public sealed record CodeActionContext(
    [property: JsonPropertyName("diagnostics")] Diagnostic[] Diagnostics);

public sealed record CodeAction(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("kind")] string Kind, // "quickfix" | "refactor" | ...
    [property: JsonPropertyName("edit")] WorkspaceEdit? Edit);

public sealed record DocumentFormattingParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("options")] FormattingOptions Options);

public sealed record FormattingOptions(
    [property: JsonPropertyName("tabSize")] int TabSize,
    [property: JsonPropertyName("insertSpaces")] bool InsertSpaces);
