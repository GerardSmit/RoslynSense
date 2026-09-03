using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.MsBuild.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services.Packages;
using Range = RoslynMCP.Lsp.Protocol.Range;

namespace RoslynMCP.Languages.MsBuild.Lsp;

/// <summary>
/// F12 in a project file.
/// </summary>
/// <remarks>
/// Two things in a project file are genuinely a reference to somewhere else, and both are answered
/// here as real locations rather than as a command that opens a panel. Under central package
/// management a <c>PackageReference</c> carries no version, and the line that decides it is in a
/// <c>Directory.Packages.props</c> further up the tree — which is the question a user pressing F12
/// on it is actually asking. An <c>Import</c> names a file outright.
/// </remarks>
internal static class MsBuildNavigationHandler
{
    public static Location[] Compute(TextDocumentPositionParams p)
    {
        string path = LspConverters.UriToPath(p.TextDocument.Uri);

        if (MsBuildDocumentCache.Get(path) is not { } document)
            return [];

        int offset = LspConverters.ToOffset(document.Text, p.Position);
        var context = MsBuildContextResolver.Resolve(document, offset);

        if (context.IsPackageId() || context.IsPackageVersion())
            return CentralVersion(document, context);

        if (IsImport(context))
            return ImportedFile(document, context);

        return [];
    }

    private static bool IsImport(in MsBuildContext context) =>
        context.Is(MsBuildLocationFlags.Attribute | MsBuildLocationFlags.Value)
        && context.ElementName.Equals("Import", StringComparison.OrdinalIgnoreCase)
        && context.AttributeName is "Project";

    /// <summary>The <c>&lt;PackageVersion&gt;</c> that decides this reference's version.</summary>
    private static Location[] CentralVersion(MsBuildDocument document, in MsBuildContext context)
    {
        string? id = context.IsPackageId() && context.Attribute is { } attribute
            ? attribute.Value
            : context.Sibling("Include") ?? context.Sibling("Update");

        if (id is not { Length: > 0 })
            return [];

        // The nearest Directory.Packages.props, by the same walk NuGet does. Nothing to jump to
        // when the version is written right here.
        if (CentralPackageVersionWriter.FindNearest(document.FilePath) is not { Length: > 0 } props
            || string.Equals(props, document.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        if (MsBuildDocumentCache.Get(props) is not { } central)
            return [];

        foreach (var reference in MsBuildPackageReader.Read(central))
        {
            if (!reference.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                continue;

            var span = reference.VersionSpan.Length > 0 ? reference.VersionSpan : reference.IdSpan;
            return [new Location(
                LspConverters.PathToUri(props),
                LspConverters.ToRange(central.Text.Lines, span))];
        }

        return [];
    }

    private static Location[] ImportedFile(MsBuildDocument document, in MsBuildContext context)
    {
        if (context.Attribute is not { } attribute)
            return [];

        string spec = attribute.Value;

        // An import whose path is built from properties — `$(MSBuildThisFileDirectory)build.props`
        // — cannot be resolved without evaluating the project, which this path may not do. Better
        // to answer nothing than to guess at a file.
        if (spec.Length == 0 || spec.Contains("$(", StringComparison.Ordinal))
            return [];

        if (Path.GetDirectoryName(document.FilePath) is not { Length: > 0 } directory)
            return [];

        string resolved;
        try
        {
            resolved = Path.GetFullPath(Path.Combine(directory, spec));
        }
        catch (ArgumentException)
        {
            return [];
        }

        if (!File.Exists(resolved))
            return [];

        // The head of the file: an import names a whole file, not a position in one.
        var start = new Position(0, 0);
        return [new Location(LspConverters.PathToUri(resolved), new Range(start, start))];
    }
}
