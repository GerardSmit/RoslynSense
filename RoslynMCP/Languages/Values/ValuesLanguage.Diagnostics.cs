using RoslynMCP.Config;
using RoslynMCP.Languages.Values.Core;
using RoslynMCP.Lsp;
using LspDiagnostic = RoslynMCP.Lsp.Protocol.Diagnostic;

namespace RoslynMCP.Languages.Values;

/// <summary>
/// The one thing this pack reports: a string that is not one of the values it has to be.
/// </summary>
/// <remarks>
/// <para>
/// One rule, because there is only one mistake. A code that the table does not have is a branch
/// that can never be taken, and it reaches production looking exactly like working code: it
/// compiles, it runs, the tests pass, and the feature is simply absent. That is the same class of
/// error as a misspelled member name, which is why it is reported as an error by default — see
/// <see cref="ValueSetsConfig.Severity"/> for softening it while a codebase catches up.
/// </para>
/// <para>
/// What makes it safe is <see cref="ValueSetContents.Decides"/>. The claim being made is about
/// <i>every</i> value there is, so it needs all of them: an unreachable database, a failed query
/// and a result the row cap cut short each report nothing at all. A tool that goes red when the
/// network hiccups is a tool people turn off, and then it catches nothing ever again.
/// </para>
/// </remarks>
internal sealed partial class ValuesLanguage : IEmbeddedDiagnosticProvider
{
    /// <summary>VAL0001 — the literal is not one of the set's values.</summary>
    private const string UnknownValue = "VAL0001";

    public async Task<IReadOnlyList<LspDiagnostic>> DiagnosticsAsync(
        EmbeddedStringContext context, CancellationToken ct)
    {
        if (!Settings.UnknownValueDiagnostic || Site(context, ct) is not { } site)
            return [];

        var contents = await _catalog.ContentsAsync(site.Set, ct);

        if (!contents.Decides || contents.Contains(site.Written))
            return [];

        var text = await context.Document.GetTextAsync(ct);

        return
        [
            new LspDiagnostic(
                LspConverters.ToRange(text.Lines, Shown(context, site)),
                LspConverters.ToLspSeverity(Settings.Severity),
                UnknownValue,
                DiagnosticSource,
                Message(site, contents)),
        ];
    }

    /// <summary>
    /// What is wrong, and the value that was probably meant.
    /// </summary>
    /// <remarks>
    /// Deliberately not where the values come from. A message is read in a list in the Problems
    /// panel, one line among hundreds, and a <c>SELECT</c> with a join and an <c>ORDER BY</c> in it
    /// pushes the three things that matter — what was written, how many values there are, which set
    /// — off the end of that line. Nobody fixes this diagnostic by reading the query; they fix it by
    /// taking the suggestion or opening the completion list.
    /// </remarks>
    private static string Message(ValueSite site, ValueSetContents contents)
    {
        string written = site.Written.Length == 0 ? "The empty string" : $"'{site.Written}'";

        string suggestion =
            ValueSuggestion.Nearest(contents, site.Written, site.Set.Comparer) is { } near
                ? $" Did you mean '{near}'?"
                : string.Empty;

        return $"{written} is not one of the {contents.Values.Length} values of "
            + $"'{site.Set.Id}'.{suggestion}";
    }
}
