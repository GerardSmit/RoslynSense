using Microsoft.CodeAnalysis;
using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using WebFormsCore;
using LspCodeLens = RoslynMCP.Lsp.Protocol.CodeLens;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Languages.WebForms;

internal sealed partial class WebFormsLanguage : ILanguageCodeLensProvider, ILanguageCodeLensGeneration
{
    /// <summary>As many as the peek window can usefully show, matching the C# handler.</summary>
    private const int MaxReferenceLocations = 100;

    /// <summary>
    /// What a reference count on a markup file depends on, so <see cref="CodeLensResolveMemo"/> can
    /// tell when one is still good.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Text</c> covers an edit to the page itself. <c>Compilation</c> covers an edit to the
    /// code-behind, which changes what the same <c>ID</c> binds to without touching this file, and
    /// it is a snapshot so reference equality is the whole test.
    /// </para>
    /// <para>
    /// Deliberately not the solution as well, which would only look like it covered more. The
    /// document these are read from is memoized on (text, compilation) alone, so a hit hands back
    /// the solution captured when it was parsed — a project grafted in afterwards would compare
    /// equal and change nothing. Widening the workspace is announced instead, by the refresh
    /// <c>LspWorkspaceRefresh</c> sends when the project set moves, which makes the client re-pull
    /// and this memo re-key on whatever compilation the graft produced.
    /// </para>
    /// </remarks>
    private sealed record LensGeneration(string Text, Compilation Compilation);

    /// <remarks>
    /// Without this the memo passes the pack straight through, and every <c>codeLens/resolve</c> —
    /// one per visible control, re-fired on every scroll and every edit — pays a fresh solution-wide
    /// reference search plus a walk of the project's markup. On a page with a couple of hundred
    /// <c>ID</c>s that is the difference between a gutter that fills in and one that arrives twenty
    /// seconds late.
    /// </remarks>
    public async ValueTask<object?> LensGenerationAsync(string uri, CancellationToken ct)
    {
        var document = await AspxDocumentService.GetAsync(LspConverters.UriToPath(uri), ct);
        return document is null
            ? null
            : new LensGeneration(document.Text, document.Compilation);
    }

    /// <summary>
    /// A reference count over every control <c>ID</c> that has a code-behind field — the markup
    /// counterpart of the count the C# handler puts over a member declaration, since the
    /// <c>ID</c> is where that field is really declared.
    /// </summary>
    /// <remarks>
    /// The IDs come from <see cref="WebFormsIndex"/>: codeLens is re-requested on every edit and
    /// every scroll, and walking the parse tree each time to find the same attributes is work the
    /// checksum already says is unnecessary. The counting itself is deferred to
    /// <see cref="ResolveCodeLensAsync"/>, so only lenses the editor actually renders pay for it.
    /// </remarks>
    public async Task<LspCodeLens[]> CodeLensAsync(CodeLensParams p, CancellationToken ct)
    {
        string path = LspConverters.UriToPath(p.TextDocument.Uri);

        // A user control is nothing but control declarations, so the count lands on almost every
        // line and pushes the markup apart — the gutter stops being an annotation and becomes the
        // file's layout. The number is still one gesture away on any ID, by find-references.
        if (IsUserControl(path))
            return [];

        var index = await WebFormsIndex.GetAsync(path, ct);
        var document = await AspxDocumentService.GetAsync(path, ct);
        if (index is null || document?.CodeBehind is not { } codeBehind)
            return [];

        var lenses = new List<LspCodeLens>();

        foreach (var control in index.Controls)
        {
            // A control inside a template is reached through FindControl and has no field, so
            // there is no declaration for a count to be about.
            if (codeBehind.GetMemberDeep(control.Id) is null)
                continue;

            lenses.Add(new LspCodeLens(LspConverters.ToRange(control.Span), Command: null)
            {
                Data = new CodeLensData(
                    p.TextDocument.Uri, control.Span.Start.Line, control.Span.Start.Character,
                    "references"),
            });
        }

        return [.. lenses];
    }

    /// <summary>
    /// Counts the references to one visible lens's control, in C# and in markup both.
    /// </summary>
    /// <remarks>
    /// Known inconsistency, accepted: the same count over a C# member comes from Roslyn alone
    /// (<c>CodeLensHandler.ResolveAsync</c>) and therefore omits the markup that names it, while
    /// this one includes both halves. Making them agree means running every pack's reference
    /// contributor per lens on the C# side, and a C# file has a lens on every member — the cost
    /// is not proportionate to a number in the gutter. <c>textDocument/references</c> is the
    /// answer that is complete from either side.
    /// </remarks>
    public async Task<LspCodeLens> ResolveCodeLensAsync(LspCodeLens lens, CancellationToken ct)
    {
        if (lens.Data is not { Kind: "references" } data)
            return lens;

        var none = new Command("0 references", "roslynSense.showReferences",
            [data.Uri, data.Line, data.Character, Array.Empty<LspLocation>()]);

        var document = await AspxDocumentService.GetAsync(LspConverters.UriToPath(data.Uri), ct);
        if (document is null)
            return lens with { Command = none };

        int offset = LspConverters.ToOffset(
            document.SourceText, new Position(data.Line, data.Character));

        if (AspxSymbolResolver.ResolveAt(document, offset)
            is not { Kind: AspxHitKind.ControlId, Symbol: { } field })
        {
            return lens with { Command = none };
        }

        // The ID attribute is the declaration, and the markup pass returns it the way Roslyn
        // returns a declaration — a lens that counted itself would read "1 reference" on a
        // control nothing uses.
        var (project, target) = await AspxDocumentService.AnchorAsync(document, field, ct);
        var locations = (await NavigationHandlers.AllReferencesAsync(
                target, project, includeDeclaration: false, ct))
            .Where(location => !IsSelf(location, data))
            .ToArray();

        string title = locations.Length == 1 ? "1 reference" : $"{locations.Length} references";
        return lens with
        {
            Command = new Command(title, "roslynSense.showReferences",
                [data.Uri, data.Line, data.Character, locations.Take(MaxReferenceLocations).ToArray()]),
        };
    }

    /// <summary>Whether the path is a user control rather than a page.</summary>
    private static bool IsUserControl(string path) =>
        Path.GetExtension(path).Equals(".ascx", StringComparison.OrdinalIgnoreCase);

    private static bool IsSelf(LspLocation location, CodeLensData data) =>
        location.Range.Start.Line == data.Line
        && location.Range.Start.Character == data.Character
        && string.Equals(location.Uri, data.Uri, StringComparison.OrdinalIgnoreCase);
}
