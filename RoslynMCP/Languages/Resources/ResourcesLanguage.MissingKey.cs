using RoslynMCP.Lsp;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;
using LspDiagnostic = RoslynMCP.Lsp.Protocol.Diagnostic;

namespace RoslynMCP.Languages.Resources;

/// <summary>
/// The one thing a resource key in C# can be wrong about: naming a key no file of its family
/// declares.
/// </summary>
/// <remarks>
/// Two gates, and both are deliberate. The rule refuses to run below
/// <see cref="RootConfidence.Inferred"/>, because proximity candidates are guesses and a diagnostic
/// over a guessed file set reports keys that resolve perfectly well at runtime. And it ships off,
/// so a solution turns it on once it has watched the navigation land where it should — a false
/// "this key does not exist" is what gets the whole feature switched off rather than the one rule.
/// </remarks>
internal sealed partial class ResourcesLanguage : IEmbeddedDiagnosticProvider
{
    /// <summary>RSX0003 — "'{0}' is not declared in {1}".</summary>
    private const string MissingKey = "RSX0003";

    public async Task<IReadOnlyList<LspDiagnostic>> DiagnosticsAsync(
        EmbeddedStringContext context, CancellationToken ct)
    {
        if (!Settings.MissingKeyDiagnostic)
            return [];

        if (await KeyAtAsync(context, ct) is not
            { Confidence: RootConfidence.Exact or RootConfidence.Inferred } match)
        {
            return [];
        }

        var families = Loaded(match);

        // No candidate family at all is "the resource file could not be found", which is a
        // different claim from "the file does not have this key" and not one worth a squiggle.
        if (families.IsEmpty || Declaring(families, match.Key).Any())
            return [];

        var text = await context.Document.GetTextAsync(ct);

        return
        [
            new LspDiagnostic(
                LspConverters.ToRange(text.Lines, match.Span),
                LspConverters.ToLspSeverity(DiagnosticSeverity.Warning),
                MissingKey,
                DiagnosticSource,
                $"'{match.Key}' is not declared in {Sources(families)}."),
        ];
    }
}
