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
        IOutputFormatter fmt,
        [Description("Maximum number of references to return. Default: 100.")]
        int maxResults = 100,
        [Description("Approximate line number near the target snippet. Used to pick the closest match when the snippet appears multiple times.")]
        int? hintLine = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Route ASPX files to a dedicated handler that resolves symbols from markup
            if (!string.IsNullOrWhiteSpace(filePath) && !string.IsNullOrWhiteSpace(markupSnippet))
            {
                var normalizedForAspx = PathHelper.NormalizePath(filePath);
                if (AspxSourceMappingService.IsAspxFile(normalizedForAspx))
                    return await FindUsagesFromAspxAsync(normalizedForAspx, markupSnippet, maxResults, fmt, cancellationToken);
            }

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
                razorSourceMap, aspxIndex, crossProjectRefs, maxResults, fmt, cancellationToken);
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

    /// <summary>
    /// Handles <c>FindUsages</c> when the source file is an ASPX/ASCX/master page.
    /// Resolves the markup snippet to a symbol (control type, event handler, property, or
    /// control ID field) and returns a usage report that also includes
    /// <c>FindControl("id")</c> call sites from wrapper utility methods.
    /// </summary>
    private static async Task<string> FindUsagesFromAspxAsync(
        string systemPath,
        string markupSnippet,
        int maxResults,
        IOutputFormatter fmt,
        CancellationToken cancellationToken)
    {
        if (!MarkupString.TryParse(markupSnippet, out var markup, out string? parseError))
            return $"Error: Invalid markup snippet. {parseError}";

        if (!File.Exists(systemPath))
            return $"Error: File {systemPath} does not exist.";

        string? projectPath = await NonCSharpProjectFinder.FindProjectAsync(systemPath, cancellationToken);
        if (string.IsNullOrEmpty(projectPath))
            return "Error: Couldn't find a project containing this file.";

        var (workspace, project) = await WorkspaceService.GetOrOpenProjectAsync(
            projectPath, targetFilePath: systemPath, cancellationToken: cancellationToken);

        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation is null)
            return "Error: Unable to get compilation for the project.";

        string? projectDir = Path.GetDirectoryName(projectPath);
        var webConfigNamespaces = projectDir is not null
            ? AspxSourceMappingService.LoadWebConfigNamespaces(projectDir)
            : default;

        string fileText = await File.ReadAllTextAsync(systemPath, cancellationToken);
        var parseResult = AspxSourceMappingService.Parse(
            systemPath, fileText, compilation,
            namespaces: webConfigNamespaces.IsDefaultOrEmpty ? null : webConfigNamespaces,
            rootDirectory: projectDir);

        var symbol = AspxSourceMappingService.ResolveAspxSymbol(parseResult, fileText, markup!);

        // Determine control ID (the string ID of the control in markup)
        string? controlId = null;
        if (symbol is IFieldSymbol)
            controlId = symbol.Name;

        if (controlId is null)
        {
            var controlNode = AspxSourceMappingService.FindControlNodeAtCursor(parseResult, fileText, markup!);
            if (controlNode?.Id is not null)
                controlId = controlNode.Id;
        }

        // Search for FindControl("id") calls and wrapper method calls
        List<AspxSymbolReference> findControlRefs = [];
        if (controlId is not null)
        {
            var wrappers = await AspxSourceMappingService.FindControlAccessorMethodsAsync(project, cancellationToken);
            findControlRefs = await AspxSourceMappingService.FindControlByIdAsync(
                project, controlId, wrappers, cancellationToken);
        }

        if (symbol is null && controlId is null)
            return $"No symbol found for '{markup!.MarkedText}' in ASPX file.";

        var aspxIndex = await ProjectIndexCacheService.GetAspxIndexAsync(project, cancellationToken);

        // Template-nested control: no code-behind field, only FindControl search
        if (symbol is null)
        {
            return FormatControlIdOnlyResults(
                controlId!, findControlRefs, aspxIndex, systemPath, projectPath, fmt);
        }

        // Resolved symbol: run full Roslyn FindReferences
        var references = await SymbolFinder.FindReferencesAsync(
            symbol, workspace.CurrentSolution, cancellationToken);

        var razorSourceMap = await ProjectIndexCacheService.GetRazorSourceMapAsync(project, cancellationToken);
        string searchSummary = controlId is not null
            ? $"Markup target: `{markup!.MarkedText}` (ASPX control ID)"
            : $"Markup target: `{markup!.MarkedText}`";

        return await FormatResultsAsync(
            symbol, references, systemPath, searchSummary, projectPath,
            razorSourceMap, aspxIndex,
            crossProjectRefs: [],
            maxResults, fmt, cancellationToken,
            findControlRefs: findControlRefs,
            controlId: controlId);
    }

    /// <summary>
    /// Formats results when a template-nested control ID is searched: no code-behind field exists,
    /// so only <c>FindControl("id")</c> call sites and ASPX expression references are shown.
    /// </summary>
    private static string FormatControlIdOnlyResults(
        string controlId,
        List<AspxSymbolReference> findControlRefs,
        AspxProjectIndex aspxIndex,
        string filePath,
        string projectPath,
        IOutputFormatter fmt)
    {
        var results = new StringBuilder();

        fmt.AppendHeader(results, "Control ID References");

        fmt.AppendHeader(results, "Search Information", level: 2);
        fmt.AppendField(results, "File", filePath);
        fmt.AppendField(results, "Control ID", controlId);
        fmt.AppendField(results, "Project", Path.GetFileName(projectPath));
        fmt.AppendField(results, "Note",
            "Control is inside a Repeater/DataList template — no code-behind field; accessed via FindControl at runtime");
        fmt.AppendSeparator(results);

        if (findControlRefs.Count > 0)
        {
            fmt.AppendHeader(results, "FindControl References", level: 2);
            fmt.AppendField(results, "Found", $"{findControlRefs.Count} FindControl(\"{controlId}\") call(s) (including wrapper methods)");
            fmt.AppendSeparator(results);

            var rows = new List<string[]>();
            foreach (var fcRef in findControlRefs)
            {
                var snippet = fcRef.CodeSnippet.Length > 80
                    ? fcRef.CodeSnippet[..77] + "..."
                    : fcRef.CodeSnippet;
                rows.Add([fcRef.FilePath, $"{fcRef.Line}", snippet]);
            }
            fmt.AppendTable(results, "FindControl Calls", ["File", "Line", "Snippet"], rows);
        }
        else
        {
            fmt.AppendHeader(results, "FindControl References", level: 2);
            fmt.AppendField(results, "Found", "None");
            fmt.AppendSeparator(results);
        }

        var aspxRefs = AspxSourceMappingService.FindSymbolReferences(aspxIndex, controlId);
        if (aspxRefs.Count > 0)
        {
            fmt.AppendHeader(results, "ASPX References", level: 2);
            var aspxRows = new List<string[]>();
            foreach (var aspxRef in aspxRefs)
            {
                var locType = aspxRef.LocationType == AspxCodeLocationType.Expression ? "Expression" : "Code Block";
                var snippet = aspxRef.CodeSnippet.Length > 80
                    ? aspxRef.CodeSnippet[..77] + "..."
                    : aspxRef.CodeSnippet;
                aspxRows.Add([Path.GetFileName(aspxRef.FilePath), $"{aspxRef.Line}", locType, snippet]);
            }
            fmt.AppendTable(results, "ASPX", ["File", "Line", "Type", "Snippet"], aspxRows);
        }

        fmt.AppendHeader(results, "Summary", level: 2);
        fmt.AppendField(results, "Control ID", $"`{controlId}`");
        fmt.AppendField(results, "FindControl calls", findControlRefs.Count);
        fmt.AppendField(results, "ASPX references", aspxRefs.Count);
        fmt.AppendSeparator(results);
        fmt.AppendHints(results, "Use get_call_hierarchy on a FindControl call site to trace the full caller chain");

        return results.ToString();
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
        IOutputFormatter fmt,
        CancellationToken cancellationToken,
        List<AspxSymbolReference>? findControlRefs = null,
        string? controlId = null)
    {
        var refList = references.ToList();
        var results = new StringBuilder();

        fmt.AppendHeader(results, "Symbol Usage Analysis");

        fmt.AppendHeader(results, "Search Information", level: 2);
        fmt.AppendField(results, "File", filePath);
        fmt.AppendField(results, "Position", searchSummary);
        fmt.AppendField(results, "Project", Path.GetFileName(projectPath));
        fmt.AppendSeparator(results);

        AppendSymbolDetails(results, symbol, fmt);

        int totalLocations = refList.Sum(r => r.Locations.Count());

        fmt.AppendHeader(results, "References", level: 2);
        fmt.AppendField(results, "Found", $"{totalLocations} reference(s) across {refList.Count} symbol definition(s)");
        fmt.AppendSeparator(results);

        int referenceCount = 1;
        foreach (var reference in refList)
        {
            if (referenceCount > maxResults)
            {
                fmt.AppendTruncation(results, maxResults, totalLocations);
                break;
            }

            fmt.AppendHeader(results, $"Reference Definition: {reference.Definition.ToDisplayString()}", level: 3);

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
                    fmt,
                    cancellationToken);
                referenceCount++;
            }
        }

        if (totalLocations > MaxCodeContextReferences)
        {
            fmt.AppendHints(results,
                $"Code context shown for the first {MaxCodeContextReferences} references; " +
                $"omitted for the remaining {totalLocations - MaxCodeContextReferences}.");
        }

        // Append cross-project references
        int crossProjectCount = crossProjectRefs.Sum(r => r.Locations.Count);
        if (crossProjectRefs.Count > 0)
        {
            fmt.AppendHeader(results, "Cross-Project References", level: 2);
            fmt.AppendField(results, "Found", $"{crossProjectCount} reference(s) in {crossProjectRefs.Count} referencing project(s)");
            fmt.AppendSeparator(results);

            foreach (var (projectName, locations) in crossProjectRefs)
            {
                var rows = new List<string[]>();
                foreach (var location in locations.Take(maxResults - referenceCount + 1))
                {
                    if (referenceCount > maxResults)
                        break;
                    var lineSpan = location.Location.GetLineSpan();
                    var path = location.Document.FilePath ?? lineSpan.Path;
                    rows.Add([path, $"{lineSpan.StartLinePosition.Line + 1}", $"{lineSpan.StartLinePosition.Character + 1}"]);
                    referenceCount++;
                }
                fmt.AppendTable(results, projectName, ["File", "Line", "Column"], rows);
                if (referenceCount > maxResults)
                {
                    fmt.AppendTruncation(results, maxResults, totalLocations + crossProjectCount);
                }
            }
        }

        // Append ASPX references
        var aspxRefs = AspxSourceMappingService.FindSymbolReferences(aspxIndex, symbol.Name);
        if (aspxRefs.Count > 0)
        {
            fmt.AppendHeader(results, "ASPX References", level: 2);
            fmt.AppendField(results, "Found", $"{aspxRefs.Count} potential references in ASPX/ASCX files");
            fmt.AppendSeparator(results);

            var aspxRows = new List<string[]>();
            foreach (var aspxRef in aspxRefs)
            {
                var locType = aspxRef.LocationType == AspxCodeLocationType.Expression
                    ? "Expression" : "Code Block";
                var snippet = aspxRef.CodeSnippet.Length > 80
                    ? aspxRef.CodeSnippet[..77] + "..."
                    : aspxRef.CodeSnippet;
                aspxRows.Add([Path.GetFileName(aspxRef.FilePath), $"{aspxRef.Line}", locType, snippet]);
            }
            fmt.AppendTable(results, "ASPX", ["File", "Line", "Type", "Snippet"], aspxRows);
        }

        // Append FindControl references (for control ID searches)
        if (findControlRefs is { Count: > 0 })
        {
            string idLabel = controlId ?? symbol.Name;
            fmt.AppendHeader(results, "FindControl References", level: 2);
            fmt.AppendField(results, "Found",
                $"{findControlRefs.Count} FindControl(\"{idLabel}\") call(s) (including wrapper methods)");
            fmt.AppendSeparator(results);

            var findControlRows = new List<string[]>();
            foreach (var fcRef in findControlRefs)
            {
                var snippet = fcRef.CodeSnippet.Length > 80
                    ? fcRef.CodeSnippet[..77] + "..."
                    : fcRef.CodeSnippet;
                findControlRows.Add([fcRef.FilePath, $"{fcRef.Line}", snippet]);
            }
            fmt.AppendTable(results, "FindControl Calls", ["File", "Line", "Snippet"], findControlRows);
        }

        fmt.AppendHeader(results, "Summary", level: 2);
        int aspxCount = aspxRefs.Count;
        int findControlCount = findControlRefs?.Count ?? 0;
        int totalRefCount = totalLocations + crossProjectCount + aspxCount + findControlCount;
        fmt.AppendField(results, "Symbol", $"`{symbol.Name}` ({symbol.Kind})");
        fmt.AppendField(results, "Total reference count", totalRefCount);
        var summaryParts = new List<string>
        {
            $"{totalLocations} C# reference(s) across {refList.Count} symbol definition(s)"
        };
        if (crossProjectCount > 0)
            summaryParts.Add($"{crossProjectCount} cross-project references");
        if (aspxCount > 0)
            summaryParts.Add($"{aspxCount} ASPX references");
        if (findControlCount > 0)
            summaryParts.Add($"{findControlCount} FindControl call(s)");
        fmt.AppendField(results, "Breakdown", string.Join(", ", summaryParts));
        fmt.AppendSeparator(results);

        fmt.AppendHints(results, "Use GoToDefinition on a reference to see its context");

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

    private static void AppendSymbolDetails(StringBuilder results, ISymbol symbol, IOutputFormatter fmt)
    {
        fmt.AppendHeader(results, "Symbol Details", level: 2);
        fmt.AppendField(results, "Name", symbol.Name);
        fmt.AppendField(results, "Kind", symbol.Kind);
        fmt.AppendField(results, "Full Name", symbol.ToDisplayString());

        if (symbol.ContainingType != null)
            fmt.AppendField(results, "Containing Type", symbol.ContainingType.ToDisplayString());

        if (symbol.ContainingNamespace is { IsGlobalNamespace: false })
            fmt.AppendField(results, "Namespace", symbol.ContainingNamespace.ToDisplayString());

        fmt.AppendField(results, "Accessibility", symbol.DeclaredAccessibility);

        switch (symbol)
        {
            case IMethodSymbol method:
                fmt.AppendField(results, "Return Type", method.ReturnType.ToDisplayString());
                fmt.AppendField(results, "Is Extension Method", method.IsExtensionMethod);
                fmt.AppendField(results, "Parameter Count", method.Parameters.Length);
                break;
            case IPropertySymbol property:
                fmt.AppendField(results, "Property Type", property.Type.ToDisplayString());
                fmt.AppendField(results, "Has Getter", property.GetMethod != null);
                fmt.AppendField(results, "Has Setter", property.SetMethod != null);
                break;
            case IFieldSymbol field:
                fmt.AppendField(results, "Field Type", field.Type.ToDisplayString());
                fmt.AppendField(results, "Is Const", field.IsConst);
                fmt.AppendField(results, "Is Static", field.IsStatic);
                break;
            case IEventSymbol evt:
                fmt.AppendField(results, "Event Type", evt.Type.ToDisplayString());
                break;
            case IParameterSymbol param:
                fmt.AppendField(results, "Parameter Type", param.Type.ToDisplayString());
                fmt.AppendField(results, "Is Optional", param.IsOptional);
                break;
            case ILocalSymbol local:
                fmt.AppendField(results, "Local Type", local.Type.ToDisplayString());
                fmt.AppendField(results, "Is Const", local.IsConst);
                break;
        }

        fmt.AppendSeparator(results);
    }

    private static async Task AppendReferenceLocationAsync(
        StringBuilder results, ReferenceLocation location, int referenceCount,
        bool includeCodeContext,
        RazorMappedLocation? razorMapping,
        IOutputFormatter fmt,
        CancellationToken cancellationToken)
    {
        var linePosition = location.Location.GetLineSpan();
        int refLine = linePosition.StartLinePosition.Line + 1;
        int refColumn = linePosition.StartLinePosition.Character + 1;
        string locationPath = location.Document.FilePath ?? linePosition.Path;
        string formattedLocation = $"{locationPath}:{refLine}:{refColumn}";

        fmt.AppendHeader(results, $"Reference {referenceCount}: {formattedLocation}", level: 4);

        if (razorMapping is not null)
        {
            fmt.AppendField(results, "Razor source", $"{razorMapping.RazorFilePath}:{razorMapping.Line}");
        }

        if (!includeCodeContext)
        {
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
