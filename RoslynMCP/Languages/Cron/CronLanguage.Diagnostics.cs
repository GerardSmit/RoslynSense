using RoslynMCP.Lsp;
using LspDiagnostic = RoslynMCP.Lsp.Protocol.Diagnostic;

namespace RoslynMCP.Languages.Cron;

/// <summary>
/// The ways a schedule can be one the library will not accept.
/// </summary>
/// <remarks>
/// <para>
/// A rejected crontab expression is not a compile error and usually not a startup error either: the
/// scheduler throws when the registration runs, which on a background service means a job that
/// silently never exists. Nothing else in the toolchain looks at the string at all, so a squiggle
/// here is the only warning there will be before the first missed run.
/// </para>
/// <para>
/// A warning by default rather than an error, and the reason is in <see cref="CronSettings.Severity"/>:
/// the string is read by a library this pack did not write, at a version it cannot see.
/// </para>
/// </remarks>
internal sealed partial class CronLanguage : IEmbeddedDiagnosticProvider
{
    /// <summary>CRON0001 — the expression is not one the library reading it accepts.</summary>
    private const string ExpressionSyntax = "CRON0001";

    internal const string DiagnosticSource = "roslyn-sense";

    public async Task<IReadOnlyList<LspDiagnostic>> DiagnosticsAsync(
        EmbeddedStringContext context, CancellationToken ct)
    {
        if (!Settings.Enabled
            || !Settings.ExpressionDiagnostic
            || Resolve(context, ct) is not { } at
            || at.Parse.Problems.IsEmpty)
        {
            return [];
        }

        var text = await context.Document.GetTextAsync(ct);
        var found = new List<LspDiagnostic>(at.Parse.Problems.Length);

        foreach (var problem in at.Parse.Problems)
        {
            found.Add(new LspDiagnostic(
                LspConverters.ToRange(text.Lines, at.InDocument(problem.Span)),
                LspConverters.ToLspSeverity(Settings.Severity),
                ExpressionSyntax,
                DiagnosticSource,
                problem.Message));
        }

        return found;
    }
}
