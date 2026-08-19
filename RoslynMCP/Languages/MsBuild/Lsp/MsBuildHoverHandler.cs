using System.Text;
using RoslynMCP.Languages.MsBuild.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.MsBuild.Lsp;

/// <summary>
/// What the name under the cursor means.
/// </summary>
/// <remarks>
/// Everything here is already in memory — the vendored corpora, a package status the diagnostics
/// pass has already fetched, or the analyzers of a solution that is already open. Hover is a
/// gesture that could afford to wait, but there is nothing worth waiting for: the same fetch the
/// squiggle needs is already running, and answering from it keeps the two consistent rather than
/// briefly disagreeing. Nothing here opens a solution to answer.
/// </remarks>
internal static class MsBuildHoverHandler
{
    public static async Task<Hover?> ComputeAsync(TextDocumentPositionParams p, CancellationToken ct)
    {
        string path = LspConverters.UriToPath(p.TextDocument.Uri);

        if (MsBuildDocumentCache.Get(path) is not { } document)
            return null;

        int offset = LspConverters.ToOffset(document.Text, p.Position);
        var context = MsBuildContextResolver.Resolve(document, offset);

        // A suppression list answers about the code under the caret, not about the whole value,
        // so it carries its own span and is asked first.
        if (MsBuildWarningList.IsWarningList(context)
            && MsBuildWarningList.CodeAt(document.Text, context.ReplaceSpan, offset) is { } entry)
        {
            if (DiagnosticCodeCatalog.Lookup(entry.Code, WorkspaceService.TryGetLoadedProject(path)) is not { } info)
                return null;

            // Counted only for a property. Metadata on one reference cannot be lifted from here,
            // and a count taken with it still applied would report zero for a suppression doing its
            // job — see MsBuildWarningList.IsProperty.
            var occurrences = MsBuildWarningList.IsProperty(context)
                ? await WarningOccurrenceCache.GetAsync(path, entry.Code, CountWait, ct)
                : null;

            return new Hover(
                new MarkupContent("markdown", Describe(info, occurrences)),
                LspConverters.ToRange(document.Text.Lines, entry.Span));
        }

        string? markdown = Describe(document, context);
        if (markdown is null)
            return null;

        return new Hover(
            new MarkupContent("markdown", markdown),
            LspConverters.ToRange(document.Text.Lines, context.ReplaceSpan));
    }

    /// <summary>
    /// How long a hover waits for a count before answering without one.
    /// </summary>
    /// <remarks>
    /// Long enough for a warm compilation of one project, short enough that the popup still feels
    /// like a popup. Past it the count keeps going in the background and the next hover has it.
    /// </remarks>
    private static readonly TimeSpan CountWait = TimeSpan.FromMilliseconds(1200);

    /// <summary>
    /// A suppressed diagnostic code, as much as is known about it.
    /// </summary>
    /// <remarks>
    /// The code itself is always shown, even when nothing else is: a hover that appears and says
    /// only <c>NU9999 — no description available</c> still confirms the reader is on the token they
    /// think they are, and the documentation link below it is the answer for a code minted after
    /// this build shipped.
    /// </remarks>
    private static string Describe(in DiagnosticCodeInfo info, WarningOccurrences? occurrences = null)
    {
        var builder = new StringBuilder();
        builder.Append("**").Append(info.Code).Append("**");

        if (info.Severity is { } severity)
            builder.Append(" — ").Append(severity.ToString().ToLowerInvariant());
        if (info.Category is { Length: > 0 } category)
            builder.Append(" (").Append(category).Append(')');

        builder.AppendLine().AppendLine();

        if (info.Title is { Length: > 0 } title)
            builder.AppendLine(title).AppendLine();
        if (info.Description is { Length: > 0 } description)
            builder.AppendLine(description).AppendLine();

        // Quoted, because it is the text the build prints rather than prose about it — and it
        // still carries the holes ('packageId', {0}) that say which parts vary.
        if (info.Message is { Length: > 0 } message)
            builder.Append("> ").AppendLine(message).AppendLine();

        if (Occurrences(occurrences) is { } counted)
            builder.AppendLine(counted).AppendLine();

        if (info.HelpLink is { Length: > 0 } link)
            builder.Append("[Documentation](").Append(link).Append(')').AppendLine();

        return builder.ToString();
    }

    /// <summary>
    /// What the suppression is hiding, when that has been counted.
    /// </summary>
    /// <remarks>
    /// The zero is the line worth reading: a suppression with nothing left to suppress is one that
    /// can go, and nothing else in the editor will ever say so. The scope is spelled out because
    /// the same entry means different things in a <c>.csproj</c> and in a
    /// <c>Directory.Build.props</c>, and a bare number would read as the first in both.
    /// </remarks>
    private static string? Occurrences(WarningOccurrences? occurrences)
    {
        if (occurrences is not { } found)
            return null;

        string scope = found.Projects == 1 ? "this project" : $"{found.Projects} projects";
        string counted = found.Partial ? $" (of {found.Scope} in scope)" : "";

        return found.Count == 0
            ? $"Not reported in {scope}{counted} — the suppression may no longer be needed."
            : $"Suppressing {found.Count} occurrence{(found.Count == 1 ? "" : "s")} in {scope}{counted}.";
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
