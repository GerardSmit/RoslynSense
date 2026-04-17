using System.Text;
using Microsoft.CodeAnalysis;
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

        // Workspace loading can fail in environments without .NET Framework CLR (clr.dll),
        // or when VS Build Tools aren't installed for legacy projects. Fall back to a pure
        // text search so callers still get FindControl reference locations.
        Workspace workspace;
        Project project;
        try
        {
            (workspace, project) = await WorkspaceService.GetOrOpenProjectAsync(
                projectPath, targetFilePath: systemPath, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await TextSearchFallbackAsync(systemPath, projectPath, markup!, ex, fmt, cancellationToken);
        }

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

        var aspxIndex = await ProjectIndexCacheService.GetAspxIndexAsync(project, cancellationToken, compilation);

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

    /// <summary>
    /// Pure filesystem text search used when the Roslyn workspace cannot be loaded
    /// (e.g. missing .NET Framework CLR for legacy projects, or no VS Build Tools).
    /// Scans all .cs files for FindControl("id") string literals and reports matches.
    /// </summary>
    private static async Task<string> TextSearchFallbackAsync(
        string filePath, string projectPath, MarkupString markup,
        Exception workspaceError, IOutputFormatter fmt, CancellationToken ct)
    {
        var controlId = markup.MarkedText;
        var projectDir = Path.GetDirectoryName(projectPath) ?? ".";
        var findControlRefs = await TextSearchFindControlAsync(projectDir, controlId, ct);

        var results = new StringBuilder();
        fmt.AppendHeader(results, "Control ID References (Text Search)");

        fmt.AppendHeader(results, "Search Information", level: 2);
        fmt.AppendField(results, "File", filePath);
        fmt.AppendField(results, "Control ID", controlId);
        fmt.AppendField(results, "Project", Path.GetFileName(projectPath));
        fmt.AppendField(results, "Warning",
            $"Roslyn workspace could not be loaded ({workspaceError.GetType().Name}: {workspaceError.Message}). " +
            "Results are from a plain text search — wrapper methods and Roslyn symbol analysis are skipped.");
        fmt.AppendSeparator(results);

        if (findControlRefs.Count > 0)
        {
            fmt.AppendHeader(results, "FindControl References", level: 2);
            fmt.AppendField(results, "Found", $"{findControlRefs.Count} FindControl(\"{controlId}\") call(s)");
            fmt.AppendSeparator(results);

            var rows = findControlRefs
                .Select(r =>
                {
                    var snippet = r.CodeSnippet.Length > 80 ? r.CodeSnippet[..77] + "..." : r.CodeSnippet;
                    return new string[] { r.FilePath, $"{r.Line}", snippet };
                })
                .ToList();
            fmt.AppendTable(results, "FindControl Calls", ["File", "Line", "Snippet"], rows);
        }
        else
        {
            fmt.AppendHeader(results, "FindControl References", level: 2);
            fmt.AppendField(results, "Found", "None");
            fmt.AppendSeparator(results);
        }

        fmt.AppendHeader(results, "Summary", level: 2);
        fmt.AppendField(results, "Control ID", $"`{controlId}`");
        fmt.AppendField(results, "FindControl calls (text)", findControlRefs.Count);
        fmt.AppendSeparator(results);
        fmt.AppendHints(results,
            "Install .NET Framework 4.7.2+ and Visual Studio Build Tools, then restart the MCP server for full Roslyn analysis");

        return results.ToString();
    }

    /// <summary>
    /// Scans <paramref name="projectDir"/> for <c>*.cs</c> files containing
    /// <c>FindControl("controlId")</c> as a plain text match (no Roslyn required).
    /// </summary>
    private static async Task<List<AspxSymbolReference>> TextSearchFindControlAsync(
        string projectDir, string controlId, CancellationToken ct)
    {
        var pattern = $"FindControl(\"{controlId}\")";
        var bag = new System.Collections.Concurrent.ConcurrentBag<AspxSymbolReference>();

        List<string> files;
        try
        {
            files = [.. Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !IsInObjOrBin(f, projectDir))];
        }
        catch { return []; }

        await Parallel.ForEachAsync(
            files,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct },
            async (file, fileCt) =>
            {
                string text;
                try { text = await File.ReadAllTextAsync(file, fileCt); }
                catch { return; }

                if (!text.Contains(pattern, StringComparison.Ordinal)) return;

                int lineNum = 1;
                int start = 0;
                while (start < text.Length)
                {
                    int end = text.IndexOf('\n', start);
                    if (end < 0) end = text.Length;
                    var line = text[start..end];
                    if (line.Contains(pattern, StringComparison.Ordinal))
                    {
                        bag.Add(new AspxSymbolReference(
                            file, lineNum, 1, line.Trim(), AspxCodeLocationType.FindControlCall));
                    }
                    start = end + 1;
                    lineNum++;
                }
            });

        return [.. bag.OrderBy(r => r.FilePath).ThenBy(r => r.Line)];
    }

    private static bool IsInObjOrBin(string filePath, string projectDir)
    {
        var rel = Path.GetRelativePath(projectDir, filePath);
        var seg = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return seg.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
               seg.Equals("bin", StringComparison.OrdinalIgnoreCase);
    }
}
