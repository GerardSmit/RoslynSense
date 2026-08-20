using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

public sealed record InlayHintParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("range")] Range Range);

public sealed record InlayHint(
    [property: JsonPropertyName("position")] Position Position,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("kind")] int? Kind, // 1 type, 2 parameter
    [property: JsonPropertyName("paddingLeft")] bool PaddingLeft,
    [property: JsonPropertyName("paddingRight")] bool PaddingRight,
    /// <summary>
    /// What double-clicking the hint writes into the buffer — the inferred type replacing a
    /// <c>var</c>, or the parameter name written out as a named argument.
    /// </summary>
    /// <remarks>
    /// Trailing and optional so the ~dozen positional constructions elsewhere keep compiling, and
    /// serialized away when unset. Sent eagerly rather than through <c>inlayHint/resolve</c>
    /// because Roslyn's own hint already carries the change: withholding it would mean a resolve
    /// round trip to hand back data that was in hand when the hint was built.
    /// </remarks>
    [property: JsonPropertyName("textEdits")] TextEdit[]? TextEdits = null);

public sealed record InlayHintOptions(
    [property: JsonPropertyName("resolveProvider")] bool ResolveProvider);
