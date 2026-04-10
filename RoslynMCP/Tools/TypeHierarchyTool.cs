using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

/// <summary>
/// Shows the full type hierarchy (base types upward + derived types downward)
/// for a given type symbol.
/// </summary>
[McpServerToolType]
public static class TypeHierarchyTool
{
    [McpServerTool, Description(
        "Show the full type hierarchy for a class or interface. " +
        "Displays base classes and interfaces going upward, and derived/implementing types going downward. " +
        "Provide a code snippet with [| |] delimiters around the type name.")]
    public static async Task<string> GetTypeHierarchy(
        [Description("Path to the file containing the type.")] string filePath,
        [Description(
            "Code snippet with [| |] markers around the type, " +
            "e.g. 'class [|MyService|] : IService'.")]
        string markupSnippet,
        IOutputFormatter fmt,
        [Description("Approximate line number near the target snippet. Used to pick the closest match when the snippet appears multiple times.")]
        int? hintLine = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var errors = new StringBuilder();
            var ctx = await ToolHelper.ResolveSymbolAsync(filePath, markupSnippet, errors, cancellationToken, hintLine);
            if (ctx is null)
                return errors.ToString();

            if (!ctx.IsResolved)
                return ToolHelper.FormatResolutionError(ctx.Resolution);

            if (ctx.Symbol is not INamedTypeSymbol typeSymbol)
                return $"'{ctx.Symbol!.Name}' is not a type. This tool works with classes, interfaces, structs, and enums.";

            return await FormatHierarchyAsync(typeSymbol, ctx.Workspace.CurrentSolution, ctx.Project, fmt, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TypeHierarchy] Unhandled error: {ex}");
            return $"Error: {ex.Message}";
        }
    }

    private static async Task<string> FormatHierarchyAsync(
        INamedTypeSymbol typeSymbol,
        Solution solution,
        Project project,
        IOutputFormatter fmt,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Type Hierarchy: {typeSymbol.Name}");
        sb.AppendLine();
        sb.AppendLine($"- **Type**: {typeSymbol.ToDisplayString()}");
        sb.AppendLine($"- **Kind**: {typeSymbol.TypeKind}");
        sb.AppendLine();

        // Walk base types upward
        sb.AppendLine("## Inheritance Chain (↑ Base Types)");
        sb.AppendLine();

        var baseChain = new List<INamedTypeSymbol>();
        var current = typeSymbol.BaseType;
        while (current is not null && current.SpecialType != SpecialType.System_Object)
        {
            baseChain.Add(current);
            current = current.BaseType;
        }
        if (current?.SpecialType == SpecialType.System_Object)
            baseChain.Add(current);

        if (baseChain.Count > 0)
        {
            // Show from most base to most derived
            for (int i = baseChain.Count - 1; i >= 0; i--)
            {
                string indent = new string(' ', (baseChain.Count - 1 - i) * 2);
                var baseType = baseChain[i];
                string location = GetTypeLocation(baseType);
                sb.AppendLine($"{indent}↳ {baseType.ToDisplayString()} {location}");
            }
            string selfIndent = new string(' ', baseChain.Count * 2);
            sb.AppendLine($"{selfIndent}↳ **{typeSymbol.ToDisplayString()}** ← you are here");
        }
        else
        {
            sb.AppendLine($"↳ **{typeSymbol.ToDisplayString()}** (no base type beyond System.Object)");
        }
        sb.AppendLine();

        // List implemented interfaces
        var interfaces = typeSymbol.AllInterfaces;
        if (interfaces.Length > 0)
        {
            sb.AppendLine("## Implemented Interfaces");
            sb.AppendLine();
            foreach (var iface in interfaces.OrderBy(i => i.ToDisplayString()))
            {
                string location = GetTypeLocation(iface);
                sb.AppendLine($"- {iface.ToDisplayString()} {location}");
            }
            sb.AppendLine();
        }

        // Find derived types downward
        sb.AppendLine("## Derived Types (↓ Subtypes)");
        sb.AppendLine();

        IEnumerable<INamedTypeSymbol> derivedTypes;
        if (typeSymbol.TypeKind == TypeKind.Interface)
        {
            var impls = await SymbolFinder.FindImplementationsAsync(
                typeSymbol, solution, cancellationToken: cancellationToken);
            derivedTypes = impls.OfType<INamedTypeSymbol>();
        }
        else
        {
            derivedTypes = await SymbolFinder.FindDerivedClassesAsync(
                typeSymbol, solution, cancellationToken: cancellationToken);
        }

        var derivedList = derivedTypes.OrderBy(t => t.ToDisplayString()).ToList();
        if (derivedList.Count == 0)
        {
            sb.AppendLine("No derived types found in the current solution.");
        }
        else
        {
            string? projectDir = Path.GetDirectoryName(project.FilePath);
            foreach (var derived in derivedList)
            {
                var loc = derived.Locations.FirstOrDefault(l => l.IsInSource);
                if (loc is not null)
                {
                    var lineSpan = loc.GetLineSpan();
                    string displayPath = projectDir is not null
                        ? Path.GetRelativePath(projectDir, lineSpan.Path)
                        : lineSpan.Path;
                    sb.AppendLine($"- {derived.ToDisplayString()} ({displayPath}:{lineSpan.StartLinePosition.Line + 1})");
                }
                else
                {
                    sb.AppendLine($"- {derived.ToDisplayString()} (external)");
                }
            }
        }
        fmt.AppendHints(sb,
            "Use FindImplementations to see method implementations in derived types",
            "Use GoToDefinition to navigate to a type");

        return sb.ToString();
    }

    private static string GetTypeLocation(INamedTypeSymbol type)
    {
        var loc = type.Locations.FirstOrDefault(l => l.IsInSource);
        if (loc is not null)
        {
            var lineSpan = loc.GetLineSpan();
            return $"({Path.GetFileName(lineSpan.Path)}:{lineSpan.StartLinePosition.Line + 1})";
        }
        if (type.Locations.Any(l => l.IsInMetadata))
            return "(external)";
        return "";
    }
}
