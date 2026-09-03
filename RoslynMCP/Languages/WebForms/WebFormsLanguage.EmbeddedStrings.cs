using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynMCP.Languages.WebForms.Core;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Languages.WebForms;

/// <summary>
/// A control ID inside a C# string literal — <c>item.FindControl("btnAction")</c> — claimed by
/// the pack rather than found by Roslyn.
/// </summary>
/// <remarks>
/// Like a resource key, the literal carries no signal Roslyn can read: no <c>[StringSyntax]</c>
/// could be written onto <c>Control.FindControl</c> without owning System.Web, and what makes a
/// wrapper method's argument a control ID is its body forwarding the parameter — nothing an
/// attribute could say. See <see cref="IConfiguredStringLanguage"/>.
/// <para>
/// F12 on the literal is the reverse of the gesture the pack already answers in markup: the
/// <c>ID</c> attribute is the declaration, and the literal is a reference to it. The provider
/// ignores <c>typeDefinition</c> — an ID has no type, so both questions get the declaration.
/// </para>
/// </remarks>
internal sealed partial class WebFormsLanguage : IConfiguredStringLanguage, IEmbeddedDefinitionProvider
{
    /// <summary>What a claimed token reports as its language, and what <c>// lang=aspxcontrolid</c>
    /// above a literal names.</summary>
    private const string ControlIdSyntaxIdentifier = "AspxControlId";

    public ImmutableArray<string> StringSyntaxIdentifiers { get; } = [ControlIdSyntaxIdentifier];

    public Task<string?> DetectAsync(
        Document document, SyntaxToken token, SemanticModel semanticModel, CancellationToken ct) =>
        Task.FromResult(
            FindControlNavigationService.IsFindControlIdLiteral(token, semanticModel, ct)
                ? ControlIdSyntaxIdentifier
                : null);

    public Task<LspLocation[]> DefinitionAsync(
        EmbeddedStringContext context, bool typeDefinition, CancellationToken ct) =>
        FindControlNavigationService.DefinitionsAsync(context, ct);
}
