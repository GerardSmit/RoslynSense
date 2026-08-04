using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Proto.Core;
using RoslynMCP.Services;
using RoslynMCP.Tools;

namespace RoslynMCP.Languages.Proto.Tools;

/// <summary>
/// Answers find-usages for a marked snippet in a <c>.proto</c>: the snippet becomes a caret, the
/// caret becomes a proto declaration, and <see cref="ProtoReferenceService"/> turns that into every
/// place the C# built from it is used.
/// </summary>
/// <remarks>
/// <para>
/// The hard part is not done here and must not be. One proto declaration is several C# symbols —
/// a service is a static holder, an abstract base and a client; an rpc is a virtual method, its
/// overrides and a handful of client overloads — and choosing that set is what
/// <see cref="ProtoReferenceService.FindUsagesAsync"/> exists for. This file only groups what comes
/// back and prints it.
/// </para>
/// <para>
/// Definitions are reported apart from references because for this language they are the answer,
/// not noise: the definitions of an rpc's symbol set include the <c>override</c> in the
/// hand-written server class, which is the thing someone opening a <c>.proto</c> and asking "who
/// implements this" is looking for.
/// </para>
/// </remarks>
internal class ProtoFindUsages(IOutputFormatter fmt) : IFindUsagesHandler
{
    private const int MaxSnippetLength = 100;

    public bool CanHandle(string filePath) => ProtoDocumentService.IsProtoFile(filePath);

    public async Task<string> FindUsagesAsync(
        string systemPath, string markupSnippet, int maxResults,
        CancellationToken cancellationToken, int? hintLine = null)
    {
        if (!MarkupString.TryParse(markupSnippet, out var markup, out string? parseError))
            return $"Error: Invalid markup snippet. {parseError}";

        if (!File.Exists(systemPath))
            return $"Error: File {systemPath} does not exist.";

        // Asked before the workspace is touched so a .proto sitting outside every project fails
        // with the reason rather than with an empty result set.
        string? projectPath = await NonCSharpProjectFinder.FindProjectAsync(systemPath, cancellationToken);
        if (string.IsNullOrEmpty(projectPath))
            return "Error: Couldn't find a project containing this file.";

        var view = await ProtoWorkspace.GetAsync(systemPath, cancellationToken);
        if (view is null)
            return $"Error: Couldn't load '{Path.GetFileName(systemPath)}'.";

        if (view.Project is not { } project)
            return "Error: Unable to get a compilation for the project that compiles this .proto.";

        var hit = ProtoMarkup.FindMarkedSpan(view.Text, markup!, hintLine) is { } marked
            ? ProtoSymbolResolver.ResolveAt(view, marked.Start)
            : null;

        if (hit is null)
            return $"No proto declaration or reference found for '{markup!.MarkedText}'.";

        var usages = await ProtoReferenceService.FindUsagesAsync(
            hit, view.Index, project, cancellationToken, ProtoReferenceService.ExplicitSearchBudget);

        return await FormatAsync(
            view, hit, markup!, usages, systemPath, project, maxResults, cancellationToken);
    }

    // ---- The report -------------------------------------------------------------------------

    private async Task<string> FormatAsync(
        ProtoProjectView view, ProtoHit hit, MarkupString markup, IReadOnlyList<ProtoUsage> usages,
        string systemPath, Project project, int maxResults, CancellationToken cancellationToken)
    {
        var target = hit.Target;
        var results = new StringBuilder();

        fmt.AppendHeader(results, "Proto Symbol Usage Analysis");

        fmt.AppendHeader(results, "Search Information", level: 2);
        fmt.AppendField(results, "File", systemPath);
        fmt.AppendField(results, "Position", $"Markup target: `{markup.MarkedText}`");
        fmt.AppendField(results, "Proto", target is not null
            ? $"{target.FullName} ({target.Kind})"
            : $"{hit.Name ?? markup.MarkedText} ({hit.Kind}) — names nothing this file can see");
        fmt.AppendField(results, "Generated C#", hit.Symbol?.ToDisplayString() ?? NotBound(view));
        fmt.AppendField(results, "Project", project.FilePath is { } path ? Path.GetFileName(path) : project.Name);
        fmt.AppendSeparator(results);

        var definitions = await RowsAsync(usages.Where(usage => usage.IsDefinition), cancellationToken);
        var references = await RowsAsync(usages.Where(usage => !usage.IsDefinition), cancellationToken);

        int total = definitions.Count + references.Count;
        int fileCount = definitions.Concat(references)
            .Select(row => row.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        fmt.AppendHeader(results, "Usages", level: 2);
        fmt.AppendField(results, "Found", $"{total} location(s) across {fileCount} file(s)");
        fmt.AppendSeparator(results);

        if (total == 0)
        {
            fmt.AppendEmpty(results, EmptyReason(view, hit));
            AppendHints(results, view, target);
            return results.ToString();
        }

        int budget = maxResults;

        if (definitions.Count > 0)
        {
            fmt.AppendHeader(results, "Definitions and implementations", level: 3);
            fmt.AppendField(results, "Found", $"{definitions.Count} declaration(s)");
            fmt.AppendSeparator(results);
            budget = AppendGroups(results, definitions, budget);
        }

        if (references.Count > 0)
        {
            fmt.AppendHeader(results, "References", level: 3);
            fmt.AppendField(results, "Found", $"{references.Count} call site(s)");
            fmt.AppendSeparator(results);
            budget = AppendGroups(results, references, budget);
        }

        fmt.AppendTruncation(results, maxResults - budget, total);
        AppendHints(results, view, target);

        return results.ToString();
    }

    /// <summary>
    /// One table per file, headed by the file's path.
    /// </summary>
    /// <remarks>
    /// Both the header and the table name carry the path, because only one of them survives each
    /// output format: the markdown formatter drops a table's name and the TOON formatter drops
    /// headers entirely, so a group labelled once would be anonymous in one of the two.
    /// </remarks>
    private int AppendGroups(StringBuilder results, List<UsageRow> rows, int budget)
    {
        foreach (var group in rows.GroupBy(row => row.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            if (budget <= 0)
                break;

            var shown = group.Take(budget).ToList();
            budget -= shown.Count;

            fmt.AppendHeader(results, group.Key, level: 4);
            fmt.AppendTable(
                results,
                group.Key,
                ["Line", "Column", "Code"],
                [.. shown.Select(row => new[] { row.Line.ToString(), row.Column.ToString(), row.Code })],
                group.Count());
            fmt.AppendSeparator(results);
        }

        return budget;
    }

    private void AppendHints(StringBuilder results, ProtoProjectView view, ProtoDeclaration? target)
    {
        var hints = new List<string>();

        if (view.Index.IsEmpty)
        {
            hints.Add(
                "The project has produced no generated code, so nothing here is bound to a symbol. " +
                "Build it and run this again.");
        }

        if (target is ProtoService or ProtoRpc)
        {
            hints.Add(
                "The definitions above include the hand-written classes deriving from the generated " +
                "service base — that is where the server logic lives.");
        }

        if (!view.Index.IsEmpty)
        {
            hints.Add(
                "protoc's own output is not listed. It is regenerated on every build and mentions " +
                "each declaration a dozen times over, so a declaration that lives there is reported " +
                "as the .proto line it was generated from.");
        }

        hints.Add("Use get_call_hierarchy on a call site to trace the full caller chain");

        fmt.AppendHints(results, [.. hints]);
    }

    // ---- Rows -------------------------------------------------------------------------------

    /// <summary>One usage as the report shows it.</summary>
    private readonly record struct UsageRow(string FilePath, int Line, int Column, string Code);

    /// <summary>
    /// Turns usages into displayable rows, ordered by file and then by line.
    /// </summary>
    /// <remarks>
    /// The text of each document is fetched once and reused, because a symbol set for one proto
    /// declaration routinely lands many times in the same file — a service's holder class, its base
    /// and its client are three answers in one <c>WidgetsGrpc.cs</c>. A row standing in for a
    /// <c>.proto</c> declaration brings its own text along, from the parse that resolved it.
    /// </remarks>
    private static async Task<List<UsageRow>> RowsAsync(
        IEnumerable<ProtoUsage> usages, CancellationToken cancellationToken)
    {
        var texts = new Dictionary<DocumentId, SourceText>();
        var rows = new List<UsageRow>();

        foreach (var usage in usages)
        {
            var text = usage.Text;

            if (text is null && usage.Document is { } document)
            {
                if (!texts.TryGetValue(document.Id, out text))
                {
                    text = await document.GetTextAsync(cancellationToken);
                    texts[document.Id] = text;
                }
            }

            if (text is null)
                continue;

            var position = text.Lines.GetLinePosition(Math.Clamp(usage.Span.Start, 0, text.Length));
            string code = text.Lines[position.Line].ToString().Trim();

            rows.Add(new UsageRow(
                usage.FilePath,
                position.Line + 1,
                position.Character + 1,
                code.Length > MaxSnippetLength ? code[..(MaxSnippetLength - 3)] + "..." : code));
        }

        return
        [
            .. rows
                .OrderBy(row => row.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Line)
                .ThenBy(row => row.Column)
        ];
    }

    // ---- Diagnosis for an empty answer --------------------------------------------------------

    private static string NotBound(ProtoProjectView view) =>
        view.Index.IsEmpty
            ? "not bound — the project has never been built"
            : "not bound — the generated code does not mention this declaration";

    /// <summary>
    /// Why nothing came back. An empty result means one of three different things here, and they
    /// need three different next steps.
    /// </summary>
    private static string EmptyReason(ProtoProjectView view, ProtoHit hit) => (view.Index.IsEmpty, hit.Symbol) switch
    {
        (true, _) =>
            "No usages. The project has produced no generated code, so there is no C# to search — " +
            "build it and run this again.",

        (false, null) when hit.WellKnown is not null =>
            "No usages. The name is one of protoc's own types, whose C# lives in the Google.Protobuf " +
            "runtime rather than in generated code this solution owns.",

        (false, null) =>
            "No usages. The declaration is not bound to any generated symbol, which usually means it " +
            "was added or renamed since the last build.",

        _ =>
            "No usages. The generated code exists and nothing in the solution references it, which " +
            "for a contract normally means the other side of the wire is in another solution.",
    };
}
