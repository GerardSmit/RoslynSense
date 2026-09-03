using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

public sealed record DidChangeWatchedFilesParams(
    [property: JsonPropertyName("changes")] FileEvent[] Changes);

public sealed record FileEvent(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("type")] int Type);

/// <summary>LSP FileChangeType.</summary>
public static class FileChangeType
{
    public const int Created = 1;
    public const int Changed = 2;
    public const int Deleted = 3;
}
