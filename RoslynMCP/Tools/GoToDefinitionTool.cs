using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

/// <summary>
/// Navigates to the definition of a symbol identified by a markup snippet,
/// returning either source context or an external metadata preview.
/// </summary>
[McpServerToolType]
public static class GoToDefinitionTool
{
    /// <summary>
    /// Resolves a symbol via markup targeting and returns its definition location
    /// with a code snippet for context.
    /// </summary>
    [McpServerTool, Description(
        "Go to the definition of a symbol. Provide a code snippet from the file with " +
        "[| |] delimiters around the target symbol, e.g. 'var x = [|Foo|].Bar();'. " +
        "Returns source file context when available, or auto-decompiled source " +
        "for referenced assembly symbols. Works with C# files.")]
    public static async Task<string> GoToDefinition(
        [Description("Path to the file containing the symbol reference.")] string filePath,
        [Description(
            "Code snippet with [| |] markers around the target symbol, " +
            "e.g. 'var x = [|Foo|].Bar();'.")]
        string markupSnippet,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var errors = new StringBuilder();
            var ctx = await ToolHelper.ResolveSymbolAsync(filePath, markupSnippet, errors, cancellationToken);
            if (ctx is null)
                return errors.ToString();

            if (!ctx.IsResolved)
                return ToolHelper.FormatResolutionError(ctx.Resolution);

            return await FormatDefinitionAsync(ctx.Symbol!, ctx.Project, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[GoToDefinition] Unhandled error: {ex}");
            return $"Error: {ex.Message}";
        }
    }

    private static async Task<string> FormatDefinitionAsync(
        ISymbol symbol, Project project, CancellationToken cancellationToken)
    {
        // For constructors, use the containing type name as the display header
        string displayName = symbol is IMethodSymbol { MethodKind: Microsoft.CodeAnalysis.MethodKind.Constructor }
            ? symbol.ContainingType?.Name ?? symbol.Name
            : symbol.Name;

        var sb = new StringBuilder();
        sb.AppendLine($"# Definition: {displayName}");
        sb.AppendLine();

        sb.AppendLine($"- **Symbol**: {symbol.ToDisplayString()}");
        sb.AppendLine($"- **Kind**: {symbol.Kind}");
        sb.AppendLine($"- **Accessibility**: {symbol.DeclaredAccessibility}");

        if (symbol.ContainingType is not null)
            sb.AppendLine($"- **Containing Type**: {symbol.ContainingType.ToDisplayString()}");

        if (symbol.ContainingNamespace is { IsGlobalNamespace: false })
            sb.AppendLine($"- **Namespace**: {symbol.ContainingNamespace.ToDisplayString()}");

        SymbolFormatter.AppendXmlDocs(sb, symbol);

        sb.AppendLine();

        var sourceLocations = symbol.Locations.Where(l => l.IsInSource).ToList();
        var metadataLocations = symbol.Locations.Where(l => l.IsInMetadata).ToList();

        if (sourceLocations.Count > 0)
        {
            await AppendLocationsAsync(
                sb,
                sourceLocations,
                project.Solution,
                "Source Location",
                provenance: null,
                assemblyPath: null,
                cancellationToken);
        }
        else if (metadataLocations.Count > 0)
        {
            var decompiled = await DecompiledSourceService.TryDecompileSymbolAsync(
                symbol,
                project,
                cancellationToken);

            if (decompiled is not null)
            {
                var decompiledLocations = decompiled.Locations.ToList();
                if (decompiledLocations.Count > 0)
                {
                    await AppendLocationsAsync(
                        sb,
                        decompiledLocations,
                        decompiled.Project.Solution,
                        "Decompiled Source",
                        provenance: "auto-decompiled",
                        assemblyPath: decompiled.AssemblyPath,
                        cancellationToken);
                }
                else
                {
                    sb.Append(MetadataSourceFormatter.FormatExternalDefinition(symbol));
                }
            }
            else
            {
                sb.Append(MetadataSourceFormatter.FormatExternalDefinition(symbol));
            }
        }
        else
        {
            sb.AppendLine("No definition location available.");
        }

        return sb.ToString();
    }

    private static async Task AppendLocationsAsync(
        StringBuilder sb,
        IReadOnlyList<Location> locations,
        Solution solution,
        string sectionTitle,
        string? provenance,
        string? assemblyPath,
        CancellationToken cancellationToken)
    {
        for (int i = 0; i < locations.Count; i++)
        {
            var location = locations[i];
            string header = locations.Count > 1
                ? $"## {sectionTitle} {i + 1}"
                : $"## {sectionTitle}";
            sb.AppendLine(header);
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(provenance) && !string.IsNullOrWhiteSpace(assemblyPath))
            {
                sb.AppendLine($"**Provenance**: {provenance} from `{Path.GetFileNameWithoutExtension(assemblyPath)}`");
                sb.AppendLine($"**Assembly Path**: {assemblyPath}");
            }

            var lineSpan = location.GetLineSpan();
            int line = lineSpan.StartLinePosition.Line + 1;
            string defFile = lineSpan.Path;

            sb.AppendLine($"**File**: {defFile}");
            sb.AppendLine($"**Line**: {line}");
            sb.AppendLine();

            await AppendCodeContextAsync(sb, location, solution, cancellationToken);
        }
    }

    private static async Task AppendCodeContextAsync(
        StringBuilder sb, Location location, Solution solution, CancellationToken cancellationToken)
    {
        var tree = location.SourceTree;
        if (tree is null) return;

        var lineSpan = location.GetLineSpan();
        int targetLine = lineSpan.StartLinePosition.Line; // 0-based

        var documentId = solution.GetDocumentIdsWithFilePath(tree.FilePath).FirstOrDefault();
        if (documentId is null) return;

        var doc = solution.GetDocument(documentId);
        if (doc is null) return;

        var text = await doc.GetTextAsync(cancellationToken);
        int contextStart = Math.Max(0, targetLine - 2);
        int contextEnd = Math.Min(text.Lines.Count - 1, targetLine + 2);

        sb.AppendLine("```csharp");
        for (int i = contextStart; i <= contextEnd; i++)
        {
            string lineText = text.Lines[i].ToString();
            int lineNum = i + 1;
            sb.AppendLine(lineNum == targetLine + 1
                ? $"{lineNum}: > {lineText}"
                : $"{lineNum}:   {lineText}");
        }
        sb.AppendLine("```");
        sb.AppendLine();
    }
}
