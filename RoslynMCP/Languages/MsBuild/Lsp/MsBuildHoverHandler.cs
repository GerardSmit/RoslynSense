using System.Text;
using RoslynMCP.Languages.MsBuild.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.MsBuild.Lsp;

/// <summary>
/// What the name under the cursor means.
/// </summary>
/// <remarks>
/// Everything here is already in memory — the vendored corpus, or a package status the diagnostics
/// pass has already fetched. Hover is a gesture that could afford to wait, but there is nothing
/// worth waiting for: the same fetch the squiggle needs is already running, and answering from it
/// keeps the two consistent rather than briefly disagreeing.
/// </remarks>
internal static class MsBuildHoverHandler
{
    public static Hover? Compute(TextDocumentPositionParams p)
    {
        string path = LspConverters.UriToPath(p.TextDocument.Uri);

        if (MsBuildDocumentCache.Get(path) is not { } document)
            return null;

        int offset = LspConverters.ToOffset(document.Text, p.Position);
        var context = MsBuildContextResolver.Resolve(document, offset);

        string? markdown = Describe(document, context);
        if (markdown is null)
            return null;

        return new Hover(
            new MarkupContent("markdown", markdown),
            LspConverters.ToRange(document.Text.Lines, context.ReplaceSpan));
    }

    private static string? Describe(MsBuildDocument document, MsBuildContext context)
    {
        // A package id, wherever it is written.
        if (context.IsPackageId() && context.Attribute is { } attribute)
            return Package(XmlSpans.Decode(attribute.Value), Version(context));

        if (context.IsPackageVersion())
            return Package(context.Sibling("Include") ?? context.Sibling("Update") ?? "", Version(context));

        // A property, hovered on its name or in its value.
        if (context.Is(MsBuildLocationFlags.Element)
            && context.Path.Contains("PropertyGroup/", StringComparison.OrdinalIgnoreCase))
        {
            return Property(context.ElementName, document.Flavour);
        }

        if (context.Attribute is { } onAttribute)
            return MsBuildSchemaHelp.Element(context.ElementName, onAttribute.Name)?.Description;

        return MsBuildSchemaHelp.Element(context.ElementName)?.Description
            ?? MsBuildSchemaHelp.Item(context.ElementName)?.Description;
    }

    private static string? Version(in MsBuildContext context) =>
        context.IsPackageVersion() && context.Attribute is { } attribute
            ? XmlSpans.Decode(attribute.Value)
            : context.Sibling("Version") ?? context.Sibling("VersionOverride");

    /// <summary>
    /// What is known about a package, and nothing that is not.
    /// </summary>
    /// <remarks>
    /// Silent when the status has not been fetched. A hover saying "checking…" would be a promise
    /// the protocol cannot keep — there is no way to update it once it has been shown.
    /// </remarks>
    private static string? Package(string id, string? version)
    {
        if (id.Length == 0)
            return null;

        var builder = new StringBuilder();
        builder.Append("**").Append(id).Append("**");

        if (version is { Length: > 0 })
            builder.Append(' ').Append(version);

        if (version is { Length: > 0 } && PackageStatusCache.TryGet(id, version) is { FeedsHealthy: true } status)
        {
            builder.AppendLine().AppendLine();

            if (!status.Exists)
            {
                builder.AppendLine("No feed publishes this version.");
            }
            else if (status.Versions.Length > 0)
            {
                builder.Append("Latest: ").Append(status.Versions.Max()!.ToString()).AppendLine();
            }

            foreach (var vulnerability in status.Vulnerabilities)
            {
                builder.AppendLine();
                builder.Append("⚠ Known vulnerability");
                if (vulnerability.AdvisoryUrl is { Length: > 0 } url)
                    builder.Append(" — [advisory](").Append(url).Append(')');
                builder.AppendLine();
            }

            if (status.Deprecation is { } deprecation)
            {
                builder.AppendLine();
                builder.Append("⚠ Deprecated");
                if (deprecation.AlternatePackageId is { Length: > 0 } alternative)
                    builder.Append(" — use ").Append(alternative);
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    private static string? Property(string name, MsBuildFlavour flavour)
    {
        var builder = new StringBuilder();
        builder.Append("**").Append(name).Append('*').Append('*');

        if (MsBuildSchemaHelp.Property(name) is { Description.Length: > 0 } entry)
            builder.AppendLine().AppendLine().AppendLine(entry.Description);

        var values = MsBuildWellKnownValues.For(name, flavour);
        if (!values.IsEmpty)
        {
            builder.AppendLine();
            builder.Append("Values: ").AppendLine(string.Join(", ", values.Select(v => $"`{v.Value}`")));
        }

        // Just the bolded name and nothing else is not worth a popup.
        return builder.Length > name.Length + 4 ? builder.ToString() : null;
    }
}
