using Microsoft.CodeAnalysis;
using RoslynMCP.Tools;

namespace RoslynMCP.Services.Designers;

/// <summary>What happened to one designer file during a regeneration pass.</summary>
public enum DesignerOutcome
{
    /// <summary>Regenerated content matched what was already on disk; nothing was written.</summary>
    Unchanged,

    /// <summary>The designer file was written.</summary>
    Updated,

    /// <summary>Content was produced but not written, because this was a dry run.</summary>
    WouldUpdate,

    /// <summary>Generation failed; any existing designer file was left untouched.</summary>
    Failed,

    /// <summary>No generator claimed the source file.</summary>
    Skipped,
}

public sealed record DesignerRegeneration(
    string SourcePath,
    string DesignerPath,
    DesignerOutcome Outcome,
    IReadOnlyList<string> Errors)
{
    /// <summary>Populated for a dry run so callers can show what would change.</summary>
    public string? ProposedContent { get; init; }
}

/// <summary>
/// Applies the designer generators to source files, writing a companion file only when its content
/// actually changes.
/// </summary>
/// <remarks>
/// This writes into the user's source tree, so it is deliberately conservative: a file whose markup
/// fails to parse keeps its existing designer rather than losing it, and identical content is never
/// rewritten (which also stops the file watcher from re-triggering itself).
/// </remarks>
public sealed class DesignerRegenerationService(IEnumerable<IDesignerGenerator> generators)
{
    private readonly IDesignerGenerator[] _generators = [.. generators];

    /// <summary>Whether any generator handles this file — the watcher's cheap pre-filter.</summary>
    public bool IsGeneratedFrom(string filePath) => _generators.Any(g => g.CanHandle(filePath));

    /// <summary>Whether a path is itself a generated designer file.</summary>
    public static bool IsDesignerFile(string filePath) =>
        filePath.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase);

    public async Task<DesignerRegeneration> RegenerateAsync(
        string sourcePath, bool dryRun, CancellationToken cancellationToken)
    {
        sourcePath = PathHelper.NormalizePath(sourcePath);

        var generator = _generators.FirstOrDefault(g => g.CanHandle(sourcePath));
        if (generator is null)
            return new DesignerRegeneration(sourcePath, "", DesignerOutcome.Skipped, []);

        var designerPath = generator.GetDesignerPath(sourcePath);

        if (!File.Exists(sourcePath))
        {
            return new DesignerRegeneration(sourcePath, designerPath, DesignerOutcome.Failed,
                ["Source file does not exist."]);
        }

        var projectPath = await NonCSharpProjectFinder.FindProjectAsync(sourcePath, cancellationToken);
        if (string.IsNullOrEmpty(projectPath))
        {
            return new DesignerRegeneration(sourcePath, designerPath, DesignerOutcome.Failed,
                ["Could not find a project containing this file."]);
        }

        DesignerResult result;
        try
        {
            var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
                projectPath, cancellationToken: cancellationToken);
            result = await generator.GenerateAsync(sourcePath, project, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DesignerRegeneration(sourcePath, designerPath, DesignerOutcome.Failed,
                [ex.Message]);
        }

        if (result.Content is not { } rawContent)
            return new DesignerRegeneration(sourcePath, result.DesignerPath, DesignerOutcome.Failed, result.Errors);

        var content = MatchLineEndings(rawContent, result.DesignerPath);

        if (await MatchesExistingAsync(result.DesignerPath, content, cancellationToken))
            return new DesignerRegeneration(sourcePath, result.DesignerPath, DesignerOutcome.Unchanged, []);

        if (dryRun)
        {
            return new DesignerRegeneration(sourcePath, result.DesignerPath, DesignerOutcome.WouldUpdate, [])
            {
                ProposedContent = content,
            };
        }

        try
        {
            await File.WriteAllTextAsync(result.DesignerPath, content, cancellationToken);
        }
        catch (Exception ex)
        {
            return new DesignerRegeneration(sourcePath, result.DesignerPath, DesignerOutcome.Failed,
                [$"Could not write designer file: {ex.Message}"]);
        }

        return new DesignerRegeneration(sourcePath, result.DesignerPath, DesignerOutcome.Updated, []);
    }

    /// <summary>
    /// Regenerates every handled file under a solution, project, or directory. A single file is
    /// regenerated on its own.
    /// </summary>
    public async Task<List<DesignerRegeneration>> RegenerateManyAsync(
        string path, bool dryRun, CancellationToken cancellationToken)
    {
        var results = new List<DesignerRegeneration>();

        foreach (var source in EnumerateSources(PathHelper.NormalizePath(path)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await RegenerateAsync(source, dryRun, cancellationToken));
        }

        return results;
    }

    private IEnumerable<string> EnumerateSources(string path)
    {
        if (File.Exists(path) && !PathHelper.IsSolutionFile(path) && !IsProjectFile(path))
        {
            yield return path;
            yield break;
        }

        foreach (var directory in ResolveSearchDirectories(path))
        {
            foreach (var file in SafeEnumerateFiles(directory))
            {
                if (IsGeneratedFrom(file))
                    yield return file;
            }
        }
    }

    private static IEnumerable<string> ResolveSearchDirectories(string path)
    {
        if (PathHelper.IsSolutionFile(path))
        {
            foreach (var project in PathHelper.GetProjectsFromSolution(path))
            {
                if (Path.GetDirectoryName(project) is { } dir)
                    yield return dir;
            }

            yield break;
        }

        if (IsProjectFile(path))
        {
            if (Path.GetDirectoryName(path) is { } projectDir)
                yield return projectDir;
            yield break;
        }

        if (Directory.Exists(path))
            yield return path;
    }

    private static bool IsProjectFile(string path) =>
        path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> SafeEnumerateFiles(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(f => !IsBuildOutput(f));
        }
        catch
        {
            return [];
        }
    }

    private static bool IsBuildOutput(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment =>
                segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("bin", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Rewrites generated content to use the line endings the existing designer file already has.
    /// </summary>
    /// <remarks>
    /// Without this, regeneration churns every line of a file whenever the generator's platform
    /// newline differs from what is on disk — which is routine: Visual Studio writes CRLF, while a
    /// repository with <c>text=auto eol=lf</c> checks the same file out as LF even on Windows, and
    /// a build on Linux generates LF regardless. Matching the file keeps regeneration a no-op when
    /// nothing actually changed. New files get CRLF, which is what Visual Studio would have written.
    /// </remarks>
    internal static string MatchLineEndings(string content, string designerPath)
    {
        var normalized = content.ReplaceLineEndings("\n");
        return DetectNewline(designerPath) == "\n" ? normalized : normalized.Replace("\n", "\r\n");
    }

    private static string DetectNewline(string path)
    {
        try
        {
            if (!File.Exists(path))
                return "\r\n";

            var existing = File.ReadAllText(path);
            var firstNewline = existing.IndexOf('\n');
            if (firstNewline < 0)
                return "\r\n"; // No newline at all: nothing to preserve.

            return firstNewline > 0 && existing[firstNewline - 1] == '\r' ? "\r\n" : "\n";
        }
        catch
        {
            return "\r\n";
        }
    }

    private static async Task<bool> MatchesExistingAsync(
        string designerPath, string content, CancellationToken cancellationToken)
    {
        if (!File.Exists(designerPath))
            return false;

        try
        {
            var existing = await File.ReadAllTextAsync(designerPath, cancellationToken);
            return string.Equals(existing, content, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}
