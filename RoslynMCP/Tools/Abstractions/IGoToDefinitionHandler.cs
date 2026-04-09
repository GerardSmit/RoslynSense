namespace RoslynMCP.Tools;

/// <summary>
/// Handler for resolving GoToDefinition in non-C# file types (ASPX, Razor, etc.).
/// </summary>
public interface IGoToDefinitionHandler
{
    bool CanHandle(string filePath);
    Task<string> ResolveAsync(string systemPath, string markupSnippet, int contextLines, CancellationToken cancellationToken);
}
