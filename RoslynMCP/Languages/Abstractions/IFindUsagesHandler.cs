namespace RoslynMCP.Languages;

/// <summary>
/// Handler for resolving FindUsages in non-C# file types (ASPX, Razor, etc.).
/// </summary>
public interface IFindUsagesHandler
{
    bool CanHandle(string filePath);
    Task<string> FindUsagesAsync(string systemPath, string markupSnippet, int maxResults, CancellationToken cancellationToken, int? hintLine = null);
}
