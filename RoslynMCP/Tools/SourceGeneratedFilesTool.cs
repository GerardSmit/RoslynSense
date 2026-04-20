using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

/// <summary>
/// Lists and displays source-generated files produced by C# source generators
/// (e.g., Razor, System.Text.Json, regex source generators).
/// </summary>
[McpServerToolType]
public static class SourceGeneratedFilesTool
{
    [McpServerTool, Description(
        "List all source-generated files in a C# project. Shows files produced by source generators " +
        "(e.g., Razor, System.Text.Json, regex) grouped by generator. " +
        "Use GetSourceGeneratedFileContent to view the content of a specific file.")]
    public static async Task<string> ListSourceGeneratedFiles(
        [Description("Path to the .csproj file or any source file in the project.")] string projectPath,
        IOutputFormatter fmt,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(projectPath))
                return "Error: Project path cannot be empty.";

            var (project, error) = await ResolveProjectAsync(projectPath, cancellationToken);
            if (project is null)
                return error!;

            var generatedDocs = (await project.GetSourceGeneratedDocumentsAsync(cancellationToken)).ToList();
            if (generatedDocs.Count == 0)
            {
                var sb2 = new StringBuilder();
                fmt.AppendEmpty(sb2, "No source-generated files found in this project.");
                return sb2.ToString().TrimEnd();
            }

            // Group by generator name
            var groups = new SortedDictionary<string, List<(string HintName, int LineCount)>>();

            foreach (var doc in generatedDocs)
            {
                var generatorName = ExtractGeneratorName(doc);
                var text = await doc.GetTextAsync(cancellationToken);
                var hintName = doc.HintName ?? doc.Name ?? "<unknown>";

                if (!groups.TryGetValue(generatorName, out var list))
                {
                    list = [];
                    groups[generatorName] = list;
                }

                list.Add((hintName, text.Lines.Count));
            }

            var sb = new StringBuilder();
            fmt.AppendHeader(sb, $"Source Generated Files ({generatedDocs.Count} files)");

            var rows = new List<string[]>();
            foreach (var (generator, files) in groups)
            {
                foreach (var (hintName, lineCount) in files.OrderBy(f => f.HintName, StringComparer.OrdinalIgnoreCase))
                {
                    rows.Add([generator, hintName, lineCount.ToString()]);
                }
            }

            fmt.AppendTable(sb, "Generated Files", ["Generator", "File (hintName)", "Lines"], rows);

            fmt.AppendHints(sb,
                "Use GetSourceGeneratedFileContent with the file's hintName to view its content.");

            return sb.ToString().TrimEnd();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ListSourceGeneratedFiles] Unhandled error: {ex}");
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description(
        "Get the content of a source-generated file. Use ListSourceGeneratedFiles first to discover " +
        "available files and their hint names.")]
    public static async Task<string> GetSourceGeneratedFileContent(
        [Description("Path to the .csproj file or any source file in the project.")] string projectPath,
        [Description("The hint name (file name) of the source-generated file to view, as shown by ListSourceGeneratedFiles.")] string hintName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(projectPath))
                return "Error: Project path cannot be empty.";

            if (string.IsNullOrWhiteSpace(hintName))
                return "Error: Hint name cannot be empty.";

            var (project, error) = await ResolveProjectAsync(projectPath, cancellationToken);
            if (project is null)
                return error!;

            var generatedDocs = await project.GetSourceGeneratedDocumentsAsync(cancellationToken);

            // Find matching document — exact match first, then partial/contains match
            var doc = generatedDocs.FirstOrDefault(d =>
                string.Equals(d.HintName, hintName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(d.Name, hintName, StringComparison.OrdinalIgnoreCase));

            doc ??= generatedDocs.FirstOrDefault(d =>
                (d.HintName ?? d.Name ?? "").Contains(hintName, StringComparison.OrdinalIgnoreCase));

            if (doc is null)
            {
                return $"Error: No source-generated file matching '{hintName}' was found.\n" +
                       "Use ListSourceGeneratedFiles to see available files.";
            }

            var text = await doc.GetTextAsync(cancellationToken);
            var generatorName = ExtractGeneratorName(doc);

            var sb = new StringBuilder();
            sb.AppendLine($"// Source-generated file: {doc.HintName ?? doc.Name}");
            sb.AppendLine($"// Generator: {generatorName}");
            sb.AppendLine($"// Lines: {text.Lines.Count}");
            sb.AppendLine();

            for (int i = 0; i < text.Lines.Count; i++)
            {
                sb.Append((i + 1).ToString().PadLeft(5));
                sb.Append(". ");
                sb.AppendLine(text.Lines[i].ToString());
            }

            return sb.ToString().TrimEnd();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[GetSourceGeneratedFileContent] Unhandled error: {ex}");
            return $"Error: {ex.Message}";
        }
    }

    private static async Task<(Project? Project, string? Error)> ResolveProjectAsync(
        string projectPath, CancellationToken cancellationToken)
    {
        string systemPath = PathHelper.NormalizePath(projectPath);
        if (!File.Exists(systemPath))
            return (null, $"Error: File {systemPath} does not exist.");

        string csprojPath;
        if (systemPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            csprojPath = systemPath;
        }
        else
        {
            var found = await WorkspaceService.FindContainingProjectAsync(systemPath, cancellationToken);
            if (string.IsNullOrEmpty(found))
                return (null, "Error: Couldn't find a project containing this file.");
            csprojPath = found;
        }

        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
            csprojPath, cancellationToken: cancellationToken);

        return (project, null);
    }

    private static string ExtractGeneratorName(SourceGeneratedDocument doc)
    {
        // SourceGeneratedDocument.FilePath contains generator info in its path structure:
        //   <assembly>/<generatorTypeName>/<hintName>
        // We extract the generator type name (second-to-last segment).
        var filePath = doc.FilePath;
        if (!string.IsNullOrEmpty(filePath))
        {
            var segments = filePath.Replace('/', '\\').Split('\\');
            if (segments.Length >= 2)
            {
                var typeName = segments[^2];
                // Simplify common long generator type names
                var lastDot = typeName.LastIndexOf('.');
                return lastDot >= 0 ? typeName[(lastDot + 1)..] : typeName;
            }
        }

        return "<unknown>";
    }
}
