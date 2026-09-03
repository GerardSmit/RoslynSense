using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

// textDocument/selectionRange

public sealed record SelectionRangeParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("positions")] Position[] Positions);

/// <summary>
/// One step of the expand-selection chain. <see cref="Parent"/> is the next wider range, so the
/// client walks the chain outward on each keypress and back inward on shrink.
/// </summary>
public sealed record SelectionRange(
    [property: JsonPropertyName("range")] Range Range,
    [property: JsonPropertyName("parent")] SelectionRange? Parent);

// textDocument/linkedEditingRange

public sealed record LinkedEditingRanges(
    [property: JsonPropertyName("ranges")] Range[] Ranges,
    [property: JsonPropertyName("wordPattern")] string? WordPattern = null);

// textDocument/inlineValue

public sealed record InlineValueParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("range")] Range Range,
    [property: JsonPropertyName("context")] InlineValueContext Context);

/// <summary>
/// Where the debugger is stopped. Only values in scope at <see cref="StoppedLocation"/> mean
/// anything, so the handler uses it to bound which part of the document it reports on.
/// </summary>
public sealed record InlineValueContext(
    [property: JsonPropertyName("frameId")] int FrameId,
    [property: JsonPropertyName("stoppedLocation")] Range StoppedLocation);

/// <summary>
/// Asks the client to look the name up in the debugger's variable scopes. Cheaper and safer
/// than evaluating: no expression is executed in the debuggee.
/// </summary>
public sealed record InlineValueVariableLookup(
    [property: JsonPropertyName("range")] Range Range,
    [property: JsonPropertyName("variableName")] string? VariableName,
    [property: JsonPropertyName("caseSensitiveLookup")] bool CaseSensitiveLookup = true);

/// <summary>
/// Asks the client to evaluate the expression in the stopped frame. Used for member access
/// (<c>this.total</c>, <c>order.Lines</c>), which a scope lookup by name cannot resolve.
/// </summary>
public sealed record InlineValueEvaluatableExpression(
    [property: JsonPropertyName("range")] Range Range,
    [property: JsonPropertyName("expression")] string? Expression);

// workspace/didCreateFiles, workspace/didDeleteFiles

public sealed record FileCreate(
    [property: JsonPropertyName("uri")] string Uri);

public sealed record CreateFilesParams(
    [property: JsonPropertyName("files")] FileCreate[] Files);

public sealed record FileDelete(
    [property: JsonPropertyName("uri")] string Uri);

public sealed record DeleteFilesParams(
    [property: JsonPropertyName("files")] FileDelete[] Files);
