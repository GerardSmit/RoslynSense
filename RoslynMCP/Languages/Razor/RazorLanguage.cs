using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynMCP.Languages.Razor.Tools;
using RoslynMCP.Services;
using RoslynMCP.Tools;

namespace RoslynMCP.Languages.Razor;

/// <summary>
/// Razor — <c>.razor</c> components and <c>.cshtml</c> views — as a pack over the existing MCP
/// tool handlers.
/// </summary>
/// <remarks>
/// MCP-only, deliberately. Razor's editor support in VS Code comes from the C# Dev Kit's own
/// Razor server, so this pack advertises no LSP providers and every editor request about a
/// <c>.cshtml</c> falls through to the C# handlers exactly as it does today. What it does own is
/// the tool surface an AI session uses, where the generated document is reached through
/// <see cref="RazorSourceMappingService"/> rather than through a projection of our own.
/// </remarks>
internal sealed class RazorLanguage :
    ILanguagePack,
    IGoToDefinitionHandler,
    IOutlineHandler,
    IRenameHandler,
    IDiagnosticsHandler
{
    private readonly RazorGoToDefinition _goToDefinition;
    private readonly RazorOutline _outline = new();
    private readonly RazorRename _rename = new();
    private readonly RazorDiagnostics _diagnostics = new();

    public RazorLanguage(IOutputFormatter formatter) =>
        _goToDefinition = new RazorGoToDefinition(formatter);

    public string Id => "razor";

    public string DisplayName => "Razor";

    public ImmutableArray<string> FileExtensions { get; } = [".razor", ".cshtml"];

    public LanguageCapabilities Capabilities => LanguageCapabilities.None;

    /// <summary>The two base classes the Razor compiler generates against: a component and an
    /// MVC view. Neither present means the project has no Razor in it.</summary>
    public ImmutableArray<string> WellKnownTypeNames { get; } =
    [
        "Microsoft.AspNetCore.Components.ComponentBase",
        "Microsoft.AspNetCore.Mvc.Razor.RazorPageBase",
    ];

    public ImmutableArray<SymbolKind> InterestingSymbolKinds { get; } =
        [SymbolKind.NamedType, SymbolKind.Method, SymbolKind.Property, SymbolKind.Field];

    /// <summary>
    /// Razor's generated C# is a Roslyn source-generated document, not a file this pack invented.
    /// It is served under the generated URI scheme, which resolution excludes for every pack.
    /// </summary>
    public bool IsProjectionPath(string? filePath) => false;

    public bool CanHandle(string filePath) => RazorSourceMappingService.IsRazorFile(filePath);

    public Task<string> ResolveAsync(
        string systemPath, string markupSnippet, int contextLines, CancellationToken cancellationToken) =>
        _goToDefinition.ResolveAsync(systemPath, markupSnippet, contextLines, cancellationToken);

    public Task<string> GetOutlineAsync(string systemPath, CancellationToken cancellationToken) =>
        _outline.GetOutlineAsync(systemPath, cancellationToken);

    public Task<List<RenameChangedFile>> UpdateReferencesAsync(
        Project project, Solution solution, ISymbol symbol,
        string oldName, string newName, CancellationToken cancellationToken) =>
        _rename.UpdateReferencesAsync(project, solution, symbol, oldName, newName, cancellationToken);

    public Task<string> ValidateAsync(
        string systemPath, IOutputFormatter fmt, CancellationToken cancellationToken) =>
        _diagnostics.ValidateAsync(systemPath, fmt, cancellationToken);
}
