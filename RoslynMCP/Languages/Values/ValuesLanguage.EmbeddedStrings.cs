using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynMCP.Languages.Values.Core;

namespace RoslynMCP.Languages.Values;

/// <summary>
/// A literal the configuration says belongs to a value set, claimed by the pack rather than found
/// by Roslyn.
/// </summary>
/// <remarks>
/// No <c>[StringSyntax]</c> could express this. The parameters and properties involved are ordinary
/// <c>string</c>s in code nobody here owns — often generated, often in another assembly — and the
/// attribute could not say the thing that matters anyway: <i>which</i> set, which is a fact about
/// this solution's data and not about the member's type. See <see cref="IConfiguredStringLanguage"/>.
/// </remarks>
internal sealed partial class ValuesLanguage : IConfiguredStringLanguage
{
    /// <summary>What a claimed token reports as its language, and what <c>// lang=valueset</c>
    /// above a literal names.</summary>
    private const string SyntaxIdentifier = "ValueSet";

    public ImmutableArray<string> StringSyntaxIdentifiers { get; } = [SyntaxIdentifier];

    public Task<string?> DetectAsync(
        Document document, SyntaxToken token, SemanticModel semanticModel, CancellationToken ct) =>
        Task.FromResult(
            Settings.Enabled
            && ValueSiteSearch.Match(Settings, semanticModel, token, ct) is not null
                ? SyntaxIdentifier
                : null);

    /// <summary>The site the context is about, for the three features that all start the same way.</summary>
    private ValueSite? Site(EmbeddedStringContext context, CancellationToken ct) =>
        Settings.Enabled
            ? ValueSiteSearch.Match(Settings, context.SemanticModel, context.Token, ct)
            : null;
}
