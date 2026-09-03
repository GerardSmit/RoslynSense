using System.Text.Json.Serialization;
using RoslynMCP.Services;

namespace RoslynMCP.Lsp.Handlers;

public sealed record EditorContextParams(
    [property: JsonPropertyName("solutionPath")] string SolutionPath,
    [property: JsonPropertyName("activeFile")] string? ActiveFile,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("character")] int Character,
    [property: JsonPropertyName("enclosingSymbol")] string? EnclosingSymbol,
    [property: JsonPropertyName("selectionText")] string? SelectionText,
    [property: JsonPropertyName("openFiles")] string[]? OpenFiles,
    [property: JsonPropertyName("dirtyFiles")] string[]? DirtyFiles,
    [property: JsonPropertyName("diagnostics")] VisibleDiagnosticParams[]? Diagnostics);

public sealed record VisibleDiagnosticParams(
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("line")] int Line);

/// <summary>Records what the editor is showing so MCP tools can answer questions about "this".</summary>
internal static class EditorContextHandler
{
    public static void Report(EditorContextParams p)
    {
        // The extension may only know its workspace folder; resolve to the owning solution so
        // the key matches what MCP tools derive from their working directory.
        string? solution = File.Exists(p.SolutionPath)
            ? p.SolutionPath
            : Daemon.HostPaths.ResolveSolutionKey(p.SolutionPath);
        if (solution is null)
            return;

        EditorContextStore.Write(solution, new EditorContextStore.Context(
            p.ActiveFile,
            p.Line,
            p.Character,
            p.EnclosingSymbol,
            p.SelectionText,
            p.OpenFiles ?? [],
            p.DirtyFiles ?? [],
            (p.Diagnostics ?? []).Select(d => new EditorContextStore.VisibleDiagnostic(
                d.Severity, d.Code, d.Message, d.Line)).ToArray(),
            DateTime.UtcNow));
    }
}
