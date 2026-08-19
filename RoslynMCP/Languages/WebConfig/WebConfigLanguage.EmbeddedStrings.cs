using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMCP.Languages.WebConfig.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Languages.WebConfig;

/// <summary>
/// The setting name inside a C# string literal — <c>ConfigurationManager.AppSettings["CdnRoot"]</c>
/// — claimed by the pack rather than found by Roslyn.
/// </summary>
/// <remarks>
/// The literal carries no signal Roslyn can read: no <c>[StringSyntax]</c> could be written onto
/// <c>NameValueCollection</c>'s indexer without owning System.Configuration, and the property it
/// is read from is what makes the argument a setting name — which is not something an attribute
/// on the indexer could say. See <see cref="IConfiguredStringLanguage"/>.
/// <para>
/// F12 on the literal is the reverse of the lens above the entry: the <c>&lt;add&gt;</c> is the
/// declaration and the literal is a reference to it. The provider ignores <c>typeDefinition</c> —
/// a setting has no type — and hover answers with the value the file gives it, which is the
/// question actually being asked when someone stops on one of these.
/// </para>
/// </remarks>
internal sealed partial class WebConfigLanguage :
    IConfiguredStringLanguage, IEmbeddedDefinitionProvider, IEmbeddedHoverProvider,
    IEmbeddedCompletionProvider
{
    /// <summary>What a claimed token reports as its language, and what
    /// <c>// lang=webconfigsetting</c> above a literal names.</summary>
    private const string SettingSyntaxIdentifier = "WebConfigSetting";

    public ImmutableArray<string> StringSyntaxIdentifiers { get; } = [SettingSyntaxIdentifier];

    /// <summary>
    /// Whether this literal is the name argument of a configuration read.
    /// </summary>
    /// <remarks>
    /// Syntax first and semantics only for the tokens that survive it: this runs against every
    /// string literal in a document on the diagnostics pass, and binding each one would be a
    /// semantic question per literal in the solution.
    /// </remarks>
    public async Task<string?> DetectAsync(
        Document document, SyntaxToken token, SemanticModel semanticModel, CancellationToken ct)
    {
        if (!token.IsKind(SyntaxKind.StringLiteralToken)
            || token.Parent is not LiteralExpressionSyntax literal)
        {
            return null;
        }

        return await ConfigurationManagerUsageIndex.SectionOfReadAsync(
            literal, semanticModel, document.Project.Solution, ct) is not null
            ? SettingSyntaxIdentifier
            : null;
    }

    public async Task<LspLocation[]> DefinitionAsync(
        EmbeddedStringContext context, bool typeDefinition, CancellationToken ct)
    {
        if (await ReadAsync(context, ct) is not { Name.Length: > 0 } read)
            return [];

        return WebConfigReferenceService.Declarations(
            context.Document.Project.FilePath, read.Section, read.Name);
    }

    public async Task<Hover?> HoverAsync(EmbeddedStringContext context, CancellationToken ct)
    {
        if (await ReadAsync(context, ct) is not { Name.Length: > 0 } read)
            return null;

        var entries = Declared(context.Document.Project.FilePath, read.Section, read.Name);

        if (entries.Count == 0)
            return null;

        var builder = new StringBuilder("**").Append(entries[^1].Name).Append("**");

        // The last declaration in override order — the one nearest a page in a subdirectory, and
        // the one a reader in that directory gets.
        if (entries[^1].Value is { } value)
            builder.Append("\n\n```text\n").Append(value).Append("\n```");

        if (entries[^1].Provider is { Length: > 0 } provider)
            builder.Append("\n\nProvider: `").Append(provider).Append('`');

        builder.Append("\n\nDeclared in ");

        for (int i = 0; i < entries.Count; i++)
        {
            if (i > 0)
                builder.Append(", ");

            builder.Append('`').Append(Path.GetFileName(entries[i].FilePath)).Append('`');
        }

        var text = await context.Document.GetTextAsync(ct);

        return new Hover(
            new MarkupContent("markdown", builder.ToString()),
            LspConverters.ToRange(text.Lines, ContentSpan(context.Token)));
    }

    /// <summary>Every name the section declares, so a read completes to one that exists.</summary>
    public async Task<CompletionList> CompletionAsync(
        EmbeddedStringContext context, CompletionParams p, CancellationToken ct)
    {
        if (await ReadAsync(context, ct) is not { } read)
            return new CompletionList(false, []);

        var text = await context.Document.GetTextAsync(ct);

        // The whole literal, not the prefix typed so far: the caret can be anywhere in it, and an
        // edit that replaces only what precedes it leaves the rest of the old name behind.
        var range = LspConverters.ToRange(text.Lines, ContentSpan(context.Token));

        var items = new List<CompletionItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string configFile in WebConfigSettings.ConfigFilesFor(
            context.Document.Project.FilePath ?? string.Empty))
        {
            if (WebConfigDocumentCache.Get(configFile) is not { } document)
                continue;

            foreach (var entry in document.Entries)
            {
                if (entry.Section != read.Section || !seen.Add(entry.Name))
                    continue;

                // Complete as sent: completionItem/resolve carries no document, and an embedded
                // literal has no URI of its own to route one back by.
                items.Add(new CompletionItem(
                    entry.Name,
                    LspCompletionItemKind.Value,
                    Inline(entry.Value),
                    entry.Name,
                    entry.Name,
                    new TextEdit(range, entry.Name)));
            }
        }

        return items.Count == 0 ? new CompletionList(false, []) : new CompletionList(false, [.. items]);
    }

    /// <summary>How much of a value fits in a completion item's detail before it stops being a
    /// glance.</summary>
    private const int DetailLength = 80;

    private static string? Inline(string? value) =>
        value is null ? null
        : value.Length <= DetailLength ? value
        : value[..DetailLength] + "…";

    /// <summary>The section and name a claimed literal reads.</summary>
    private static async Task<(WebConfigSection Section, string Name)?> ReadAsync(
        EmbeddedStringContext context, CancellationToken ct)
    {
        if (context.Token.Parent is not LiteralExpressionSyntax literal)
            return null;

        return await ConfigurationManagerUsageIndex.SectionOfReadAsync(
            literal, context.SemanticModel, context.Document.Project.Solution, ct) is { } section
                ? (section, literal.Token.ValueText)
                : null;
    }

    /// <summary>Every entry declaring the name, in override order.</summary>
    private static IReadOnlyList<WebConfigEntry> Declared(
        string? projectFilePath, WebConfigSection section, string name)
    {
        if (projectFilePath is not { Length: > 0 })
            return [];

        var entries = new List<WebConfigEntry>();

        foreach (string configFile in WebConfigSettings.ConfigFilesFor(projectFilePath))
        {
            if (WebConfigDocumentCache.Get(configFile) is { } document
                && document.Find(section, name) is { } entry)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    private static Microsoft.CodeAnalysis.Text.TextSpan ContentSpan(SyntaxToken token) =>
        ConfigurationManagerUsageIndex.ContentSpan(token);
}
