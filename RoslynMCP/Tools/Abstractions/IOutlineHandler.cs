namespace RoslynMCP.Tools;

/// <summary>
/// Handler for producing file outlines for non-C# file types (ASPX, Razor, etc.).
/// </summary>
public interface IOutlineHandler
{
    bool CanHandle(string filePath);
    Task<string> GetOutlineAsync(string systemPath, CancellationToken cancellationToken);
}
