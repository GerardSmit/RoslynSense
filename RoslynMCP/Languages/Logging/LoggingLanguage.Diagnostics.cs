using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Logging.Core;
using RoslynMCP.Lsp;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;
using LspDiagnostic = RoslynMCP.Lsp.Protocol.Diagnostic;

namespace RoslynMCP.Languages.Logging;

/// <summary>
/// The five ways a message template and the values beside it can disagree, none of which the
/// compiler has an opinion about.
/// </summary>
/// <remarks>
/// Two of these — <see cref="UnknownPlaceholder"/> and <see cref="UnusedValue"/> over a
/// <c>[LoggerMessage]</c> — restate what the source generator reports as SYSLIB1014 and SYSLIB1015,
/// at a better location: on the hole and on the parameter rather than on the method. Where the
/// generator runs that is two squiggles saying one thing, so both rules are switchable; see
/// <see cref="LoggingSettings"/>. Where it does not — a project that predates it, targets an older
/// framework, or has the generator disabled — these are the only report there is.
/// </remarks>
internal sealed partial class LoggingLanguage : IEmbeddedDiagnosticProvider
{
    /// <summary>LOG0001 — the template text itself is malformed.</summary>
    private const string TemplateSyntax = "LOG0001";

    /// <summary>LOG0002 — a hole names nothing the method declares.</summary>
    private const string UnknownPlaceholder = "LOG0002";

    /// <summary>LOG0003 — the template and the call disagree about how many values there are.</summary>
    private const string ValueCount = "LOG0003";

    /// <summary>LOG0004 — a value no hole renders.</summary>
    private const string UnusedValue = "LOG0004";

    /// <summary>LOG0005 — an exception passed as a rendered value instead of as the exception.</summary>
    private const string ExceptionPosition = "LOG0005";

    public async Task<IReadOnlyList<LspDiagnostic>> DiagnosticsAsync(
        EmbeddedStringContext context, CancellationToken ct)
    {
        if (!Settings.Enabled || Resolve(context, ct) is not { } at)
            return [];

        var text = await context.Document.GetTextAsync(ct);
        var found = new List<LspDiagnostic>();

        LspDiagnostic Report(string id, TextSpan span, string message) =>
            new(LspConverters.ToRange(text.Lines, span),
                LspConverters.ToLspSeverity(DiagnosticSeverity.Warning),
                id, DiagnosticSource, message);

        if (Settings.TemplateSyntax)
        {
            foreach (var problem in at.Template.Problems)
                found.Add(Report(TemplateSyntax, at.InDocument(problem.Span), problem.Message));
        }

        if (at.Site.Binding == TemplateBinding.ByName)
            ByName(at, found, Report);
        else if (at.Site.ValuesAreComplete)
            Positional(at, found, Report);

        if (Settings.ExceptionPosition)
            Exceptions(at, found, Report);

        return found;
    }

    /// <summary>
    /// A generated method: holes and parameters are matched by name, so each side can name
    /// something the other does not have.
    /// </summary>
    private void ByName(
        TemplateAt at, List<LspDiagnostic> found, Func<string, TextSpan, string, LspDiagnostic> report)
    {
        if (Settings.UnknownPlaceholder)
        {
            foreach (var bound in HoleBinding.Bind(at.Template, at.Site))
            {
                if (bound.Value is null)
                {
                    found.Add(report(
                        UnknownPlaceholder, at.InDocument(bound.Hole.NameSpan),
                        $"'{bound.Hole.Name}' is not a parameter of '{at.Site.Subject}', so it prints as literal text."));
                }
            }
        }

        if (!Settings.UnusedValue)
            return;

        foreach (var value in HoleBinding.Unrendered(at.Template, at.Site))
        {
            found.Add(report(
                UnusedValue, value.Span,
                $"'{value.Name}' is not in the message, so it is captured into the log state but never printed."));
        }
    }

    /// <summary>
    /// A call: holes and values are matched by order, so the only thing that can be wrong is how
    /// many there are — and being wrong about that shifts every hole after the mistake onto the
    /// wrong value.
    /// </summary>
    private void Positional(
        TemplateAt at, List<LspDiagnostic> found, Func<string, TextSpan, string, LspDiagnostic> report)
    {
        int wanted = at.Template.ValueCount;
        int given = at.Site.Values.Length;

        if (wanted != given)
        {
            if (Settings.ValueCount)
            {
                found.Add(report(
                    ValueCount, at.Token.Span,
                    $"The template has {Count(wanted, "placeholder")} and the call passes "
                    + $"{Count(given, "value")}."));
            }

            // The unused-value rule would say the same thing again about the tail of the list.
            return;
        }

        if (!Settings.UnusedValue)
            return;

        foreach (var value in HoleBinding.Unrendered(at.Template, at.Site))
        {
            found.Add(report(
                UnusedValue, value.Span,
                $"No placeholder renders '{value.Name}'."));
        }
    }

    /// <summary>
    /// An exception handed to the template instead of to the logger.
    /// </summary>
    /// <remarks>
    /// All three libraries take the exception as the first argument, in front of the template, and
    /// what that buys is the stack trace: passed as a value it renders as
    /// <c>ex.ToString()</c> into one property and the sink never sees an exception at all, so the
    /// structured fields, the error grouping and the sink's own exception rendering are all lost.
    /// The call still compiles and still logs something, which is why this survives review.
    /// </remarks>
    private static void Exceptions(
        TemplateAt at, List<LspDiagnostic> found, Func<string, TextSpan, string, LspDiagnostic> report)
    {
        foreach (var value in at.Site.Values)
        {
            if (value.IsException)
            {
                found.Add(report(
                    ExceptionPosition, value.Span,
                    $"Pass '{value.Name}' as the first argument, before the template, so the logger "
                    + "records it as the exception rather than rendering it into one property."));
            }
        }
    }

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
}
