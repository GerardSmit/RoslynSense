using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Services.Packages;
using RoslynMCP.Services.ProjectModel;
using Range = RoslynMCP.Lsp.Protocol.Range;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>
/// Binding redirects as editor feedback: a warning on the <c>dependentAssembly</c> that is wrong,
/// and a quick fix that rewrites it.
/// </summary>
/// <remarks>
/// The config file is not a Roslyn document — no project contains it — so this sits in front of the
/// C# handlers rather than behind them. It is deliberately not a language pack: a
/// <c>web.config</c> is not a language, and the question being answered is about a project's output
/// rather than about the buffer.
/// </remarks>
internal static class BindingRedirectHandler
{
    private const string Source = "roslynSense";
    private const string Code = "binding-redirect";

    private static readonly string[] s_names = ["web.config", "app.config"];

    public static bool IsConfigPath(string? filePath) =>
        filePath is { Length: > 0 } &&
        s_names.Contains(Path.GetFileName(filePath), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The project whose redirects this file carries — the one beside it.
    /// </summary>
    /// <remarks>
    /// By directory rather than by containment: a legacy <c>.csproj</c> does not list its
    /// <c>web.config</c> as an item in any way that survives evaluation, and a config file that
    /// sits next to no project is not one this has anything to say about.
    /// </remarks>
    public static string? ProjectFor(string configPath)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(configPath));
        if (directory is null)
            return null;

        return SolutionProjectIndex.ProjectPaths().FirstOrDefault(
            project => string.Equals(
                Path.GetDirectoryName(Path.GetFullPath(project)), directory, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<Diagnostic[]> DiagnosticsAsync(string configPath, CancellationToken ct)
    {
        if (ProjectFor(configPath) is not { } projectPath)
            return [];

        return ToDiagnostics(await BindingRedirectService.AnalyzeAsync(projectPath, ct));
    }

    public static Diagnostic[] ToDiagnostics(BindingRedirectReport report) =>
        report.Findings.Select(ToDiagnostic).ToArray();

    public static async Task<CodeAction[]> CodeActionsAsync(CodeActionParams p, CancellationToken ct)
    {
        string configPath = LspConverters.UriToPath(p.TextDocument.Uri);
        if (ProjectFor(configPath) is not { } projectPath)
            return [];

        var report = await BindingRedirectService.AnalyzeAsync(projectPath, ct);
        if (report.Findings.Count == 0)
            return [];

        string original;
        try
        {
            original = await File.ReadAllTextAsync(configPath, ct);
        }
        catch (IOException)
        {
            return [];
        }

        var actions = new List<CodeAction>();

        // The one under the cursor first, then the sweep. A file with thirty stale redirects is
        // the common case after a framework upgrade, and fixing them one at a time is not a fix.
        foreach (var finding in report.Findings.Where(f => Touches(f, p.Range)))
        {
            if (Edit(configPath, original, [finding]) is { } edit)
                actions.Add(new CodeAction(Title(finding), "quickfix", edit));
        }

        if (report.Findings.Count > 1 && Edit(configPath, original, report.Findings) is { } all)
        {
            actions.Add(new CodeAction(
                $"Fix all {report.Findings.Count} binding redirects in this file", "quickfix", all));
        }

        return actions.ToArray();
    }

    private static bool Touches(BindingRedirectFinding finding, Range range) =>
        // A missing redirect has no element to sit on, so it is offered everywhere in the file.
        finding.Line < 0 || (finding.Line >= range.Start.Line && finding.Line <= range.End.Line);

    private static string Title(BindingRedirectFinding finding) => finding.Problem switch
    {
        BindingRedirectProblem.Missing => $"Add a binding redirect for {finding.AssemblyName}",
        BindingRedirectProblem.Narrow => $"Widen the binding redirect for {finding.AssemblyName}",
        _ => $"Redirect {finding.AssemblyName} to {finding.RequiredVersion}",
    };

    /// <summary>
    /// The rewrite as one whole-document edit.
    /// </summary>
    /// <remarks>
    /// Whole-document rather than a surgical range: the fix can insert a <c>runtime</c> section
    /// that did not exist, and computing minimal edits for that buys nothing the client's own diff
    /// does not already do.
    /// </remarks>
    private static WorkspaceEdit? Edit(
        string configPath, string original, IReadOnlyList<BindingRedirectFinding> findings)
    {
        var (text, applied) = BindingRedirectService.Rewrite(original, findings);
        if (text is null || applied.Count == 0)
            return null;

        var end = EndOf(original);

        return new WorkspaceEdit(new Dictionary<string, TextEdit[]>
        {
            [LspConverters.PathToUri(configPath)] =
                [new TextEdit(new Range(new Position(0, 0), end), text)],
        });
    }

    private static Position EndOf(string text)
    {
        int line = 0;
        int lastBreak = -1;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                lastBreak = i;
            }
        }

        return new Position(line, text.Length - lastBreak - 1);
    }

    private static Diagnostic ToDiagnostic(BindingRedirectFinding finding)
    {
        int line = Math.Max(0, finding.Line);

        return new Diagnostic(
            new Range(new Position(line, 0), new Position(line, 0)),
            Severity(finding.Problem),
            Code,
            Source,
            finding.Message);
    }

    /// <summary>
    /// Stale and missing redirects fail at runtime, so they are warnings. The rest are true but
    /// harmless — an orphan redirect has never broken anything — and a warning for those would
    /// train people to ignore the ones that matter.
    /// </summary>
    private static int Severity(BindingRedirectProblem problem) => problem switch
    {
        BindingRedirectProblem.Stale or BindingRedirectProblem.Missing or BindingRedirectProblem.Narrow => 2,
        _ => 3,
    };
}
