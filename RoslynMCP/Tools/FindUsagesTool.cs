using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

[McpServerToolType]
public static class FindUsagesTool
{
    private const int MaxCodeContextReferences = 3;

    /// <summary>
    /// Finds all references to a symbol identified by a markup snippet with
    /// <c>[| |]</c> delimiters around the target symbol.
    /// </summary>
    [McpServerTool, Description(
        "Find all references to a symbol. Provide a code snippet from the file with " +
        "[| |] delimiters around the target symbol, e.g. 'var x = [|Foo|].Bar();'. " +
        "Also searches Razor source-generated files and ASPX inline code. " +
        "Whitespace differences between the snippet and the file are tolerated.")]
    public static async Task<string> FindUsages(
        [Description("Path to the file.")] string filePath,
        [Description(
            "Code snippet with [| |] markers around the target symbol, " +
            "e.g. 'var x = [|Foo|].Bar();'.")]
        string markupSnippet,
        [Description("Maximum number of references to return. Default: 100.")]
        int maxResults = 100,
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

            var symbol = ctx.Symbol!;

            var references = await SymbolFinder.FindReferencesAsync(
                symbol, ctx.Workspace.CurrentSolution, cancellationToken);

            // Build Razor source map for mapping generated references
            var razorSourceMap = await ProjectIndexCacheService.GetRazorSourceMapAsync(ctx.Project, cancellationToken);

            // Build ASPX index for searching inline code references
            var aspxIndex = await ProjectIndexCacheService.GetAspxIndexAsync(ctx.Project, cancellationToken);

            // Search referencing projects for cross-project usages
            var crossProjectRefs = new List<(string ProjectName, List<ReferenceLocation> Locations)>();
            if (ctx.Project.FilePath is not null)
            {
                var referencingProjects = WorkspaceService.FindReferencingProjects(ctx.Project.FilePath);
                foreach (var refProjectPath in referencingProjects)
                {
                    try
                    {
                        var (refWorkspace, refProject) = await WorkspaceService.GetOrOpenProjectAsync(
                            refProjectPath, diagnosticWriter: TextWriter.Null, cancellationToken: cancellationToken);

                        var refSolution = refWorkspace.CurrentSolution;
                        var refDoc = WorkspaceService.FindDocumentInProject(refProject, ctx.SystemPath)
                            ?? FindDocumentInSolution(refSolution, ctx.SystemPath);

                        if (refDoc is null) continue;

                        var refModel = await refDoc.GetSemanticModelAsync(cancellationToken);
                        var refRoot = await refDoc.GetSyntaxRootAsync(cancellationToken);
                        if (refModel is null || refRoot is null) continue;

                        var originalLocation = symbol.Locations.FirstOrDefault(l => l.IsInSource);
                        if (originalLocation is null) continue;

                        var node = refRoot.FindNode(originalLocation.SourceSpan);
                        var refSymbol = refModel.GetDeclaredSymbol(node, cancellationToken)
                            ?? refModel.GetSymbolInfo(node, cancellationToken).Symbol;

                        if (refSymbol is null) continue;

                        var refResults = await SymbolFinder.FindReferencesAsync(refSymbol, refSolution, cancellationToken);
                        var locations = refResults.SelectMany(r => r.Locations).ToList();
                        if (locations.Count > 0)
                        {
                            crossProjectRefs.Add((Path.GetFileNameWithoutExtension(refProjectPath), locations));
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[FindUsages] Error searching referencing project '{refProjectPath}': {ex.Message}");
                    }
                }
            }

            string searchSummary = $"Markup target: `{ctx.Markup.MarkedText}`";
            return await FormatResultsAsync(
                symbol, references, ctx.SystemPath, searchSummary, ctx.Project.FilePath!,
                razorSourceMap, aspxIndex, crossProjectRefs, maxResults, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FindUsages] Unhandled error: {ex}");
            return $"Error finding usages: {ex.Message}";
        }
    }

    private static Document? FindDocumentInSolution(Solution solution, string filePath)
    {
        foreach (var project in solution.Projects)
        {
            var doc = WorkspaceService.FindDocumentInProject(project, filePath);
            if (doc is not null) return doc;
        }
        return null;
    }

    /// <summary>
    /// Formats the symbol and reference data into the markdown report returned to the caller.
    /// </summary>
    private static async Task<string> FormatResultsAsync(
        ISymbol symbol,
        IEnumerable<ReferencedSymbol> references,
        string filePath,
        string searchSummary,
        string projectPath,
        RazorSourceMap razorSourceMap,
        AspxProjectIndex aspxIndex,
        List<(string ProjectName, List<ReferenceLocation> Locations)> crossProjectRefs,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var refList = references.ToList();
        var results = new StringBuilder();

        results.AppendLine("# Symbol Usage Analysis");
        results.AppendLine();
        results.AppendLine("## Search Information");
        results.AppendLine($"- **File**: {filePath}");
        results.AppendLine($"- **Position**: {searchSummary}");
        results.AppendLine($"- **Project**: {Path.GetFileName(projectPath)}");
        results.AppendLine();

        AppendSymbolDetails(results, symbol);

        int totalLocations = refList.Sum(r => r.Locations.Count());

        results.AppendLine("## References");
        results.AppendLine(
            $"Found {totalLocations} reference(s) across {refList.Count} symbol definition(s).");
        results.AppendLine();

        int referenceCount = 1;
        foreach (var reference in refList)
        {
            if (referenceCount > maxResults)
            {
                results.AppendLine($"_Showing first {maxResults} of {totalLocations} references. Use `maxResults` to see more._");
                results.AppendLine();
                break;
            }

            results.AppendLine($"### Reference Definition: {reference.Definition.ToDisplayString()}");

            foreach (var location in reference.Locations)
            {
                if (referenceCount > maxResults) break;

                // Check if this reference is in a Razor source-generated file
                var mappedRazor = TryMapRazorReference(location, razorSourceMap);

                await AppendReferenceLocationAsync(
                    results,
                    location,
                    referenceCount,
                    includeCodeContext: referenceCount <= MaxCodeContextReferences,
                    mappedRazor,
                    cancellationToken);
                referenceCount++;
            }
        }

        if (totalLocations > MaxCodeContextReferences)
        {
            results.AppendLine(
                $"_Code context shown for the first {MaxCodeContextReferences} references; " +
                $"omitted for the remaining {totalLocations - MaxCodeContextReferences}._");
            results.AppendLine();
        }

        // Append cross-project references
        int crossProjectCount = crossProjectRefs.Sum(r => r.Locations.Count);
        if (crossProjectRefs.Count > 0)
        {
            results.AppendLine("## Cross-Project References");
            results.AppendLine(
                $"Found {crossProjectCount} reference(s) in {crossProjectRefs.Count} referencing project(s).");
            results.AppendLine();

            foreach (var (projectName, locations) in crossProjectRefs)
            {
                results.AppendLine($"### {projectName}");
                foreach (var location in locations.Take(maxResults - referenceCount + 1))
                {
                    if (referenceCount > maxResults)
                    {
                        results.AppendLine($"_Truncated. Use `maxResults` to see more._");
                        break;
                    }
                    var lineSpan = location.Location.GetLineSpan();
                    var path = location.Document.FilePath ?? lineSpan.Path;
                    results.AppendLine(
                        $"- {path}:{lineSpan.StartLinePosition.Line + 1}:{lineSpan.StartLinePosition.Character + 1}");
                    referenceCount++;
                }
                results.AppendLine();
            }
        }

        // Append ASPX references
        var aspxRefs = AspxSourceMappingService.FindSymbolReferences(aspxIndex, symbol.Name);
        if (aspxRefs.Count > 0)
        {
            results.AppendLine("## ASPX References");
            results.AppendLine(
                $"Found {aspxRefs.Count} potential references in ASPX/ASCX files.");
            results.AppendLine();

            foreach (var aspxRef in aspxRefs)
            {
                var locType = aspxRef.LocationType == AspxCodeLocationType.Expression
                    ? "Expression" : "Code Block";
                results.AppendLine(
                    $"- **{Path.GetFileName(aspxRef.FilePath)}**:{aspxRef.Line} ({locType})");
                var snippet = aspxRef.CodeSnippet.Length > 80
                    ? aspxRef.CodeSnippet[..77] + "..."
                    : aspxRef.CodeSnippet;
                results.AppendLine($"  `{snippet}`");
            }

            results.AppendLine();
        }

        results.AppendLine("## Summary");
        int aspxCount = aspxRefs.Count;
        var summaryParts = new List<string>
        {
            $"{totalLocations} C# reference(s) across {refList.Count} symbol definition(s)"
        };
        if (crossProjectCount > 0)
            summaryParts.Add($"{crossProjectCount} cross-project references");
        if (aspxCount > 0)
            summaryParts.Add($"{aspxCount} ASPX references");

        results.AppendLine(
            $"Symbol `{symbol.Name}` of type `{symbol.Kind}` has {string.Join(", ", summaryParts)}.");

        return results.ToString();
    }

    /// <summary>
    /// Attempts to map a reference location from Razor-generated code back to the original .razor file.
    /// </summary>
    private static RazorMappedLocation? TryMapRazorReference(
        ReferenceLocation location, RazorSourceMap sourceMap)
    {
        var docPath = location.Document.FilePath;
        if (string.IsNullOrEmpty(docPath))
            return null;

        // Check if this is a source-generated Razor document
        var docName = Path.GetFileName(docPath);
        if (!docName.EndsWith(".razor.g.cs", StringComparison.OrdinalIgnoreCase) &&
            !docName.EndsWith(".cshtml.g.cs", StringComparison.OrdinalIgnoreCase))
            return null;

        var lineSpan = location.Location.GetLineSpan();
        int generatedLine = lineSpan.StartLinePosition.Line + 1; // Convert to 1-indexed

        return RazorSourceMappingService.MapGeneratedToRazor(sourceMap, docPath, generatedLine);
    }

    private static void AppendSymbolDetails(StringBuilder results, ISymbol symbol)
    {
        results.AppendLine("## Symbol Details");
        results.AppendLine($"- **Name**: {symbol.Name}");
        results.AppendLine($"- **Kind**: {symbol.Kind}");
        results.AppendLine($"- **Full Name**: {symbol.ToDisplayString()}");

        if (symbol.ContainingType != null)
            results.AppendLine($"- **Containing Type**: {symbol.ContainingType.ToDisplayString()}");

        if (symbol.ContainingNamespace is { IsGlobalNamespace: false })
            results.AppendLine($"- **Namespace**: {symbol.ContainingNamespace.ToDisplayString()}");

        results.AppendLine($"- **Accessibility**: {symbol.DeclaredAccessibility}");

        switch (symbol)
        {
            case IMethodSymbol method:
                results.AppendLine($"- **Return Type**: {method.ReturnType.ToDisplayString()}");
                results.AppendLine($"- **Is Extension Method**: {method.IsExtensionMethod}");
                results.AppendLine($"- **Parameter Count**: {method.Parameters.Length}");
                break;
            case IPropertySymbol property:
                results.AppendLine($"- **Property Type**: {property.Type.ToDisplayString()}");
                results.AppendLine($"- **Has Getter**: {property.GetMethod != null}");
                results.AppendLine($"- **Has Setter**: {property.SetMethod != null}");
                break;
            case IFieldSymbol field:
                results.AppendLine($"- **Field Type**: {field.Type.ToDisplayString()}");
                results.AppendLine($"- **Is Const**: {field.IsConst}");
                results.AppendLine($"- **Is Static**: {field.IsStatic}");
                break;
            case IEventSymbol evt:
                results.AppendLine($"- **Event Type**: {evt.Type.ToDisplayString()}");
                break;
            case IParameterSymbol param:
                results.AppendLine($"- **Parameter Type**: {param.Type.ToDisplayString()}");
                results.AppendLine($"- **Is Optional**: {param.IsOptional}");
                break;
            case ILocalSymbol local:
                results.AppendLine($"- **Local Type**: {local.Type.ToDisplayString()}");
                results.AppendLine($"- **Is Const**: {local.IsConst}");
                break;
        }

        results.AppendLine();
    }

    private static async Task AppendReferenceLocationAsync(
        StringBuilder results, ReferenceLocation location, int referenceCount,
        bool includeCodeContext,
        RazorMappedLocation? razorMapping,
        CancellationToken cancellationToken)
    {
        var linePosition = location.Location.GetLineSpan();
        int refLine = linePosition.StartLinePosition.Line + 1;
        int refColumn = linePosition.StartLinePosition.Character + 1;
        string locationPath = location.Document.FilePath ?? linePosition.Path;
        string formattedLocation = $"{locationPath}:{refLine}:{refColumn}";

        results.AppendLine($"#### Reference {referenceCount}: {formattedLocation}");

        if (razorMapping is not null)
        {
            results.AppendLine(
                $"  ↳ **Razor source**: {razorMapping.RazorFilePath}:{razorMapping.Line}");
        }

        if (!includeCodeContext)
        {
            results.AppendLine();
            return;
        }

        try
        {
            var refSourceText = await location.Document.GetTextAsync(cancellationToken);
            int contextStart = Math.Max(0, refLine - 3);
            int contextEnd = Math.Min(refSourceText.Lines.Count - 1, refLine + 1);

            results.AppendLine("```csharp");

            for (int i = contextStart; i <= contextEnd; i++)
            {
                string lineText = refSourceText.Lines[i].ToString();
                int lineNumber = i + 1;

                results.AppendLine(lineNumber == refLine
                    ? $"{lineNumber}: > {lineText}"
                    : $"{lineNumber}:   {lineText}");
            }

            results.AppendLine("```");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            results.AppendLine($"Code context unavailable: {ex.Message}");
        }

        results.AppendLine();
    }
}
