using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.WebForms;

/// <summary>
/// ASPX-family markup — <c>.aspx</c>, <c>.ascx</c>, <c>.master</c> and their siblings — as one
/// pack owning both front-ends: the LSP features the editor asks for and the MCP tools an AI
/// session calls.
/// </summary>
/// <remarks>
/// Split across partial files by feature, so the pack can grow a provider at a time without every
/// change landing in the same file. Each part forwards into <c>WebForms/Core</c> and
/// <c>WebForms/Lsp</c>; nothing decides anything here.
/// </remarks>
internal sealed partial class WebFormsLanguage : ILanguagePack
{
    public WebFormsLanguage(IOutputFormatter formatter) => InitializeToolHandlers(formatter);

    public string Id => "webforms";

    public string DisplayName => "ASP.NET WebForms";

    public ImmutableArray<string> FileExtensions { get; } =
        [".aspx", ".ascx", ".master", ".asax", ".ashx", ".asmx"];

    /// <summary>
    /// Markup's equivalent of C#'s ".": <c>&lt;</c> opens a tag, <c>:</c> separates the prefix
    /// from the control name, and <c>=</c> and the quotes open an attribute value.
    /// <c>generateEventHandler</c> is the pack's own command — a completion item cannot carry an
    /// edit to another file, so committing <c>OnClick="Save_Click"</c> asks for the code-behind
    /// method through <c>workspace/executeCommand</c> instead.
    /// </summary>
    public LanguageCapabilities Capabilities { get; } = new(
        CompletionTriggerCharacters: ["<", ":", "=", "\"", "'"],
        SignatureHelpTriggerCharacters: [],
        Commands: [ExecuteCommandHandler.GenerateEventHandlerCommand],
        FileOperationGlobs:
        [
            "**/*.aspx", "**/*.ascx", "**/*.master", "**/*.asax", "**/*.ashx", "**/*.asmx",
        ],
        SemanticTokenTypes: [.. SemanticTokenTypeNames],
        SemanticTokenModifiers: [],
        SupportsBreakpoints: true);

    /// <summary>
    /// The two control base classes. Markup needs one of them to mean anything, so a project
    /// where neither resolves has no WebForms in it and the contributors can decline without
    /// touching the file system — the check
    /// <see cref="AspxReferenceService.HostsWebFormsAsync"/> already makes per project.
    /// </summary>
    public ImmutableArray<string> WellKnownTypeNames { get; } =
        ["System.Web.UI.Control", "WebFormsCore.UI.Control"];

    /// <summary>
    /// Markup refers to code-behind members by name from attributes: a handler method, a control
    /// field, the page class in <c>Inherits</c>. Those are the only symbols a markup pass can
    /// have anything to say about.
    /// </summary>
    public ImmutableArray<SymbolKind> InterestingSymbolKinds { get; } =
        [SymbolKind.NamedType, SymbolKind.Method, SymbolKind.Property, SymbolKind.Field, SymbolKind.Event];

    public bool IsProjectionPath(string? filePath) =>
        AspxProjectionService.IsProjectionPath(filePath);
}
