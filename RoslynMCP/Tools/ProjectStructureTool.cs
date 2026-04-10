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
        "and types organized by namespace. " +
        "Useful for understanding the project layout before navigating code.")]
    public static async Task<string> GetProjectStructure(
        [Description("Path to the .csproj file or any file in the project.")] string projectOrFilePath,
        IOutputFormatter fmt,
        [Description("Include system assemblies (System.*, Microsoft.*) in the assembly references list. Default: false.")]
        bool includeSystemAssemblies = false,
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

            return await FormatProjectStructureAsync(project, projectPath, includeSystemAssemblies, fmt, cancellationToken);
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
        bool includeSystemAssemblies,
        IOutputFormatter fmt,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"# Project: {project.Name}");
        sb.AppendLine();

        // Basic info
        sb.AppendLine("## Info");
        sb.AppendLine();
        sb.AppendLine($"- **Path**: {projectPath}");
        if (project.CompilationOptions is not null)
        {
            sb.AppendLine($"- **Output**: {FormatOutputKind(project.CompilationOptions.OutputKind)}");
            sb.AppendLine($"- **Nullable**: {project.CompilationOptions.NullableContextOptions}");
        }

        // Target framework from preprocessor symbols
        string? targetFramework = InferTargetFramework(project);
        if (targetFramework is not null)
            sb.AppendLine($"- **Framework**: {targetFramework}");

        // C# language version
        if (project.ParseOptions is CSharpParseOptions csharpOptions)
        {
            sb.AppendLine($"- **C# Version**: {csharpOptions.LanguageVersion}");
        }

        // App/framework type detection
        var appTypes = DetectAppType(project, projectPath);
        if (appTypes.Count > 0)
            sb.AppendLine($"- **App Type**: {string.Join(", ", appTypes)}");

        // Test framework detection
        string? testFramework = DetectTestFramework(project);
        if (testFramework is not null)
            sb.AppendLine($"- **Test Framework**: {testFramework}");

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

                // For large projects, show a compact namespace summary
                const int compactThreshold = 200;
                if (types.Count > compactThreshold)
                {
                    AppendNamespaceSummary(sb, types);
                }
                else
                {
                    sb.AppendLine("```");
                    AppendNamespaceTree(sb, types);
                    sb.AppendLine("```");
                }
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
        var allAssemblies = metadataRefs
            .Select(r => Path.GetFileNameWithoutExtension(r.Display ?? "unknown"))
            .OrderBy(n => n)
            .ToList();

        if (includeSystemAssemblies)
        {
            sb.AppendLine($"## Assembly References ({allAssemblies.Count})");
            sb.AppendLine();
            foreach (var name in allAssemblies)
            {
                sb.AppendLine($"- {name}");
            }
        }
        else
        {
            var nonSystem = allAssemblies
                .Where(n => !n.StartsWith("System", StringComparison.OrdinalIgnoreCase) &&
                            !n.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) &&
                            !n.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase) &&
                            !n.StartsWith("mscorlib", StringComparison.OrdinalIgnoreCase) &&
                            !n.StartsWith("WindowsBase", StringComparison.OrdinalIgnoreCase))
                .ToList();
            int systemCount = allAssemblies.Count - nonSystem.Count;

            sb.AppendLine($"## Assembly References ({nonSystem.Count} packages, {systemCount} system)");
            sb.AppendLine();
            if (nonSystem.Count > 0)
            {
                foreach (var name in nonSystem)
                {
                    sb.AppendLine($"- {name}");
                }
            }
            else
            {
                sb.AppendLine("_(only system assemblies)_");
            }
        }
        fmt.AppendHints(sb,
            "Use GetFileOutline to see detailed structure of a file",
            "Use FindSymbol to search for specific types or members");

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
    /// Builds a compact namespace summary showing type counts per namespace.
    /// </summary>
    private static void AppendNamespaceSummary(StringBuilder sb, List<INamedTypeSymbol> types)
    {
        var byNamespace = types
            .GroupBy(t =>
            {
                if (t.ContainingNamespace is null || t.ContainingNamespace.IsGlobalNamespace)
                    return "(global)";
                return t.ContainingNamespace.ToDisplayString();
            })
            .OrderBy(g => g.Key);

        sb.AppendLine("| Namespace | Classes | Interfaces | Enums | Structs | Other |");
        sb.AppendLine("|-----------|---------|------------|-------|---------|-------|");

        foreach (var nsGroup in byNamespace)
        {
            int classes = nsGroup.Count(t => t.TypeKind == TypeKind.Class);
            int interfaces = nsGroup.Count(t => t.TypeKind == TypeKind.Interface);
            int enums = nsGroup.Count(t => t.TypeKind == TypeKind.Enum);
            int structs = nsGroup.Count(t => t.TypeKind == TypeKind.Struct);
            int other = nsGroup.Count() - classes - interfaces - enums - structs;

            sb.Append($"| {nsGroup.Key} | {classes}");
            sb.Append($" | {interfaces}");
            sb.Append($" | {enums}");
            sb.Append($" | {structs}");
            sb.AppendLine($" | {other} |");
        }
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
            TypeKind.Class when type.IsStatic => "[Sc]",
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

    internal static string FormatOutputKind(OutputKind outputKind)
    {
        return outputKind switch
        {
            OutputKind.ConsoleApplication => "Console Application",
            OutputKind.WindowsApplication => "Windows Application",
            OutputKind.DynamicallyLinkedLibrary => "Library (DLL)",
            OutputKind.NetModule => "Module",
            OutputKind.WindowsRuntimeMetadata => "WinRT Metadata",
            OutputKind.WindowsRuntimeApplication => "WinRT Application",
            _ => outputKind.ToString(),
        };
    }

    /// <summary>
    /// Detects the app/framework type from SDK, assembly references, and project properties.
    /// </summary>
    internal static List<string> DetectAppType(Project project, string projectPath)
    {
        var assemblyNames = project.MetadataReferences
            .Select(r => Path.GetFileNameWithoutExtension(r.Display ?? ""))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string? sdkType = ReadProjectSdk(projectPath);

        // Check for WebForms files (.aspx, .ascx, .master) in the project directory
        bool hasWebFormFiles = false;
        string? projectDir = Path.GetDirectoryName(projectPath);
        if (projectDir is not null && Directory.Exists(projectDir))
        {
            hasWebFormFiles = Directory.EnumerateFiles(projectDir, "*.*", SearchOption.AllDirectories)
                .Any(f => f.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase)
                    || f.EndsWith(".ascx", StringComparison.OrdinalIgnoreCase)
                    || f.EndsWith(".master", StringComparison.OrdinalIgnoreCase));
        }

        return DetectAppType(assemblyNames, sdkType, hasWebFormFiles);
    }

    /// <summary>
    /// Core detection logic, testable with explicit assembly names and SDK type.
    /// </summary>
    internal static List<string> DetectAppType(IReadOnlySet<string> assemblyNames, string? sdkType, bool hasWebFormFiles = false)
    {
        var result = new List<string>();

        // --- Desktop frameworks ---
        if (assemblyNames.Contains("Avalonia") || assemblyNames.Contains("Avalonia.Base"))
            result.Add("Avalonia");
        if (assemblyNames.Contains("Microsoft.Maui") || assemblyNames.Contains("Microsoft.Maui.Controls"))
            result.Add(".NET MAUI");
        if (assemblyNames.Contains("Microsoft.WinUI") || assemblyNames.Contains("Microsoft.UI.Xaml"))
            result.Add("WinUI");
        if (assemblyNames.Contains("PresentationFramework") || assemblyNames.Contains("PresentationCore"))
            result.Add("WPF");
        if (assemblyNames.Contains("System.Windows.Forms"))
            result.Add("WinForms");
        if (assemblyNames.Contains("Uno.UI") || assemblyNames.Contains("Uno.Foundation"))
            result.Add("Uno Platform");

        // --- Web frameworks ---
        bool isBlazorWasm = assemblyNames.Contains("Microsoft.AspNetCore.Components.WebAssembly")
            || string.Equals(sdkType, "Microsoft.NET.Sdk.BlazorWebAssembly", StringComparison.OrdinalIgnoreCase);
        bool isBlazorServer = assemblyNames.Contains("Microsoft.AspNetCore.Components.Server");
        bool isBlazor = assemblyNames.Contains("Microsoft.AspNetCore.Components")
            || assemblyNames.Contains("Microsoft.AspNetCore.Components.Web");

        if (isBlazorWasm)
            result.Add("Blazor WebAssembly");
        else if (isBlazorServer)
            result.Add("Blazor Server");
        else if (isBlazor)
            result.Add("Blazor");

        if (assemblyNames.Contains("Microsoft.AspNetCore.Mvc") || assemblyNames.Contains("Microsoft.AspNetCore.Mvc.Core"))
        {
            if (!result.Any(r => r.StartsWith("Blazor")))
                result.Add("ASP.NET Core MVC");
        }

        bool isWebSdk = string.Equals(sdkType, "Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase);
        if (isWebSdk && result.Count == 0)
        {
            if (assemblyNames.Contains("Microsoft.AspNetCore"))
                result.Add("ASP.NET Core");
            else
                result.Add("ASP.NET Core (Web SDK)");
        }

        if (string.Equals(sdkType, "Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase))
            result.Add("Worker Service");

        if (string.Equals(sdkType, "Microsoft.NET.Sdk.Razor", StringComparison.OrdinalIgnoreCase)
            && !result.Any(r => r.StartsWith("Blazor")))
            result.Add("Razor Class Library");

        // Classic ASP.NET / WebForms — detect by actual .aspx/.ascx/.master files,
        // not just System.Web (which is a shim in modern .NET for HttpUtility).
        if (hasWebFormFiles)
            result.Add("ASP.NET (WebForms)");

        // --- Cloud / serverless ---
        if (assemblyNames.Contains("Microsoft.Azure.Functions.Worker")
            || assemblyNames.Contains("Microsoft.Azure.WebJobs"))
            result.Add("Azure Functions");

        if (assemblyNames.Contains("Aspire.Hosting"))
            result.Add(".NET Aspire (AppHost)");
        else if (assemblyNames.Contains("Microsoft.Extensions.ServiceDiscovery")
            || assemblyNames.Contains("Aspire.Dashboard"))
            result.Add(".NET Aspire");

        // --- Popular libraries (as secondary tags) ---
        if (assemblyNames.Contains("Microsoft.EntityFrameworkCore"))
            result.Add("EF Core");
        if (assemblyNames.Contains("Grpc.AspNetCore") || assemblyNames.Contains("Grpc.Net.Client"))
            result.Add("gRPC");
        if (assemblyNames.Contains("HotChocolate.AspNetCore") || assemblyNames.Contains("HotChocolate"))
            result.Add("GraphQL (HotChocolate)");
        else if (assemblyNames.Contains("GraphQL") || assemblyNames.Contains("GraphQL.Server.Core"))
            result.Add("GraphQL");
        if (assemblyNames.Contains("Microsoft.AspNetCore.SignalR") || assemblyNames.Contains("Microsoft.AspNetCore.SignalR.Core"))
            result.Add("SignalR");
        if (assemblyNames.Contains("MediatR"))
            result.Add("MediatR");

        return result;
    }

    /// <summary>
    /// Reads the SDK attribute from a .csproj file (e.g., "Microsoft.NET.Sdk.Web").
    /// </summary>
    private static string? ReadProjectSdk(string projectPath)
    {
        if (!projectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            // Quick read: just look for Sdk= in the <Project> element
            using var reader = new StreamReader(projectPath);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                line = line.TrimStart();
                if (line.StartsWith("<Project", StringComparison.OrdinalIgnoreCase))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(
                        line, """Sdk\s*=\s*["']([^"']+)["']""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (match.Success)
                        return match.Groups[1].Value;
                    break;
                }

                // Also check for <Sdk Name="..."/> import style
                if (line.StartsWith("<Sdk", StringComparison.OrdinalIgnoreCase))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(
                        line, """Name\s*=\s*["']([^"']+)["']""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (match.Success)
                        return match.Groups[1].Value;
                }
            }
        }
        catch
        {
            // Don't fail if we can't read the project file
        }
        return null;
    }

    /// <summary>
    /// Detects the test framework from project references.
    /// </summary>
    internal static string? DetectTestFramework(Project project)
    {
        var assemblyNames = project.MetadataReferences
            .Select(r => Path.GetFileNameWithoutExtension(r.Display ?? ""))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var frameworks = new List<string>();

        if (assemblyNames.Contains("xunit.core") || assemblyNames.Contains("xunit.v3.core") || assemblyNames.Contains("xunit.assert"))
            frameworks.Add("xUnit");
        if (assemblyNames.Contains("nunit.framework"))
            frameworks.Add("NUnit");
        if (assemblyNames.Contains("Microsoft.VisualStudio.TestPlatform.TestFramework"))
            frameworks.Add("MSTest");

        // Also check for common assertion/mocking libraries
        var extras = new List<string>();
        if (assemblyNames.Contains("NSubstitute"))
            extras.Add("NSubstitute");
        if (assemblyNames.Contains("Moq"))
            extras.Add("Moq");
        if (assemblyNames.Contains("FluentAssertions"))
            extras.Add("FluentAssertions");
        if (assemblyNames.Contains("Shouldly"))
            extras.Add("Shouldly");
        if (assemblyNames.Contains("Verify.Xunit") || assemblyNames.Contains("Verify.NUnit") || assemblyNames.Contains("Verify.MSTest"))
            extras.Add("Verify");

        if (frameworks.Count == 0)
            return null;

        string result = string.Join(" + ", frameworks);
        if (extras.Count > 0)
            result += $" (with {string.Join(", ", extras)})";
        return result;
    }
}
