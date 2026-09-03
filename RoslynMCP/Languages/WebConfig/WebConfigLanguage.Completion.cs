using Microsoft.Language.Xml;
using RoslynMCP.Languages.WebConfig.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services.MetadataConfiguration;
using SourceText = Microsoft.CodeAnalysis.Text.SourceText;
using TextSpan = Microsoft.CodeAnalysis.Text.TextSpan;

namespace RoslynMCP.Languages.WebConfig;

internal sealed partial class WebConfigLanguage : ILanguageCompletionProvider
{
    private const int KindProperty = 10;

    /// <summary>
    /// The names that belong in a <c>key</c> or <c>name</c> attribute: the settings the
    /// application reads and this file does not declare.
    /// </summary>
    /// <remarks>
    /// The mirror image of the reference count. A lens saying "0 references" finds the settings
    /// that outlived their code; this finds the code that outlived its settings — a read of
    /// <c>ConfigurationManager.AppSettings["Timeout"]</c> against a file with no <c>Timeout</c> in
    /// it, which fails as a null at runtime and as nothing at all before then. Reads from
    /// referenced assemblies are offered on the same footing, since a key a package needs is one
    /// this file has to declare and nothing in the solution's own source will ever mention.
    /// </remarks>
    public async Task<CompletionList> CompletionAsync(
        CompletionParams p, LspResolveCache cache, CancellationToken ct)
    {
        if (await WebConfigWorkspace.GetAsync(
                LspConverters.UriToPath(p.TextDocument.Uri), ct) is not { } view)
        {
            return new CompletionList(false, []);
        }

        int offset = LspConverters.ToOffset(view.Text, p.Position);

        if (NameAttribute(view.Text, offset) is not ({ } section, { } replace))
            return new CompletionList(false, []);

        var external = view.Project is { } project
            ? await MetadataConfigurationIndex.GetAsync(project, ct)
            : MetadataConfigurationIndex.Empty;

        var wanted = WebConfigMetadataReads.Wanted(
            section, view.Document.Entries, view.Index.Usages, view.MarkupUsages, external.Reads);

        if (wanted.Count == 0)
            return new CompletionList(false, []);

        var range = LspConverters.ToRange(view.Text.Lines, replace);

        return new CompletionList(false,
        [
            .. wanted.Select((entry, index) => new CompletionItem(
                entry.Key, KindProperty, entry.Value,
                SortText: index.ToString("D3"),
                FilterText: entry.Key,
                TextEdit: new TextEdit(range, entry.Key))),
        ]);
    }

    public Task<CompletionItem> ResolveCompletionAsync(
        CompletionItem item, LspResolveCache cache, CancellationToken ct) =>
        Task.FromResult(item);

    /// <summary>
    /// The section a caret is naming an entry of, and the span its value occupies — or null
    /// wherever else in the file the caret is.
    /// </summary>
    /// <remarks>
    /// Reparsed rather than read off the document's entries: a half-typed <c>&lt;add key="</c> has
    /// no entry to find, and that is exactly the moment completion is asked for.
    /// </remarks>
    private static (WebConfigSection Section, TextSpan Replace)? NameAttribute(
        SourceText text, int offset)
    {
        var root = Parser.ParseText(text.ToString());

        foreach (var attribute in root.DescendantNodes().OfType<XmlAttributeSyntax>())
        {
            var span = attribute.ValueSpan.ToRoslynSpan();

            // Inclusive at both ends: the caret sits between the quotes of an empty value, and
            // at the far end of one being typed.
            if (span == default || offset < span.Start || offset > span.End)
                continue;

            if (attribute.Parent is not XmlElementBaseSyntax add
                || LocalName(add) != "add"
                || SectionOf(add) is not { } section)
            {
                return null;
            }

            string expected = section == WebConfigSection.ConnectionStrings ? "name" : "key";

            return string.Equals(attribute.Name, expected, StringComparison.OrdinalIgnoreCase)
                ? (section, span)
                : null;
        }

        return null;
    }

    /// <summary>The section an <c>&lt;add&gt;</c> belongs to, from the element above it.</summary>
    private static WebConfigSection? SectionOf(XmlNodeSyntax add)
    {
        for (var parent = add.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is not XmlElementBaseSyntax element)
                continue;

            return LocalName(element) switch
            {
                "appSettings" => WebConfigSection.AppSettings,
                "connectionStrings" => WebConfigSection.ConnectionStrings,
                _ => null,
            };
        }

        return null;
    }

    private static string LocalName(XmlElementBaseSyntax element)
    {
        string name = element.Name ?? "";
        int colon = name.IndexOf(':');
        return colon >= 0 ? name[(colon + 1)..] : name;
    }
}
