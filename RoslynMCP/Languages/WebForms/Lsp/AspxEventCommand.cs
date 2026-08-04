using System.Text.Json;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Languages.WebForms.Core;
using WebFormsCore;
using RoslynMCP.Services;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;

namespace RoslynMCP.Languages.WebForms.Lsp;

/// <summary>
/// Backs <see cref="ExecuteCommandHandler.GenerateEventHandlerCommand"/>: the client has just
/// committed an event-handler name into markup and now needs the method to exist.
/// </summary>
/// <remarks>
/// The command deliberately depends on nothing the client just inserted. It is given the offset
/// of the control's start tag as it stood when the completion list was built, and the event
/// attribute's name — both of which survive the insertion, because the attribute is written
/// inside the tag and the tag's start does not move. That means the command works whether or not
/// the matching <c>didChange</c> has reached the server yet.
/// </remarks>
internal static class AspxEventCommand
{
    public static async Task<object> ExecuteAsync(ExecuteCommandParams p, CancellationToken ct)
    {
        if (p.Arguments is not [var uriArg, var offsetArg, var attributeArg, var handlerArg, ..])
            return "Expected [uri, startTagOffset, attributeName, handlerName].";

        if (uriArg.ValueKind != JsonValueKind.String
            || attributeArg.ValueKind != JsonValueKind.String
            || handlerArg.ValueKind != JsonValueKind.String
            || !offsetArg.TryGetInt32(out int startTagOffset))
            return "Malformed arguments.";

        string path = LspConverters.UriToPath(uriArg.GetString()!);
        string attributeName = attributeArg.GetString()!;
        string handlerName = handlerArg.GetString()!;

        var document = await AspxDocumentService.GetAsync(path, ct);
        if (document?.Tree is not { } root || document.CodeBehind is null)
            return "No parsed markup for this file.";

        var control = AspxSymbolResolver.EnumerateControls(root)
            .FirstOrDefault(c => c.StartTag.Range.Start.Offset == startTagOffset);
        if (control is null)
            return "The control this handler belongs to is no longer there.";

        if (AspxSymbolResolver.TryGetEvent(control.ControlType, attributeName) is not { } @event)
            return $"'{control.ControlType.Name}' has no event behind '{attributeName}'.";

        // Already written — committing the same item twice must not append a second method.
        if (document.CodeBehind.GetDeep<Microsoft.CodeAnalysis.IMethodSymbol>(handlerName) is not null)
            return $"'{handlerName}' already exists.";

        var generated = await AspxEventHandlerService.GenerateAsync(document, @event, handlerName, ct);
        if (generated is not var (filePath, changes) || changes.Count == 0)
            return "Could not find a code-behind file to write the handler into.";

        var target = Services.WorkspaceService.FindDocumentInProject(document.Project, filePath);
        if (target is null)
            return "Could not find the code-behind document.";

        var text = await target.GetTextAsync(ct);
        string updated = text.WithChanges(changes).ToString();

        if (await LspSessionRegistry.TryApplyFullTextEditAsync(
                filePath, updated, $"Generate {handlerName}", ct))
        {
            return $"Generated '{handlerName}'.";
        }

        // The file is not open in an editor, so there is no buffer to race: write it.
        await File.WriteAllTextAsync(filePath, updated, ct);
        return $"Generated '{handlerName}' in {Path.GetFileName(filePath)}.";
    }
}
