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

/// <summary>
/// What a lens needs to resolve itself later. <paramref name="Kind"/> is the emitter's own
/// vocabulary — C# uses "references", "derived", "implemented" and "overridden", and a pack uses
/// whatever it likes — so <paramref name="PackId"/> is what says whose vocabulary to read it in.
/// </summary>
/// <remarks>
/// Null <paramref name="PackId"/> means C#, which is why it is last and optional: a lens over a
/// file a pack owns is routed back to that pack by the URI's extension, but a pack contributing a
/// lens to a <c>.cs</c> file has no extension to be found by and has to name itself.
/// </remarks>
public sealed record CodeLensData(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("character")] int Character,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("packId")] string? PackId = null);

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
