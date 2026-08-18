using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services;
using RoslynMCP.Tools;

namespace RoslynMCP.Languages.AppSettings.Core;

/// <summary>
/// One configuration file together with everything the workspace knows about it: the keys, the
/// project whose code reads them, and the usage index over that project's closure.
/// </summary>
/// <remarks>
/// <see cref="Project"/> is nullable the way <c>DbmlView</c>'s is: the file's own structure —
/// outline, folding, the keys themselves — needs no project, and only counting references and
/// offering bound properties does.
/// </remarks>
internal sealed record AppSettingsView(
    AppSettingsDocument Document, Project? Project, ConfigurationUsageIndex Index)
{
    public string FilePath => Document.FilePath;

    public SourceText Text => Document.Text;
}

/// <summary>Resolves a configuration JSON path to its keys, its project and its usage index.</summary>
internal static class AppSettingsWorkspace
{
    public static async Task<AppSettingsView?> GetAsync(string filePath, CancellationToken ct)
    {
        if (AppSettingsDocumentCache.Get(filePath) is not { } document)
            return null;

        var project = await ProjectForAsync(document.FilePath, ct);

        return new AppSettingsView(
            document, project,
            project is null
                ? ConfigurationUsageIndex.Empty
                : await ConfigurationUsageIndex.GetAsync(project, ct));
    }

    /// <summary>
    /// The project whose application reads this file. For a file in a project tree, the nearest
    /// <c>.csproj</c> above it; for a user-secrets store, the project declaring the
    /// <c>UserSecretsId</c> the path carries — the store lives under the profile, nowhere near
    /// the code that reads it.
    /// </summary>
    public static async Task<Project?> ProjectForAsync(string filePath, CancellationToken ct)
    {
        string? projectPath = AppSettingsFile.IsSecretsPath(filePath)
            ? ProjectPathForSecrets(filePath)
            : await NonCSharpProjectFinder.FindProjectAsync(filePath, ct);

        if (projectPath is not { Length: > 0 })
            return null;

        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
            projectPath, targetFilePath: filePath, cancellationToken: ct);

        return project;
    }

    /// <summary>
    /// Every configuration file feeding this project's keyspace: the base file, its environment
    /// overlays, and the user-secrets store when the project declares one. Base file first.
    /// </summary>
    public static IReadOnlyList<string> ConfigurationFilesFor(string projectFilePath)
    {
        var files = new List<string>();

        if (Path.GetDirectoryName(projectFilePath) is { Length: > 0 } directory
            && Directory.Exists(directory))
        {
            try
            {
                files.AddRange(Directory.EnumerateFiles(directory, "appsettings*.json")
                    .Where(AppSettingsFile.IsConfigurationPath)
                    .OrderBy(path => AppSettingsFile.Environment(path) is null ? 0 : 1)
                    .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase));
            }
            catch (IOException)
            {
            }
        }

        if (SecretsPathFor(projectFilePath) is { } secrets && File.Exists(secrets))
            files.Add(secrets);

        return files;
    }

    // ---- User secrets ------------------------------------------------------------------------

    private static readonly ConcurrentDictionary<string, string?> s_secretsProjects =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The secrets store a project declares, whether or not it exists yet.</summary>
    public static string? SecretsPathFor(string projectFilePath)
    {
        if (UserSecretsId(projectFilePath) is not { Length: > 0 } id)
            return null;

        string root = System.Environment.GetFolderPath(
            System.Environment.SpecialFolder.ApplicationData);

        return root.Length == 0
            ? null
            : Path.Combine(root, "Microsoft", "UserSecrets", id, AppSettingsFile.SecretsFileName);
    }

    /// <summary>
    /// The project a <c>secrets.json</c> path belongs to: the directory name is the
    /// <c>UserSecretsId</c>, and the project declaring that id in the most recent solution is the
    /// owner. Resolved once per id — an id is minted at <c>user-secrets init</c> and never moves.
    /// </summary>
    private static string? ProjectPathForSecrets(string secretsPath)
    {
        if (Path.GetDirectoryName(secretsPath) is not { Length: > 0 } directory)
            return null;

        string id = Path.GetFileName(directory);
        if (id.Length == 0)
            return null;

        return s_secretsProjects.GetOrAdd(id, static secretsId =>
        {
            if (WorkspaceService.TryGetMostRecentSolution() is not { } solution)
                return null;

            foreach (var project in solution.Projects)
            {
                if (project.FilePath is { Length: > 0 } path
                    && string.Equals(UserSecretsId(path), secretsId, StringComparison.OrdinalIgnoreCase))
                {
                    return path;
                }
            }

            return null;
        });
    }

    /// <summary>Read from the project file's text rather than through MSBuild evaluation — the
    /// id is authored as a literal, and evaluating a project to read one line is the expensive
    /// way to be equally right.</summary>
    private static string? UserSecretsId(string projectFilePath)
    {
        try
        {
            if (!File.Exists(projectFilePath))
                return null;

            string text = File.ReadAllText(projectFilePath);
            const string open = "<UserSecretsId>";
            const string close = "</UserSecretsId>";

            int start = text.IndexOf(open, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return null;

            start += open.Length;
            int end = text.IndexOf(close, start, StringComparison.OrdinalIgnoreCase);
            return end > start ? text[start..end].Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
