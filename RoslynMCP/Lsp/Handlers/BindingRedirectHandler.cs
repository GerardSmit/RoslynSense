using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Services.Packages;
using RoslynMCP.Services.ProjectModel;
using Range = RoslynMCP.Lsp.Protocol.Range;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>
/// Binding redirects as editor feedback: a warning on the <c>dependentAssembly</c> that is wrong,
/// a quick fix that rewrites it, one lens above the file that fixes the lot, and the version that
/// actually ships on hover.
/// </summary>
/// <remarks>
/// <para>
/// The config file is not a Roslyn document — no project contains it — so this sits in front of the
/// C# handlers rather than behind them. It is deliberately not a language pack: a
/// <c>web.config</c> is not a language, and the question being answered is about a project's output
/// rather than about the buffer.
/// </para>
/// <para>
/// In front of, not instead of. The same file is the webconfig pack's — its reference counts and
/// its hover over an <c>&lt;add key&gt;</c> are about the same document — so
/// <see cref="LspServer"/> composes the two: this contributes its lens above the pack's, and its
/// hover answers only over an <c>assemblyIdentity</c>'s name, leaving every other position in the
/// file to the pack.
/// </para>
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

    /// <summary>
    /// One lens, at the top of the document, for the whole file.
    /// </summary>
    /// <remarks>
    /// Not one per <c>dependentAssembly</c>: the redirects that are wrong already carry a squiggle
    /// and a quick fix each, and a lens above every element would be a second copy of that in the
    /// one place a config file has no room for it. What the diagnostics cannot say is how many
    /// there are in total, which is the question being asked when thirty of them went stale at
    /// once.
    /// </remarks>
    public static async Task<Protocol.CodeLens[]> CodeLensAsync(CodeLensParams p, CancellationToken ct)
    {
        string configPath = LspConverters.UriToPath(p.TextDocument.Uri);
        if (ProjectFor(configPath) is not { } projectPath)
            return [];

        // Cached, unlike the diagnostics path: this one is re-asked on every scroll.
        var report = await BindingRedirectService.CachedAnalyzeAsync(projectPath, ct);

        return Lenses(configPath, report.Findings);
    }

    /// <summary>
    /// The lens the file gets, counting only what clicking it would actually change.
    /// </summary>
    /// <remarks>
    /// An orphan and an unsigned redirect are reported as hints and repaired by nothing, so
    /// counting them here would put a number on the lens that the fix then fails to reach.
    /// </remarks>
    internal static Protocol.CodeLens[] Lenses(
        string configPath, IReadOnlyList<BindingRedirectFinding> findings)
    {
        int count = BindingRedirectService.Fixable(findings).Count();
        if (count == 0)
            return [];

        return
        [
            new Protocol.CodeLens(
                new Range(new Position(0, 0), new Position(0, 0)),
                new Command(
                    count == 1
                        ? "1 binding redirect out of date — fix it"
                        : $"{count} binding redirects out of date — fix them all",
                    ExecuteCommandHandler.FixBindingRedirectsCommand,
                    [configPath])),
        ];
    }

    /// <summary>
    /// Applies every fixable redirect in the file the lens sits on.
    /// </summary>
    /// <remarks>
    /// Through <c>workspace/applyEdit</c> where the file is open, so the change lands in the
    /// buffer and stays undoable, and on disk otherwise. A lens is clicked from an open editor, so
    /// the first path is the normal one; the second is what a client that invoked the command from
    /// its palette gets.
    /// </remarks>
    public static async Task<string> FixAllAsync(string configPath, CancellationToken ct)
    {
        if (ProjectFor(configPath) is not { } projectPath)
            return "No project sits beside this config file.";

        var report = await BindingRedirectService.AnalyzeAsync(projectPath, ct);

        return await ApplyAsync(configPath, report.Findings, ct);
    }

    /// <summary>The rewrite and the sentence describing it.</summary>
    internal static async Task<string> ApplyAsync(
        string configPath, IReadOnlyList<BindingRedirectFinding> findings, CancellationToken ct)
    {
        string original;
        try
        {
            original = OpenDocumentStore.TryGet(configPath, out var buffer)
                ? buffer.ToString()
                : await File.ReadAllTextAsync(configPath, ct);
        }
        catch (IOException ex)
        {
            return $"Could not read '{Path.GetFileName(configPath)}': {ex.Message}";
        }

        var (text, applied) = BindingRedirectService.Rewrite(original, findings);
        if (text is null || applied.Count == 0)
            return "Every binding redirect already names what ships.";

        string label = applied.Count == 1
            ? $"Fix the binding redirect for {applied[0].AssemblyName}"
            : $"Fix {applied.Count} binding redirects";

        if (!await LspSessionRegistry.TryApplyFullTextEditAsync(configPath, text, label, ct))
            await File.WriteAllTextAsync(configPath, text, ct);

        await LspSessionRegistry.RequestRefreshAsync(RefreshKind.Diagnostics | RefreshKind.CodeLens, ct);

        return applied.Count == 1
            ? $"Redirected {applied[0].AssemblyName} to {applied[0].RequiredVersion}."
            : $"Updated {applied.Count} binding redirects.";
    }

    /// <summary>
    /// Over an <c>assemblyIdentity</c>'s name: which version of it the project actually ships.
    /// </summary>
    /// <remarks>
    /// The version a redirect names is the one thing in the file that cannot be checked by reading
    /// it, and the answer otherwise means opening <c>bin</c> and looking at file properties. It
    /// answers whether or not there is a finding — a redirect that is right is worth confirming,
    /// and a hover that appeared only when something was wrong would be read as "no information".
    /// </remarks>
    public static async Task<Hover?> HoverAsync(TextDocumentPositionParams p, CancellationToken ct)
    {
        string configPath = LspConverters.UriToPath(p.TextDocument.Uri);

        string text;
        try
        {
            text = OpenDocumentStore.TryGet(configPath, out var buffer)
                ? buffer.ToString()
                : await File.ReadAllTextAsync(configPath, ct);
        }
        catch (IOException)
        {
            return null;
        }

        // The cheap question first. Most hovers in a config file are nowhere near an
        // assemblyIdentity, and answering those without touching the project index or the solution
        // file is what keeps this off the cost of a mouse resting anywhere in the document.
        if (IdentityNameAt(text, p.Position) is not { } hit)
            return null;

        if (ProjectFor(configPath) is not { } projectPath)
            return null;

        var (name, range) = hit;

        var installed = await BindingRedirectService.InstalledAsync(projectPath, ct);
        installed.TryGetValue(name, out var file);

        // From the same text the name came out of, not from disk: the version the redirect names
        // is the half of this hover the reader may be in the middle of editing.
        var configured = BindingRedirectService.ReadText(text)
            .FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        string markdown = HoverMarkdown(
            name,
            file?.Identity.Version,
            file is null ? null : Relative(projectPath, file.Path),
            configured?.NewVersion);

        return new Hover(new MarkupContent("markdown", markdown), range);
    }

    /// <summary>
    /// What the hover says: the version that ships, where it was read from, and — only when they
    /// disagree — the version the redirect names.
    /// </summary>
    /// <remarks>
    /// The mismatch line repeats what the diagnostic on the same element already says, and does it
    /// anyway: the hover is what gets used while reading a config top to bottom, which is exactly
    /// when the squiggle three lines down has not been looked at yet.
    /// </remarks>
    internal static string HoverMarkdown(
        string name, Version? installed, string? path, Version? redirectedTo)
    {
        var lines = new List<string> { $"**{name}**", "" };

        if (installed is null)
        {
            lines.Add("Nothing this project ships is named that, so the redirect has no effect.");
            return string.Join("\n", lines);
        }

        lines.Add($"Installed: `{installed}`");

        if (path is { Length: > 0 })
        {
            lines.Add("");
            lines.Add($"`{path}`");
        }

        if (redirectedTo is not null && redirectedTo != installed)
        {
            lines.Add("");
            lines.Add($"The redirect names `{redirectedTo}`, which is not what ships.");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// The <c>name</c> attribute value under the cursor, when it belongs to an
    /// <c>assemblyIdentity</c>.
    /// </summary>
    /// <remarks>
    /// By text rather than by parsing: <see cref="System.Xml.Linq.XDocument"/> records the line an
    /// element starts on and nothing about where its attributes sit, and a hover has to answer for
    /// a document that is mid-edit and may not parse at all. What is checked is the element the
    /// attribute is written inside: the tag opened nearest before it on the line, or — when the
    /// line opens none, which is the hand-wrapped form — the tag still open at the end of the line
    /// above. A config file is full of <c>name=</c> that belong to something else.
    /// </remarks>
    internal static (string Name, Range Range)? IdentityNameAt(string text, Position position)
    {
        var lines = text.Split('\n');
        if (position.Line < 0 || position.Line >= lines.Length)
            return null;

        string line = lines[position.Line].TrimEnd('\r');

        var match = System.Text.RegularExpressions.Regex.Match(
            line, """(?<=\bname\s*=\s*")[^"]+""");

        if (!match.Success ||
            position.Character < match.Index ||
            position.Character > match.Index + match.Length)
        {
            return null;
        }

        if (!InsideIdentity(lines, position.Line, line, match.Index))
            return null;

        return (match.Value, new Range(
            new Position(position.Line, match.Index),
            new Position(position.Line, match.Index + match.Length)));
    }

    /// <summary>Whether the attribute at <paramref name="attribute"/> is written inside an
    /// <c>assemblyIdentity</c> tag.</summary>
    private static bool InsideIdentity(string[] lines, int lineIndex, string line, int attribute)
    {
        int opened = line.LastIndexOf('<', Math.Max(0, attribute - 1));

        // The tag opens on this line, so it is the one this attribute belongs to whatever the
        // line above says.
        if (opened >= 0)
            return Names(line, opened);

        // No tag opens before the attribute, so it continues the one above — but only while that
        // one is still open. A closed element followed by a bare attribute is not markup this has
        // anything to say about, and treating it as one would answer for the wrong element.
        if (lineIndex == 0)
            return false;

        string previous = lines[lineIndex - 1].TrimEnd('\r');
        int previousOpened = previous.LastIndexOf('<');

        return previousOpened >= 0 &&
            previous.IndexOf('>', previousOpened) < 0 &&
            Names(previous, previousOpened);

        // The tag's own name, past a closing slash and past a namespace prefix — a config written
        // by hand sometimes binds the assembly namespace to a prefix rather than as the default.
        static bool Names(string text, int open)
        {
            var tag = text.AsSpan(open + 1).TrimStart('/');

            int end = tag.IndexOfAny(" \t/>".AsSpan());
            if (end >= 0)
                tag = tag[..end];

            int colon = tag.IndexOf(':');
            if (colon >= 0)
                tag = tag[(colon + 1)..];

            return tag.SequenceEqual("assemblyIdentity".AsSpan());
        }
    }

    /// <summary>The assembly's path as the reader thinks of it — from the project, not from the
    /// root of the disk.</summary>
    private static string Relative(string projectPath, string assemblyPath)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(projectPath));

        return directory is null
            ? assemblyPath
            : Path.GetRelativePath(directory, assemblyPath);
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
