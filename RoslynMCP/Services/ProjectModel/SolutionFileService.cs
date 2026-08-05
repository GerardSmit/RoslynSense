using System.Collections.Concurrent;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

namespace RoslynMCP.Services.ProjectModel;

/// <summary>A node in the solution's logical tree: a solution folder or a project.</summary>
public sealed record SolutionNode(
    string Id,
    string? ParentId,
    string Name,
    string? Path,
    bool IsFolder,
    IReadOnlyList<string> Files);

/// <summary>
/// The solution's *logical* structure — the folder hierarchy authors see in Visual Studio and
/// Rider. Roslyn's Solution model has no concept of solution folders, so this reads the file
/// itself, through the same library Visual Studio and <c>dotnet sln</c> use.
/// </summary>
/// <remarks>
/// Both formats go through <c>Microsoft.VisualStudio.SolutionPersistence</c> rather than being
/// parsed here. It is not only less code: <c>.slnx</c> writes its folders flat, with the whole
/// path in one <c>Name</c> attribute (<c>&lt;Folder Name="/Outer/Inner/"/&gt;</c>), and a reader
/// that expects them nested silently loses every folder below the first level — while a writer
/// that nests them produces a file Visual Studio opens with the projects inside missing. Neither
/// mistake announces itself. The <c>.sln</c> side gains the same way: solution items live in a
/// <c>ProjectSection</c> that MSBuild's own parser does not expose at all.
/// </remarks>
public static class SolutionFileService
{
    /// <summary>The parse, keyed on the file's stamp. A full-tree refresh replays this once per
    /// expanded node and the search picker once per keystroke, so re-reading the file each time
    /// made the parse the constant cost of the whole explorer.</summary>
    private sealed record CachedNodes(DateTime LastWriteUtc, long Length, IReadOnlyList<SolutionNode> Nodes);

    private static readonly ConcurrentDictionary<string, CachedNodes> s_read =
        new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<SolutionNode> Read(string solutionPath)
    {
        var info = new FileInfo(solutionPath);
        if (!info.Exists)
            return [];

        if (s_read.TryGetValue(info.FullName, out var cached)
            && cached.LastWriteUtc == info.LastWriteTimeUtc
            && cached.Length == info.Length)
        {
            return cached.Nodes;
        }

        try
        {
            var nodes = ToNodes(Open(solutionPath), solutionPath);
            s_read[info.FullName] = new CachedNodes(info.LastWriteTimeUtc, info.Length, nodes);
            return nodes;
        }
        catch (Exception ex)
        {
            ServiceLog.Warn(
                $"Could not read the structure of '{Path.GetFileName(solutionPath)}': {ex.Message}",
                key: $"solution-parse:{solutionPath}");
            return [];
        }
    }

    /// <summary>
    /// Loads the solution into the model the serializer round-trips through.
    /// </summary>
    /// <remarks>
    /// Blocking on the async API is safe here and not merely convenient: the daemon has no
    /// synchronization context to deadlock against, and the tree is read from synchronous call
    /// sites that would otherwise all have to become async for a file parse measured in
    /// milliseconds.
    /// </remarks>
    internal static SolutionModel Open(string solutionPath) =>
        SerializerFor(solutionPath).OpenAsync(solutionPath, default).GetAwaiter().GetResult();

    internal static void Save(string solutionPath, SolutionModel model)
    {
        SerializerFor(solutionPath).SaveAsync(solutionPath, model, default).GetAwaiter().GetResult();

        // The stamp check would catch this too, but not within the same timestamp granularity —
        // and the caller is about to re-read what it just wrote.
        s_read.TryRemove(Path.GetFullPath(solutionPath), out _);
    }

    private static Microsoft.VisualStudio.SolutionPersistence.ISolutionSerializer SerializerFor(
        string solutionPath) =>
        SolutionSerializers.GetSerializerByMoniker(solutionPath)
        ?? throw new InvalidOperationException(
            $"'{Path.GetFileName(solutionPath)}' is not a solution file this can read.");

    private static IReadOnlyList<SolutionNode> ToNodes(SolutionModel model, string solutionPath)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        var nodes = new List<SolutionNode>();

        foreach (var folder in model.SolutionFolders)
        {
            nodes.Add(new SolutionNode(
                Id: folder.Path,
                // From the path rather than from the model's own Parent, which a folder does not
                // carry — nesting in this format *is* the path, and both readers agree on that.
                ParentId: ParentPath(folder.Path),
                Name: folder.ActualDisplayName,
                Path: null,
                IsFolder: true,
                Files: [.. (folder.Files ?? [])
                    .Select(file => Path.GetFullPath(Path.Combine(directory, file)))]));
        }

        foreach (var project in model.SolutionProjects)
        {
            string full = Path.GetFullPath(Path.Combine(directory, project.FilePath));
            nodes.Add(new SolutionNode(
                Id: full,
                ParentId: project.Parent?.Path,
                Name: project.ActualDisplayName,
                Path: full,
                IsFolder: false,
                Files: []));
        }

        return nodes;
    }

    /// <summary>
    /// The folder one level up, or null for a top-level folder. <c>/Outer/Inner/</c> gives
    /// <c>/Outer/</c>.
    /// </summary>
    internal static string? ParentPath(string folderPath)
    {
        string trimmed = folderPath.TrimEnd('/');
        int slash = trimmed.LastIndexOf('/');
        return slash <= 0 ? null : trimmed[..(slash + 1)];
    }
}
