using RoslynMCP.Services;

namespace RoslynMCP.Tools;

/// <summary>
/// Locates the containing .csproj project for non-C# files (ASPX, Razor, <c>.proto</c>, …) by
/// walking parent directories, falling back to the workspace when the walk finds nothing.
/// </summary>
/// <remarks>
/// <para>
/// The directory walk goes first, and that ordering is the whole point of this type. The fallback,
/// <see cref="WorkspaceService.FindContainingProjectAsync"/>, opens <em>every</em> <c>.csproj</c> in
/// <em>every</em> ancestor directory through the full MSBuild load path and then asks whether the
/// file is one of the project's documents — and <c>WorkspaceService.FindDocumentInProject</c> scans
/// only <c>project.Documents</c>, which holds <c>Compile</c> items. A <c>.proto</c>, an
/// <c>.aspx</c> or a <c>.razor</c> is never a <c>Compile</c> item, so for the only file kinds that
/// reach this method the answer is unconditionally <see langword="null"/>: it was paying a
/// design-time build per candidate project to be told nothing, on every single request.
/// </para>
/// <para>
/// It also could not stop early. Not finding the file means walking to the drive root, so a
/// <c>.proto</c> under a temp directory enumerated <c>%TEMP%</c> — tens of thousands of entries on
/// a developer's machine, with a leading-wildcard pattern NTFS cannot serve from its name index —
/// once per request, and a single <c>.proto</c> scroll issues one <c>codeLens</c> plus one
/// <c>codeLens/resolve</c> per lens.
/// </para>
/// <para>
/// The walk answers the same question by the rule the projects themselves follow, terminates at the
/// first project directory above the file, and touches no MSBuild at all. The workspace probe is
/// kept underneath it for the case the walk cannot serve — a linked file included from outside the
/// project's own directory tree — where it is the only thing that can find the owner.
/// </para>
/// </remarks>
internal static class NonCSharpProjectFinder
{
    public static async Task<string?> FindProjectAsync(
        string filePath, CancellationToken cancellationToken)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(filePath)!);
        while (dir is not null)
        {
            var csproj = dir.GetFiles("*.csproj").FirstOrDefault();
            if (csproj is not null)
                return csproj.FullName;
            dir = dir.Parent;
        }

        string? projectPath = await WorkspaceService.FindContainingProjectAsync(filePath, cancellationToken);
        return string.IsNullOrEmpty(projectPath) ? null : projectPath;
    }
}
