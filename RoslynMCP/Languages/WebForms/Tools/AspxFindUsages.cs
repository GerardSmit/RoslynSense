using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMCP.Services;
using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Tools;

namespace RoslynMCP.Languages.WebForms.Tools;

/// <summary>
/// Resolves FindUsages requests originating from ASPX/ASCX/master-page files: the marked snippet
/// is mapped to a caret in the markup, and the symbol under it is searched for in C# and in
/// markup both. A control that lives inside a template has no symbol, and is answered with its
/// <c>FindControl</c> call sites instead.
/// </summary>
internal class AspxFindUsages(IOutputFormatter fmt) : IFindUsagesHandler
{
    public bool CanHandle(string filePath) => AspxDocumentService.IsAspxFile(filePath);

    public async Task<string> FindUsagesAsync(
        string systemPath, string markupSnippet, int maxResults,
        CancellationToken cancellationToken, int? hintLine = null)
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
        AspxDocument? document;
        try
        {
            document = await AspxDocumentService.GetAsync(systemPath, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await TextSearchFallbackAsync(systemPath, projectPath, markup!, ex, fmt, cancellationToken);
        }

        if (document is null)
            return "Error: Unable to get compilation for the project.";

        var project = document.Project;
        var hit = AspxSourceMappingService.FindMarkedSpan(document.Text, markup!, hintLine) is { } marked
            ? AspxSymbolResolver.ResolveAt(document, marked.Start)
            : null;
        var symbol = hit?.Symbol;

        // The ID names a code-behind field for a top-level control and nothing for a
        // template-nested one, so it is taken from the hit rather than from the symbol.
        string? controlId = hit is { Kind: AspxHitKind.ControlId, Name: { } id } ? id : null;

        if (controlId is null)
        {
            var controlNode = AspxSourceMappingService.FindControlNodeAtCursor(
                document.Parse, document.Text, markup!, hintLine);
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

        // Template-nested control: no code-behind field, only FindControl search
        if (symbol is null)
        {
            return FormatControlIdOnlyResults(
                controlId!, findControlRefs, systemPath, project.FilePath!, fmt);
        }

        // Resolved symbol: run full Roslyn FindReferences
        var references = await SymbolFinder.FindReferencesAsync(
            symbol, project.Solution, cancellationToken);

        var markupReferences = await AspxReferenceService.FindAsync(symbol, project, cancellationToken);
        var razorSourceMap = await ProjectIndexCacheService.GetRazorSourceMapAsync(project, cancellationToken);
        string searchSummary = controlId is not null
            ? $"Markup target: `{markup!.MarkedText}` (ASPX control ID)"
            : $"Markup target: `{markup!.MarkedText}`";

        return await FindUsagesTool.FormatResultsAsync(
            symbol, references, systemPath, searchSummary, project.FilePath!,
            razorSourceMap, markupReferences,
            crossProjectRefs: [],
            maxResults, fmt, cancellationToken,
            findControlRefs: findControlRefs,
            controlId: controlId);
    }

    /// <summary>
    /// The report for a control that lives inside a template. It has no code-behind field, so
    /// there is no symbol to search for and <c>FindControl</c> call sites are the whole answer.
    /// </summary>
    private static string FormatControlIdOnlyResults(
        string controlId,
        List<AspxSymbolReference> findControlRefs,
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

        fmt.AppendHeader(results, "Summary", level: 2);
        fmt.AppendField(results, "Control ID", $"`{controlId}`");
        fmt.AppendField(results, "FindControl calls", findControlRefs.Count);
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
