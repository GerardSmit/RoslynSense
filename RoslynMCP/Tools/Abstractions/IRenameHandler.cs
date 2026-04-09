using Microsoft.CodeAnalysis;

namespace RoslynMCP.Tools;

/// <summary>
/// Handler for propagating symbol renames into non-C# file types (ASPX, Razor, etc.).
/// Unlike other handlers, rename handlers don't use CanHandle — all registered handlers
/// are invoked because a single C# rename may affect multiple file types simultaneously.
/// </summary>
public interface IRenameHandler
{
    Task<List<RenameChangedFile>> UpdateReferencesAsync(
        Project project, Solution solution, ISymbol symbol,
        string oldName, string newName, CancellationToken cancellationToken);
}
