using System.Text;
using RoslynMCP.Languages.WebConfig.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services.MetadataConfiguration;

namespace RoslynMCP.Languages.WebConfig;

internal sealed partial class WebConfigLanguage : ILanguageHoverProvider
{
    /// <summary>
    /// Hover on an entry: what it is worth, and what the override chain does to it.
    /// </summary>
    /// <remarks>
    /// The value is shown even though it is on the same line, because the interesting half is the
    /// second one — a setting a nested config also declares is a setting whose value here may
    /// never be the one that runs, and nothing on the line says so.
    /// </remarks>
    public async Task<Hover?> HoverAsync(TextDocumentPositionParams p, CancellationToken ct)
    {
        if (await ViewAndEntryAsync(p.TextDocument.Uri, p.Position, ct) is not ({ } view, { } entry))
            return null;

        var builder = new StringBuilder("**").Append(entry.Name).Append("**");

        if (entry.Value is { } value)
            builder.Append("\n\n```text\n").Append(value).Append("\n```");

        if (entry.Provider is { Length: > 0 } provider)
            builder.Append("\n\nProvider: `").Append(provider).Append('`');

        if (view.Project is { } project)
        {
            var external = await MetadataConfigurationIndex.GetAsync(project, ct);
            ExternalReferences.Append(builder, external.ReadsFor(
                WebConfigMetadataReads.KindOf(entry.Section), entry.Name));
        }

        var chain = WebConfigOverrides.ChainFor(view.Project?.FilePath, entry.Section, entry.Name);

        if (chain.Length > 1)
        {
            ConfigOverrides.Append(builder, chain, view.FilePath);
            builder.Append("\n\nThe declaration nearest the reading file wins.");
        }

        return new Hover(
            new MarkupContent("markdown", builder.ToString()),
            LspConverters.ToRange(view.Text.Lines, entry.NameSpan));
    }
}
