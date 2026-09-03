using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

public sealed record CompletionParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("position")] Position Position,
    [property: JsonPropertyName("context")] LspCompletionContext? Context = null);

public sealed record LspCompletionContext(
    [property: JsonPropertyName("triggerKind")] int TriggerKind, // 1 invoked, 2 trigger char, 3 re-trigger
    [property: JsonPropertyName("triggerCharacter")] string? TriggerCharacter);

public sealed record CompletionList(
    [property: JsonPropertyName("isIncomplete")] bool IsIncomplete,
    [property: JsonPropertyName("items")] CompletionItem[] Items)
{
    /// <summary>Fields every item in <see cref="Items"/> would otherwise repeat, sent once
    /// (LSP 3.17). Only filled when the client asked for it in
    /// <c>textDocument.completion.completionList.itemDefaults</c> — see
    /// <see cref="RoslynMCP.Lsp.LspClientState.CompletionEditRangeDefault"/>. A client that did
    /// not ask never sees this member and keeps getting a per-item <c>textEdit</c>.</summary>
    [JsonPropertyName("itemDefaults")] public CompletionItemDefaults? ItemDefaults { get; init; }
}

/// <summary>
/// The per-list defaults an item may leave out. An item that sets the same field itself wins;
/// the default only applies where the item is silent.
/// </summary>
public sealed record CompletionItemDefaults
{
    /// <summary>The span a committed item replaces. With this set, an item needs no
    /// <c>textEdit</c> at all: the client pairs this range with the item's
    /// <see cref="CompletionItem.TextEditText"/>, or with its label when that is absent too.</summary>
    [JsonPropertyName("editRange")] public Range? EditRange { get; init; }

    [JsonPropertyName("commitCharacters")] public string[]? CommitCharacters { get; init; }

    [JsonPropertyName("insertTextFormat")] public int? InsertTextFormat { get; init; }

    /// <summary>Resolve payload shared by every item in the list. Left null here: the resolve
    /// key is a per-item index into the cached Roslyn list, so there is nothing to share.</summary>
    [JsonPropertyName("data")] public CompletionItemData? Data { get; init; }
}

public sealed record CompletionItem(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("kind")] int Kind,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("sortText")] string? SortText,
    [property: JsonPropertyName("filterText")] string? FilterText,
    [property: JsonPropertyName("textEdit")] TextEdit? TextEdit)
{
    /// <summary>Server-defined resolve payload: round-tripped by the client into
    /// completionItem/resolve. Holds a cache generation + item index.</summary>
    [JsonPropertyName("data")] public CompletionItemData? Data { get; init; }

    /// <summary>What to insert over <see cref="CompletionItemDefaults.EditRange"/> when this item
    /// has no <see cref="TextEdit"/> of its own — the label is used when this is null too. Only
    /// worth sending for the items whose commit text differs from what they display (a generic
    /// type shows <c>List&lt;&gt;</c> and commits <c>List</c>), which is what makes dropping the
    /// per-item edit safe.</summary>
    [JsonPropertyName("textEditText")] public string? TextEditText { get; init; }

    [JsonPropertyName("documentation")] public MarkupContent? Documentation { get; init; }

    /// <summary>Extra edits away from the main edit — e.g. the auto-inserted using directive
    /// for import completion. Filled lazily in completionItem/resolve.</summary>
    [JsonPropertyName("additionalTextEdits")] public TextEdit[]? AdditionalTextEdits { get; init; }

    [JsonPropertyName("preselect")] public bool? Preselect { get; init; }

    /// <summary>Runs after the item is inserted. Carries the accept signal that feeds
    /// completion usage statistics — the client forwards it back as workspace/executeCommand.</summary>
    [JsonPropertyName("command")] public Command? Command { get; init; }

    [JsonPropertyName("commitCharacters")] public string[]? CommitCharacters { get; init; }

    /// <summary>1 = plain text, 2 = snippet. Snippets carry tab stops, which is what puts the
    /// caret inside a generated override body instead of after it.</summary>
    [JsonPropertyName("insertTextFormat")] public int? InsertTextFormat { get; init; }
}

public static class LspInsertTextFormat
{
    public const int PlainText = 1;
    public const int Snippet = 2;
}

public sealed record CompletionItemData(
    [property: JsonPropertyName("cacheId")] long CacheId,
    [property: JsonPropertyName("index")] int Index);

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
    [property: JsonPropertyName("edit")] WorkspaceEdit? Edit)
{
    /// <summary>Server-defined resolve payload: id of the cached Roslyn action whose edit is
    /// computed lazily in codeAction/resolve.</summary>
    [JsonPropertyName("data")] public CodeActionData? Data { get; init; }

    /// <summary>Runs after <see cref="Edit"/> is applied. Carries the part of the fix that is
    /// too expensive to compute while merely listing actions, or that reaches a file the edit
    /// cannot address.</summary>
    [JsonPropertyName("command")] public Command? Command { get; init; }
}

public sealed record CodeActionData(
    [property: JsonPropertyName("id")] long Id);

/// <summary>
/// One node of a code-action group, sent as the argument of the client-side picker command.
/// A node has either <paramref name="Children"/> (another level to choose from) or an
/// <paramref name="Id"/> (a leaf, resolvable through <c>codeAction/resolve</c>), never both.
/// </summary>
public sealed record NestedCodeActionGroup(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("children")] NestedCodeActionGroup[]? Children);

public sealed record CodeActionOptions(
    [property: JsonPropertyName("resolveProvider")] bool ResolveProvider);

public sealed record DocumentFormattingParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("options")] FormattingOptions Options);

public sealed record FormattingOptions(
    [property: JsonPropertyName("tabSize")] int TabSize,
    [property: JsonPropertyName("insertSpaces")] bool InsertSpaces);
