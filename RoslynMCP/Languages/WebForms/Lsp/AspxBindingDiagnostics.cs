using Microsoft.CodeAnalysis;
using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Lsp;
using Protocol = RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.WebForms.Lsp;

/// <summary>
/// A data-binding path naming a member the item type does not have.
/// </summary>
/// <remarks>
/// The mistake this catches survives the compiler by construction: <c>Eval</c> takes a string and
/// reflects over it, so a misspelled member is not a build error, not a test failure, and not
/// anything at all until the page renders — where it throws at the user rather than at whoever
/// wrote it.
/// <para>
/// Emitted here rather than from the parser, which raises the rest of the markup's diagnostics. The
/// parser binds tags and attributes against the compilation; this needs the item type, which comes
/// from an ancestor's <c>ItemType</c> or from a <c>DataSource</c> assignment traced through the
/// code-behind, and is a question about the page's semantics rather than about its syntax.
/// </para>
/// </remarks>
internal static class AspxBindingDiagnostics
{
    private const string UnknownMember = "WFB0001";
    private const string DiagnosticSource = "roslyn-sense";

    public static async Task<Protocol.Diagnostic[]> DiagnosticsAsync(
        AspxDocument document, CancellationToken ct)
    {
        var settings = MarkupBindingSettings.Current;
        if (!settings.UnknownMemberDiagnostic)
            return [];

        List<Protocol.Diagnostic>? found = null;

        var arguments = DataBindingService.AllArguments(document.Text)
            .Concat(MarkupBindingSites.Enumerate(document).Select(site => site.Value));

        foreach (var argument in arguments)
        {
            ct.ThrowIfCancellationRequested();

            // Silence rather than doubt when the item type is unknown. A container that declares
            // no ItemType and whose DataSource could not be traced is ordinary, and every path
            // under it would light up — which trains the reader to ignore the rule everywhere
            // else, including where it is right.
            if (await DataBindingService.ItemTypeAsync(document, argument.Start, ct) is not { } itemType)
                continue;

            foreach (var segment in DataBindingService.Segments(document.Text, argument, itemType))
            {
                if (segment.Symbol is not null || segment.Name.Length == 0)
                    continue;

                (found ??= []).Add(new Protocol.Diagnostic(
                    AspxLanguageHandler.ToRange(document, segment.Span),
                    LspConverters.ToLspSeverity(settings.Severity),
                    UnknownMember,
                    DiagnosticSource,
                    $"'{itemType.ToDisplayString()}' has no member named '{segment.Name}'."));

                // The first unresolved segment only. Everything after it is a member of a type
                // nobody knows, so the resolution stopped there — reporting the rest would be
                // reporting the same one mistake once per remaining dot.
                break;
            }
        }

        return found is null ? [] : [.. found];
    }
}
