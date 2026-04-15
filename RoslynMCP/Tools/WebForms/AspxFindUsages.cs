using System.Text;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMCP.Services;

namespace RoslynMCP.Tools.WebForms;

/// <summary>
/// Resolves FindUsages requests originating from ASPX/ASCX/master-page files by
/// parsing the WebForms markup tree and matching the marked text to controls,
/// properties, events, or control ID fields.
/// </summary>
internal class AspxFindUsages(IOutputFormatter fmt) : IFindUsagesHandler
{
    public bool CanHandle(string filePath) => AspxSourceMappingService.IsAspxFile(filePath);

    public async Task<string> FindUsagesAsync(
        string systemPath, string markupSnippet, int maxResults,
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
        if (symbol is Microsoft.CodeAnalysis.IFieldSymbol)
            controlId = symbol.Name;

        if (controlId is null)
        {
            var controlNode = AspxSourceMappingService.FindControlNodeAtCursor(parseResult, fileText, markup!);
            if (controlNode?.Id is not null)
                controlId = controlNode.Id;
        }

        // Search for FindControl("id") calls and wrapper method calls.
        // Wrappers are cached per-project; reference search is always syntax-only.
        List<AspxSymbolReference> findControlRefs = [];
        if (controlId is not null)
        {
            var wrappers = await ProjectIndexCacheService.GetFindControlWrappersAsync(project, cancellationToken);
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

        return await FindUsagesTool.FormatResultsAsync(
            symbol, references, systemPath, searchSummary, projectPath,
            razorSourceMap, aspxIndex,
            crossProjectRefs: [],
            maxResults, fmt, cancellationToken,
            findControlRefs: findControlRefs,
            controlId: controlId);
    }

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
}
