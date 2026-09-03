using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services.ProjectModel;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>
/// The New menu's two requests: what can be added here, and add it.
/// </summary>
/// <remarks>
/// The catalogue is computed rather than configured, so the editor asks per node instead of
/// carrying a list of its own. That is the whole point of putting it here: which templates apply
/// depends on the SDK, on <c>UseWPF</c>, on which test framework is referenced and on whether the
/// project is a legacy System.Web site — facts the server already has and the extension does not.
/// </remarks>
internal static class ItemTemplatesHandler
{
    public static async Task<ItemTemplatesResult> ListAsync(
        ItemTemplatesParams p, CancellationToken ct)
    {
        var templates = await ItemTemplates.ForAsync(Path.GetFullPath(p.Path), ct);

        return new ItemTemplatesResult(
            [.. templates.Select(template => new ItemTemplateInfo(
                template.Id,
                template.Label,
                template.Group,
                template.DefaultName,
                template.Detail,
                template.Fixed))]);
    }

    public static async Task<CreateItemResult> CreateAsync(CreateItemParams p, CancellationToken ct)
    {
        var result = await ItemTemplates.CreateAsync(
            p.TemplateId, Path.GetFullPath(p.Path), p.Name, ct);

        return new CreateItemResult(result.Ok, result.Message, [.. result.Paths]);
    }
}
