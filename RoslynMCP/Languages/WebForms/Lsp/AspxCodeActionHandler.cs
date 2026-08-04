using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Languages.WebForms.Core;
using WebFormsCore.Nodes;
using RoslynMCP.Services;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;

namespace RoslynMCP.Languages.WebForms.Lsp;

/// <summary>
/// The quick fixes a WebForms file needs: writing the code-behind method an event attribute
/// names, and wiring a control's default event the way double-clicking it in the designer used
/// to.
/// </summary>
/// <remarks>
/// Neither action computes the generated method while it is merely being listed. A client asks
/// for code actions on every cursor move, and generating means running the simplifier and the
/// formatter over the code-behind — so the method arrives from
/// <see cref="ExecuteCommandHandler.GenerateEventHandlerCommand"/> once the action is actually
/// invoked. Only the markup half, which is a span and a string, is computed up front.
/// </remarks>
internal static class AspxCodeActionHandler
{
    public static async Task<CodeAction[]> CodeActionsAsync(CodeActionParams p, CancellationToken ct)
    {
        string path = LspConverters.UriToPath(p.TextDocument.Uri);
        var document = await AspxDocumentService.GetAsync(path, ct);
        if (document?.Tree is null || document.CodeBehind is null)
            return [];

        int offset = LspConverters.ToOffset(document.SourceText, p.Range.Start);
        var hit = AspxSymbolResolver.ResolveAt(document, offset);
        if (hit is null)
            return [];

        var actions = new List<CodeAction>();

        if (MissingHandlerAction(document, hit) is { } fix)
            actions.Add(fix);

        if (DefaultEventAction(document, hit) is { } wire)
            actions.Add(wire);

        return actions.ToArray();
    }

    /// <summary>
    /// <c>OnClick="BtnSave_Click"</c> with no such method: write it, with the signature the
    /// event's delegate demands.
    /// </summary>
    private static CodeAction? MissingHandlerAction(AspxDocument document, AspxHit hit)
    {
        if (hit is not
            { Kind: AspxHitKind.EventHandler, Symbol: null, Event: { } @event, Name: { Length: > 0 } name })
            return null;

        if (hit.Element is not ControlNode control || !IsIdentifier(name))
            return null;

        if (AspxEventHandlerService.FindCodeBehindDocument(
                document.CodeBehind!, document.Project, document.FilePath) is not { } target)
            return null;

        return new CodeAction(
            $"Generate event handler '{name}' in {Path.GetFileName(target.FilePath ?? target.Name)}",
            "quickfix",
            Edit: null)
        {
            Command = GenerateCommand(document, control, @event, name),
        };
    }

    /// <summary>
    /// The caret is on a control whose <c>[DefaultEvent]</c> is not wired: add the attribute and
    /// the method in one gesture.
    /// </summary>
    private static CodeAction? DefaultEventAction(AspxDocument document, AspxHit hit)
    {
        if (hit is not { Kind: AspxHitKind.ControlType or AspxHitKind.ControlId, Element: ControlNode control })
            return null;

        if (AspxCatalog.DefaultEvent(control.ControlType) is not { } @event)
            return null;

        string attribute = "On" + @event.Name;
        if (control.RawAttributes.Keys.Any(k => k.Value.Equals(attribute, StringComparison.OrdinalIgnoreCase)))
            return null;

        if (AttributeInsertion(document, control, attribute, out var markup) is not { } name)
            return null;

        return new CodeAction(
            $"Wire {@event.Name} to '{name}'",
            "refactor",
            new WorkspaceEdit(new Dictionary<string, TextEdit[]>
            {
                [LspConverters.PathToUri(document.FilePath)] = [markup!],
            }))
        {
            Command = GenerateCommand(document, control, @event, name),
        };
    }

    private static Command GenerateCommand(
        AspxDocument document, ControlNode control, IEventSymbol @event, string handlerName) =>
        new(
            "Generate event handler",
            ExecuteCommandHandler.GenerateEventHandlerCommand,
            [
                LspConverters.PathToUri(document.FilePath),
                control.StartTag.Range.Start.Offset,
                "On" + @event.Name,
                handlerName,
            ]);

    /// <summary>
    /// Builds the edit that adds ` OnClick="Handler"` immediately before the start tag's closing
    /// bracket, and returns the handler name it chose.
    /// </summary>
    private static string? AttributeInsertion(
        AspxDocument document, ControlNode control, string attribute, out TextEdit? edit)
    {
        edit = null;

        int end = Math.Min(control.StartTag.Range.End.Offset, document.Text.Length);
        int i = end - 1;

        while (i >= 0 && char.IsWhiteSpace(document.Text[i]))
            i--;
        if (i < 0 || document.Text[i] != '>')
            return null;

        i--; // before '>'
        if (i >= 0 && document.Text[i] == '/')
            i--; // before the '/' of a self-closing tag

        int insert = i + 1;

        var @event = AspxSymbolResolver.TryGetEvent(control.ControlType, attribute);
        if (@event is null)
            return null;

        string name = AspxEventHandlerService.SuggestName(control, @event, document.CodeBehind);
        edit = new TextEdit(
            AspxLanguageHandler.ToRange(document, new TextSpan(insert, 0)),
            $" {attribute}=\"{name}\"");
        return name;
    }

    private static bool IsIdentifier(string name) =>
        name.Length > 0
        && (char.IsLetter(name[0]) || name[0] == '_')
        && name.All(c => char.IsLetterOrDigit(c) || c == '_');
}
