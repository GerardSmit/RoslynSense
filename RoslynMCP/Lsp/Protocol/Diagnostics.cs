using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

public sealed record PublishDiagnosticsParams(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("version")] int? Version,
    [property: JsonPropertyName("diagnostics")] Diagnostic[] Diagnostics);

public sealed record Diagnostic(
    [property: JsonPropertyName("range")] Range Range,
    [property: JsonPropertyName("severity")] int Severity, // 1 error, 2 warning, 3 info, 4 hint
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("message")] string Message);
