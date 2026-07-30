using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

public sealed record CodeLensParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument);

public sealed record CodeLens(
    [property: JsonPropertyName("range")] Range Range,
    [property: JsonPropertyName("command")] Command? Command)
{
    /// <summary>Present on unresolved lenses; codeLens/resolve fills in <see cref="Command"/>.</summary>
    [JsonPropertyName("data")] public CodeLensData? Data { get; init; }
}

public sealed record CodeLensData(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("character")] int Character,
    [property: JsonPropertyName("kind")] string Kind); // "references"

public sealed record Command(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("command")] string Name,
    [property: JsonPropertyName("arguments")] object[]? Arguments);

public sealed record CodeLensOptions(
    [property: JsonPropertyName("resolveProvider")] bool ResolveProvider);

public sealed record ExecuteCommandParams(
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("arguments")] System.Text.Json.JsonElement[]? Arguments);

public sealed record ExecuteCommandOptions(
    [property: JsonPropertyName("commands")] string[] Commands);
