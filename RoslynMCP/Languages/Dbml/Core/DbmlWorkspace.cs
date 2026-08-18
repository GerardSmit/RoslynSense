using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services;
using RoslynMCP.Tools;

namespace RoslynMCP.Languages.Dbml.Core;

/// <summary>
/// One <c>.dbml</c> together with everything the workspace knows about it: the parsed model, the
/// project that compiles the C# generated from it, and the bindings between the two.
/// </summary>
/// <remarks>
/// <see cref="Project"/> is nullable on purpose. A model means something without one — its outline,
/// its folding and its structural diagnostics come from the file alone, and only binding a
/// declaration to a symbol needs the workspace. Refusing to produce a view for a <c>.dbml</c> sitting
/// outside any project would take all of that away to protect the one feature that cannot work.
/// </remarks>
internal sealed record DbmlView(DbmlDocument Document, Project? Project, DbmlGeneratedIndex Index)
{
    public string FilePath => Document.FilePath;

    public SourceText Text => Document.Text;

    public DbmlDatabase Database => Document.Database;
}

/// <summary>Resolves a <c>.dbml</c> path to a parsed model, its project and its bindings.</summary>
internal static class DbmlWorkspace
{
    /// <summary>
    /// The full view of a model, or <c>null</c> when the path is not a <c>.dbml</c> or cannot be read.
    /// </summary>
    public static async Task<DbmlView?> GetAsync(string filePath, CancellationToken ct)
    {
        if (DbmlDocumentCache.Get(filePath) is not { } document)
            return null;

        // After the parse, not before: everything except symbol binding works without a project, so
        // the file is never held hostage to opening one.
        var project = await ProjectForAsync(document.FilePath, ct);

        return new DbmlView(
            document, project, await DbmlGeneratedIndex.GetAsync(document.FilePath, project, ct));
    }

    /// <summary>
    /// The project that compiles the designer for a model.
    /// </summary>
    /// <remarks>
    /// Singular where the protobuf pack's is plural, and that difference is the languages'. A
    /// <c>.proto</c> is routinely compiled by both sides of a wire into two separate assemblies; a
    /// <c>.dbml</c> generates a <c>DataContext</c> that belongs to exactly one project, because the
    /// custom tool runs where the file sits. The nearest <c>.csproj</c> above the file is therefore
    /// the answer rather than a heuristic.
    /// </remarks>
    public static async Task<Project?> ProjectForAsync(string dbmlPath, CancellationToken ct)
    {
        if (await NonCSharpProjectFinder.FindProjectAsync(dbmlPath, ct) is not { Length: > 0 } projectPath)
            return null;

        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
            projectPath, targetFilePath: dbmlPath, cancellationToken: ct);

        return project;
    }
}
