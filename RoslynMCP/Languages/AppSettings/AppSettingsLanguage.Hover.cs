using System.Text;
using RoslynMCP.Languages.AppSettings.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services.MetadataConfiguration;

namespace RoslynMCP.Languages.AppSettings;

internal sealed partial class AppSettingsLanguage : ILanguageHoverProvider
{
    /// <summary>
    /// Hover on a key: its path, the property it binds to, and what the rest of the keyspace does
    /// to its value.
    /// </summary>
    /// <remarks>
    /// The path is the half a nested file hides — a key three objects deep is read as
    /// <c>Logging:LogLevel:Default</c>, and the line the caret is on shows only the last segment.
    /// The override chain is the half every file hides: <c>appsettings.json</c> reads as the
    /// value the application uses right up until an overlay replaces it.
    /// </remarks>
    public async Task<Hover?> HoverAsync(TextDocumentPositionParams p, CancellationToken ct)
    {
        if (await AppSettingsWorkspace.GetAsync(LspConverters.UriToPath(p.TextDocument.Uri), ct)
            is not { } view)
        {
            return null;
        }

        int offset = LspConverters.ToOffset(view.Text, p.Position);

        if (view.Document.KeyAt(offset) is not { } key)
            return null;

        var builder = new StringBuilder("**").Append(key.Path).Append("**");

        if (AppSettingsOverrides.ValueText(view.Text, key) is { } value)
            builder.Append("\n\n```text\n").Append(value).Append("\n```");

        if (view.Index.BoundProperty(key.Path) is { } property)
        {
            builder.Append("\n\nBinds to `")
                .Append(property.ContainingType?.Name is { Length: > 0 } type ? type + "." : "")
                .Append(property.Name).Append('`');
        }

        if (view.Project is { } project)
        {
            var external = await MetadataConfigurationIndex.GetAsync(project, ct);
            ExternalReferences.Append(
                builder, external.ReadsFor(MetadataConfigurationKind.Path, key.Path));
        }

        var chain = AppSettingsOverrides.ChainFor(view.Project?.FilePath, key.Path);

        if (chain.Length > 1)
        {
            ConfigOverrides.Append(builder, chain, view.FilePath);
            builder.Append("\n\nWhich one applies depends on the environment the application runs under.");
        }

        return new Hover(
            new MarkupContent("markdown", builder.ToString()),
            LspConverters.ToRange(view.Text.Lines, key.NameSpan));
    }
}
