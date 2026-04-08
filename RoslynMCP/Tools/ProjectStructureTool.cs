using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

/// <summary>
/// Provides an overview of a project's structure: files, references, target framework,
/// and types organized by namespace in a compact tree format.
/// </summary>
[McpServerToolType]
public static class ProjectStructureTool
{
    [McpServerTool, Description(
        "Get an overview of a C# project's structure. Shows target framework, references, " +
        "source files, and types organized by namespace. " +
        "Useful for understanding the project layout before navigating code.")]
    public static async Task<string> GetProjectStructure(
        [Description("Path to the .csproj file or any file in the project.")] string projectOrFilePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(projectOrFilePath))
                return "Error: Path cannot be empty.";

            string systemPath = PathHelper.NormalizePath(projectOrFilePath);
            if (!File.Exists(systemPath))
                return $"Error: File {systemPath} does not exist.";

            string projectPath;
            if (systemPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                projectPath = systemPath;
            }
            else
            {
                var found = await WorkspaceService.FindContainingProjectAsync(systemPath, cancellationToken);
                if (string.IsNullOrEmpty(found))
                    return "Error: Couldn't find a project containing this file.";
                projectPath = found;
            }

            var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
                projectPath, cancellationToken: cancellationToken);

            return await FormatProjectStructureAsync(project, projectPath, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ProjectStructure] Unhandled error: {ex}");
            return $"Error: {ex.Message}";
        }
    }

    private static async Task<string> FormatProjectStructureAsync(
        Project project,
        string projectPath,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        string? projectDir = Path.GetDirectoryName(projectPath);

        sb.AppendLine($"# Project: {project.Name}");
        sb.AppendLine();

        // Basic info
        sb.AppendLine("## Info");
        sb.AppendLine();
        sb.AppendLine($"- **Path**: {projectPath}");
        if (project.CompilationOptions is not null)
            sb.AppendLine($"- **Output**: {project.CompilationOptions.OutputKind}");

        // Target framework from preprocessor symbols
        string? targetFramework = InferTargetFramework(project);
        if (targetFramework is not null)
            sb.AppendLine($"- **Framework**: {targetFramework}");
        sb.AppendLine();

        // Source files grouped by folder
        var documents = project.Documents.ToList();
        sb.AppendLine($"## Files ({documents.Count})");
        sb.AppendLine();

        var byFolder = documents
            .Where(d => d.FilePath is not null)
            .GroupBy(d =>
            {
                string dir = Path.GetDirectoryName(d.FilePath!)!;
                return projectDir is not null
                    ? Path.GetRelativePath(projectDir, dir)
                    : dir;
            })
            .OrderBy(g => g.Key);

        foreach (var group in byFolder)
        {
            string folder = group.Key == "." ? "(root)" : group.Key;
            var files = group.OrderBy(d => d.Name).Select(d => d.Name).ToList();
            sb.AppendLine($"- {folder}/: {string.Join(", ", files)}");
        }
        sb.AppendLine();

        // Types organized by namespace as a tree
        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation is not null)
        {
            var types = compilation.Assembly.GlobalNamespace
                .GetMembers()
                .SelectMany(GetAllTypes)
                .Where(t => t.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal)
                .Where(t => !t.Name.StartsWith('<'))
                .ToList();

            if (types.Count > 0)
            {
                sb.AppendLine($"## Types ({types.Count})");
                sb.AppendLine();
                sb.AppendLine("```");
                AppendNamespaceTree(sb, types);
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }

        // Project references
        var projectRefs = project.ProjectReferences.ToList();
        if (projectRefs.Count > 0)
        {
            sb.AppendLine($"## Project References ({projectRefs.Count})");
            sb.AppendLine();
            foreach (var pRef in projectRefs)
            {
                var refProject = project.Solution.GetProject(pRef.ProjectId);
                sb.AppendLine($"- {refProject?.Name ?? pRef.ProjectId.ToString()}");
            }
            sb.AppendLine();
        }

        // Package references (deduplicated, showing assembly names)
        var metadataRefs = project.MetadataReferences.ToList();
        sb.AppendLine($"## Assembly References ({metadataRefs.Count})");
        sb.AppendLine();
        foreach (var mRef in metadataRefs.OrderBy(r => Path.GetFileName(r.Display ?? "")))
        {
            string name = Path.GetFileNameWithoutExtension(mRef.Display ?? "unknown");
            sb.AppendLine($"- {name}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Infers the target framework from Roslyn's preprocessor symbols.
    /// MSBuild defines symbols like NET8_0, NET10_0, NETCOREAPP, NETSTANDARD2_0, etc.
    /// </summary>
    internal static string? InferTargetFramework(Project project)
    {
        if (project.ParseOptions is not CSharpParseOptions parseOptions)
            return null;

        var symbols = parseOptions.PreprocessorSymbolNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Check for specific .NET version symbols (newest first)
        string[] netVersions =
        [
            "NET10_0", "NET9_0", "NET8_0", "NET7_0", "NET6_0", "NET5_0"
        ];

        foreach (var version in netVersions)
        {
            if (symbols.Contains(version))
                return version.ToLowerInvariant().Replace('_', '.');
        }

        // Check for .NET Core
        string[] coreVersions =
        [
            "NETCOREAPP3_1", "NETCOREAPP3_0", "NETCOREAPP2_2", "NETCOREAPP2_1", "NETCOREAPP2_0",
            "NETCOREAPP1_1", "NETCOREAPP1_0"
        ];

        foreach (var version in coreVersions)
        {
            if (symbols.Contains(version))
                return version.ToLowerInvariant().Replace('_', '.');
        }

        // Check for .NET Standard
        string[] standardVersions =
        [
            "NETSTANDARD2_1", "NETSTANDARD2_0", "NETSTANDARD1_6", "NETSTANDARD1_5",
            "NETSTANDARD1_4", "NETSTANDARD1_3", "NETSTANDARD1_2", "NETSTANDARD1_1", "NETSTANDARD1_0"
        ];

        foreach (var version in standardVersions)
        {
            if (symbols.Contains(version))
                return version.ToLowerInvariant().Replace('_', '.');
        }

        // Check for .NET Framework
        string[] frameworkVersions =
        [
            "NET48", "NET472", "NET471", "NET47", "NET462", "NET461",
            "NET46", "NET452", "NET451", "NET45", "NET40", "NET35", "NET20"
        ];

        foreach (var version in frameworkVersions)
        {
            if (symbols.Contains(version))
                return version.ToLowerInvariant();
        }

        // Fallback: check generic symbols
        if (symbols.Contains("NETCOREAPP"))
            return ".NET Core";
        if (symbols.Contains("NETSTANDARD"))
            return ".NET Standard";
        if (symbols.Contains("NETFRAMEWORK"))
            return ".NET Framework";

        return null;
    }

    /// <summary>
    /// Builds a compact tree of namespaces → types.
    /// </summary>
    private static void AppendNamespaceTree(StringBuilder sb, List<INamedTypeSymbol> types)
    {
        var byNamespace = types
            .GroupBy(t =>
            {
                if (t.ContainingNamespace is null || t.ContainingNamespace.IsGlobalNamespace)
                    return "(global)";
                return t.ContainingNamespace.ToDisplayString();
            })
            .OrderBy(g => g.Key);

        foreach (var nsGroup in byNamespace)
        {
            sb.AppendLine(nsGroup.Key);

            var sorted = nsGroup
                .OrderBy(t => t.TypeKind)
                .ThenBy(t => t.Name)
                .ToList();

            for (int i = 0; i < sorted.Count; i++)
            {
                var type = sorted[i];
                bool isLast = i == sorted.Count - 1;
                string prefix = isLast ? "└── " : "├── ";
                string kindLabel = FormatTypeKind(type);
                sb.AppendLine($"  {prefix}{kindLabel} {type.Name}");
            }
        }
    }

    private static string FormatTypeKind(INamedTypeSymbol type)
    {
        return type.TypeKind switch
        {
            TypeKind.Interface => "[I]",
            TypeKind.Enum => "[E]",
            TypeKind.Struct => "[S]",
            TypeKind.Delegate => "[D]",
            TypeKind.Class when type.IsAbstract => "[A]",
            TypeKind.Class when type.IsStatic => "[S]",
            TypeKind.Class when type.IsRecord => "[R]",
            _ => "[C]",
        };
    }

    private static IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceOrTypeSymbol symbol)
    {
        if (symbol is INamedTypeSymbol type)
        {
            yield return type;
            foreach (var nested in type.GetTypeMembers().SelectMany(GetAllTypes))
                yield return nested;
        }
        else if (symbol is INamespaceSymbol ns)
        {
            foreach (var member in ns.GetMembers().SelectMany(GetAllTypes))
                yield return member;
        }
    }
}
