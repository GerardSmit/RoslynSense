using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMCP.Languages.AppSettings.Core;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Languages.AppSettings;

/// <summary>
/// The configuration path inside a C# string literal — <c>GetValue&lt;string&gt;("App:Host")</c>,
/// <c>GetSection("Example")</c>, <c>Configuration["App:Title"]</c> — claimed by the pack rather
/// than found by Roslyn.
/// </summary>
/// <remarks>
/// The literal carries no signal Roslyn can read: <c>[StringSyntax]</c> would have to be written
/// onto <c>IConfiguration</c>'s own members, and what makes an argument a configuration path is
/// the receiver it is asked of — chained sections included, which no attribute could express. See
/// <see cref="IConfiguredStringLanguage"/>.
/// <para>
/// F12 lands on the key in the settings files, which is where the value is actually decided. It is
/// deliberately not the bound options property: navigating from a read to a property whose own
/// value comes from the JSON puts one more hop between the question and its answer, and the key's
/// own lens already goes the other way. Every file declaring the path is answered, because which
/// one wins depends on the environment the application runs under.
/// </para>
/// </remarks>
internal sealed partial class AppSettingsLanguage :
    IConfiguredStringLanguage, IEmbeddedDefinitionProvider
{
    /// <summary>What a claimed token reports as its language, and what
    /// <c>// lang=configurationpath</c> above a literal names.</summary>
    private const string PathSyntaxIdentifier = "ConfigurationPath";

    public ImmutableArray<string> StringSyntaxIdentifiers { get; } = [PathSyntaxIdentifier];

    /// <summary>
    /// Whether this literal is a configuration path.
    /// </summary>
    /// <remarks>
    /// Syntax first and semantics only for the tokens that survive it: this runs against every
    /// string literal in a document on the diagnostics pass, and binding each one would be a
    /// semantic question per literal in the solution. A literal that is not a read of the
    /// framework's own shapes is then asked about once more, in case the method it is passed to is
    /// one of the solution's own reading methods.
    /// </remarks>
    public async Task<string?> DetectAsync(
        Document document, SyntaxToken token, SemanticModel semanticModel, CancellationToken ct) =>
        token.IsKind(SyntaxKind.StringLiteralToken)
        && token.Parent is LiteralExpressionSyntax literal
        && await ConfigurationUsageIndex.PathOfReadAsync(
            literal, semanticModel, document.Project.Solution, ct) is { Length: > 0 }
            ? PathSyntaxIdentifier
            : null;

    /// <summary>The key in every settings file that declares it. Ignores
    /// <paramref name="typeDefinition"/> — a key has no type.</summary>
    public async Task<LspLocation[]> DefinitionAsync(
        EmbeddedStringContext context, bool typeDefinition, CancellationToken ct)
    {
        if (context.Token.Parent is not LiteralExpressionSyntax literal
            || await ConfigurationUsageIndex.PathOfReadAsync(
                literal, context.SemanticModel, context.Document.Project.Solution, ct)
                is not { Length: > 0 } path)
        {
            return [];
        }

        return AppSettingsReferenceService.Declarations(context.Document.Project.FilePath, path);
    }
}
