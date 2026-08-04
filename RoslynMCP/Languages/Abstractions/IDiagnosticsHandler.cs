using RoslynMCP.Services;

namespace RoslynMCP.Languages;

/// <summary>
/// Handler for validating/diagnosing non-C# file types (ASPX, Razor, etc.).
/// </summary>
public interface IDiagnosticsHandler
{
    bool CanHandle(string filePath);
    Task<string> ValidateAsync(string systemPath, IOutputFormatter fmt, CancellationToken cancellationToken);
}
