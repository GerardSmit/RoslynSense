using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynMCP.Services;
using RoslynMCP.Tools;

namespace RoslynMCP.Resources;

/// <summary>
/// Exposes a C# file's structural outline as an MCP resource template.
/// Delegates to <see cref="GetFileOutlineTool"/> for the actual outline generation,
/// making the same data available as attachable context rather than a tool call.
/// </summary>
[McpServerResourceType]
public static class FileOutlineResource
{
    /// <summary>
    /// Returns the namespace/type/member outline of a C# file with line numbers.
    /// </summary>
    [McpServerResource(
        Name = "file-outline",
        UriTemplate = "roslyn://file-outline/{filePath}",
        MimeType = "text/plain"),
     Description(
        "Get a compact structural outline (namespaces, types, members with line numbers) " +
        "of a C# file. Same data as the GetFileOutline tool, available as attachable context.")]
    public static async Task<string> GetFileOutlineAsync(
        string filePath,
        IOutputFormatter fmt,
        CancellationToken cancellationToken = default)
    {
        return await GetFileOutlineTool.GetFileOutline(filePath, fmt, handlers: null, cancellationToken: cancellationToken);
    }
}
