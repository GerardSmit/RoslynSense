using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Language.Xml;
using TextSpan = Microsoft.CodeAnalysis.Text.TextSpan;

namespace RoslynMCP.Languages.MsBuild.Core;

/// <summary>One package reference as it is written in a buffer.</summary>
/// <param name="Id">The package, decoded.</param>
/// <param name="Version">The version as written, or null when this file does not carry one.</param>
/// <param name="IdSpan">Where the id is, for a squiggle that has no version to point at.</param>
/// <param name="VersionSpan">Where the version is, and what a quick fix replaces. Empty when the
/// version lives in another file.</param>
/// <param name="VersionAttribute">The attribute the version came from, for building an edit.</param>
internal readonly record struct MsBuildPackageRef(
    string Id,
    string? Version,
    TextSpan IdSpan,
    TextSpan VersionSpan,
    XmlAttributeSyntax? VersionAttribute);

/// <summary>
/// The package references a project file declares, read from the buffer rather than an evaluation.
/// </summary>
/// <remarks>
/// From the buffer on purpose. An MSBuild evaluation is cached against file timestamps, so an
/// unsaved edit keeps serving the version that was last saved — and every span derived from it
/// would point into text that is no longer there. The evaluation answers a different question
/// (which reference is centrally managed, and where its version lives) that is stable across
/// editing a version value.
/// </remarks>
internal static class MsBuildPackageReader
{
    private static readonly ImmutableHashSet<string> Elements =
        ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "PackageReference", "PackageVersion", "GlobalPackageReference");

    public static ImmutableArray<MsBuildPackageRef> Read(MsBuildDocument document) =>
        document.Kind switch
        {
            MsBuildFileKind.PackagesConfig => ReadPackagesConfig(document),
            _ when MsBuildFile.IsMsBuild(document.Kind) => ReadMsBuild(document),
            _ => [],
        };

    private static ImmutableArray<MsBuildPackageRef> ReadMsBuild(MsBuildDocument document)
    {
        var results = ImmutableArray.CreateBuilder<MsBuildPackageRef>();

        foreach (var element in document.Root.DescendantNodes().OfType<XmlElementBaseSyntax>())
        {
            if (!Elements.Contains(element.Name))
                continue;

            XmlAttributeSyntax? id = null;
            XmlAttributeSyntax? version = null;

            foreach (var attribute in element.Attributes)
            {
                switch (attribute.Name)
                {
                    case "Include" or "Update":
                        id ??= attribute;
                        break;

                    // An override wins over the central version, and is the one written here.
                    case "VersionOverride":
                        version = attribute;
                        break;

                    case "Version":
                        version ??= attribute;
                        break;
                }
            }

            if (id is null || id.Value is not { Length: > 0 } packageId)
                continue;

            results.Add(new MsBuildPackageRef(
                packageId,
                version is null ? null : version.Value,
                id.ValueSpan.ToRoslynSpan(),
                version?.ValueSpan.ToRoslynSpan() ?? default,
                version));
        }

        return results.ToImmutable();
    }

    private static ImmutableArray<MsBuildPackageRef> ReadPackagesConfig(MsBuildDocument document)
    {
        var results = ImmutableArray.CreateBuilder<MsBuildPackageRef>();

        foreach (var element in document.Root.DescendantNodes().OfType<XmlElementBaseSyntax>())
        {
            if (!element.Name.Equals("package", StringComparison.OrdinalIgnoreCase))
                continue;

            XmlAttributeSyntax? id = null;
            XmlAttributeSyntax? version = null;

            foreach (var attribute in element.Attributes)
            {
                if (attribute.Name.Equals("id", StringComparison.OrdinalIgnoreCase))
                    id ??= attribute;
                else if (attribute.Name.Equals("version", StringComparison.OrdinalIgnoreCase))
                    version ??= attribute;
            }

            if (id is null || id.Value is not { Length: > 0 } packageId)
                continue;

            results.Add(new MsBuildPackageRef(
                packageId,
                version is null ? null : version.Value,
                id.ValueSpan.ToRoslynSpan(),
                version?.ValueSpan.ToRoslynSpan() ?? default,
                version));
        }

        return results.ToImmutable();
    }
}
