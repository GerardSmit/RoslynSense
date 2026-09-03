using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Cron.Core;

namespace RoslynMCP.Languages.Cron;

/// <summary>
/// A crontab expression inside a C# literal, claimed from the call around it when nothing
/// annotated it.
/// </summary>
/// <remarks>
/// Roslyn's own detector cannot find these: its unannotated-API list is hardcoded to regular
/// expressions and JSON, and no BCL type is involved here anyway. So the call is the signal — the
/// shipped Hangfire and Quartz bindings, whatever the solution configured, and, failing both, the
/// parameter's name. The <c>[StringSyntax]</c> and <c>// lang=</c> routes below are for the code
/// that wants to say so outright.
/// </remarks>
internal sealed partial class CronLanguage : IConfiguredStringLanguage
{
    /// <summary>The identifier a schedule is claimed under.</summary>
    private const string CronSyntax = "Cron";

    /// <summary>
    /// The <c>StringSyntaxAttribute</c> constants this pack answers to.
    /// </summary>
    /// <remarks>
    /// None of these is a BCL constant — there is no <c>StringSyntaxAttribute.Cron</c> — so they are
    /// what a solution writes for itself: <c>[StringSyntax("Cron")]</c> on its own parameter, or a
    /// <c>// lang=cron</c> comment above a literal. Both are the escape hatch for a call shape the
    /// bindings do not describe.
    /// </remarks>
    public ImmutableArray<string> StringSyntaxIdentifiers { get; } =
        [CronSyntax, "CronExpression", "Crontab"];

    public Task<string?> DetectAsync(
        Document document, SyntaxToken token, SemanticModel semanticModel, CancellationToken ct)
    {
        if (!Settings.Enabled || !CronCallSite.CouldBeCron(token))
            return Task.FromResult<string?>(null);

        var call = CronCallSite.Resolve(
            Settings.Bindings, Settings.ParameterNames, semanticModel, token, ct);

        return Task.FromResult(call is null ? null : CronSyntax);
    }

    /// <summary>
    /// The literal's text, how it is read, and how to map an offset inside it back to the document —
    /// everything every feature in the pack starts from.
    /// </summary>
    /// <param name="Offset">Where the expression begins in the document.</param>
    /// <param name="Exact">
    /// Whether an offset inside the text is an offset in the document. False once the literal
    /// escapes anything, since one <c>\n</c> in the source is one character in the value and two in
    /// the file. Almost always true here — a schedule has nothing in it worth escaping — but the
    /// three lines that check are cheaper than colouring the wrong characters once.
    /// </param>
    private readonly record struct CronAt(
        CronParse Parse, string Text, SyntaxToken Token, int Offset, bool Exact, CronCall Call)
    {
        /// <summary>A span inside the expression, as a span in the document.</summary>
        /// <remarks>
        /// Collapses to the whole literal when the mapping is not exact. A colour over the string is
        /// a worse answer than one over the field, and a colour over the wrong two characters is
        /// worse than both.
        /// </remarks>
        public TextSpan InDocument(TextSpan inside) =>
            Exact ? new TextSpan(Offset + inside.Start, inside.Length) : Token.Span;
    }

    private CronAt? Resolve(EmbeddedStringContext context, CancellationToken ct)
    {
        var token = context.Token;
        if (!Settings.Enabled)
            return null;

        var call = CronCallSite.Resolve(
            Settings.Bindings, Settings.ParameterNames, context.SemanticModel, token, ct);

        if (call is null)
        {
            // Reached when an annotation claimed the literal rather than a call — a
            // `[StringSyntax("Cron")]` parameter of the solution's own, where nothing named a
            // library. The compilation is then the only thing that can say which reading applies.
            call = new CronCall(
                Binding: null,
                CronTypes.For(context.SemanticModel.Compilation).Dialect ?? CronDialect.Standard,
                Subject: string.Empty,
                token.Span);
        }

        var dialect = Options(context, call.Value.Dialect);

        string raw = token.Text;
        bool verbatim = raw.StartsWith("@\"", StringComparison.Ordinal);
        int prefix = verbatim ? 2 : 1;

        bool exact = !raw.Contains('\\')
            && !raw.AsSpan(prefix).Contains("\"\"", StringComparison.Ordinal);

        string text = token.ValueText;

        return new CronAt(
            Cron.Parse(text, dialect),
            text,
            token,
            token.SpanStart + prefix,
            exact,
            call.Value with { Dialect = dialect });
    }

    /// <summary>
    /// The dialect a <c>// lang=cron,quartz</c> comment named, if one did.
    /// </summary>
    /// <remarks>
    /// The option words are what that seam is for, and they are the only way to say which library
    /// reads a literal in a solution that references both — where neither the call nor the
    /// compilation can settle it, and where getting it wrong is most likely.
    /// </remarks>
    private static CronDialect Options(EmbeddedStringContext context, CronDialect fallback)
    {
        foreach (string option in context.Options)
        {
            switch (option.Trim().ToLowerInvariant())
            {
                case "quartz":
                    return CronDialect.Quartz;
                case "hangfire":
                    return CronDialect.Hangfire;
                case "standard":
                case "crontab":
                    return CronDialect.Standard;
            }
        }

        return fallback;
    }
}
