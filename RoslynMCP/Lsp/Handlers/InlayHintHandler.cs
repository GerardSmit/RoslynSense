using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.InlineHints;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>
/// textDocument/inlayHint backed by Roslyn's InlineHints services (parameter-name and
/// implicit-type hints). The feature is internal in Roslyn — accessed via Publicizer
/// (see RoslynMCP.csproj), so signature changes surface at build time. The master switches
/// in InlineHintsOptions.Default are off (the editor is expected to flip them), so an
/// enabled copy is used; VS's suppression heuristics (argument matches parameter name,
/// method intent, …) stay active.
/// </summary>
internal static class InlayHintHandler
{
    private static readonly InlineHintsOptions s_options = InlineHintsOptions.Default with
    {
        ParameterOptions = InlineParameterHintsOptions.Default with { EnabledForParameters = true },
        TypeOptions = InlineTypeHintsOptions.Default with { EnabledForTypes = true },
    };

    public static async Task<InlayHint[]> InlayHintsAsync(InlayHintParams p, CancellationToken ct)
    {
        var document = await LspDocumentResolver.ResolveAsync(
            LspConverters.UriToPath(p.TextDocument.Uri), ct);
        var service = document?.Project.Services.GetService<IInlineHintsService>();
        if (document is null || service is null)
            return Array.Empty<InlayHint>();

        var text = await document.GetTextAsync(ct);
        var span = LspConverters.ToTextSpan(text, p.Range);

        var hints = await service.GetInlineHintsAsync(
            document, span, s_options, displayAllOverride: false, ct);

        var result = new List<InlayHint>(hints.Length);
        foreach (var hint in hints)
        {
            // DisplayParts carry a trailing space for editor rendering; padding flags replace it.
            string label = string.Concat(hint.DisplayParts.Select(t => t.Text)).Trim();
            if (label.Length == 0)
                continue;

            // Parameter-name hints render as "name:"; type hints are bare type names.
            bool isParameter = label.EndsWith(":", StringComparison.Ordinal);
            // Roslyn already computed what accepting the hint would write — the inferred type in
            // place of `var`, or the argument rewritten as `name: value`. Passing it through is
            // what makes double-clicking a hint insert it, which is the behaviour it has in VS and
            // Rider; discarding it left the hints decorative.
            var edits = hint.ReplacementTextChange is { } change
                ? new[] { new TextEdit(LspConverters.ToRange(text.Lines, change.Span), change.NewText ?? "") }
                : null;

            result.Add(new InlayHint(
                LspConverters.ToPosition(text.Lines.GetLinePosition(hint.Span.Start)),
                label,
                Kind: isParameter ? 2 : 1,
                PaddingLeft: false, // Roslyn anchors hints directly before the identifier/argument
                PaddingRight: true,
                TextEdits: edits));
        }
        return result.ToArray();
    }
}
