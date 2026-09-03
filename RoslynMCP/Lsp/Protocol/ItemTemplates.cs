using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

/// <summary>
/// roslynSense/itemTemplates: what can be added on this node.
/// </summary>
/// <param name="Path">The solution, project or folder the New menu was opened on.</param>
public sealed record ItemTemplatesParams(
    [property: JsonPropertyName("path")] string Path);

public sealed record ItemTemplatesResult(
    [property: JsonPropertyName("templates")] ItemTemplateInfo[] Templates);

/// <summary>
/// One offer, as the picker needs it.
/// </summary>
/// <param name="Fixed">The name is the template — <c>Web.config</c> under another name is not a
/// web.config — so the picker creates it without asking for one.</param>
public sealed record ItemTemplateInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("group")] string Group,
    [property: JsonPropertyName("defaultName")] string DefaultName,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("fixed")] bool Fixed);

/// <summary>roslynSense/createItem: make one, and say what it made.</summary>
public sealed record CreateItemParams(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("templateId")] string TemplateId,
    [property: JsonPropertyName("name")] string Name);

/// <param name="Paths">Every file created, the one to open first. A template is often more than
/// one file, and the editor should not have to guess which of them is the point.</param>
public sealed record CreateItemResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("paths")] string[] Paths);
