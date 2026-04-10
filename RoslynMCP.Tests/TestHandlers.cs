using RoslynMCP.Services;
using RoslynMCP.Tools;
using RoslynMCP.Tools.Razor;
using RoslynMCP.Tools.WebForms;

namespace RoslynMCP.Tests;

/// <summary>
/// Provides default handler instances for tests that call tool methods directly.
/// </summary>
internal static class TestHandlers
{
    public static IGoToDefinitionHandler[] GoToDefinition { get; } =
        [new AspxGoToDefinition(new MarkdownFormatter()), new RazorGoToDefinition(new MarkdownFormatter())];

    public static IOutlineHandler[] Outline { get; } =
        [new AspxOutline(), new RazorOutline()];

    public static IRenameHandler[] Rename { get; } =
        [new AspxRename(), new RazorRename()];

    public static IDiagnosticsHandler[] Diagnostics { get; } =
        [new AspxDiagnostics(), new RazorDiagnostics()];
}
