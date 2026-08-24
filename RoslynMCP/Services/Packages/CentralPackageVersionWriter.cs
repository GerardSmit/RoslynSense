using Microsoft.Language.Xml;

namespace RoslynMCP.Services.Packages;

/// <summary>
/// Edits a <c>Directory.Packages.props</c> in place.
/// </summary>
/// <remarks>
/// `dotnet add package` already writes the central version correctly, so this exists only for the
/// batch path, where running the CLI once per project would mean one restore per package. The edit
/// is deliberately surgical: only the one <c>Version</c> attribute changes, so the file's
/// formatting, comments and ordering survive a fifty-package update untouched.
/// </remarks>
public static class CentralPackageVersionWriter
{
    /// <summary>
    /// Sets a package's central version. Returns false when the file does not declare it — the
    /// caller then falls back to the CLI, which knows how to add a new entry.
    /// </summary>
    public static bool TrySetVersion(string propsPath, string packageId, string version)
    {
        try
        {
            if (!File.Exists(propsPath))
                return false;

            string original = File.ReadAllText(propsPath);
            var document = Parser.ParseText(original);

            if (Declaration(document, packageId) is not { } element)
                return false;

            if (element.GetAttributeValue("Version") == version)
                return true;

            // Every character of the source is in the tree, so writing the tree back out is the
            // original file with one attribute value different — there is no formatting pass to
            // preserve anything through, and nothing to tell not to reindent.
            File.WriteAllText(
                propsPath,
                document.ReplaceNode(element, element.SetAttribute("Version", version)).ToFullString());

            return true;
        }
        catch (Exception ex)
        {
            ServiceLog.Warn(
                $"Could not update '{Path.GetFileName(propsPath)}': {ex.Message}",
                key: $"cpm-write:{propsPath}");
            return false;
        }
    }

    /// <summary>
    /// The element declaring a package's central version, by either <c>Include</c> or
    /// <c>Update</c> and whatever the file's element names are prefixed with.
    /// </summary>
    private static XmlElementBaseSyntax? Declaration(XmlDocumentSyntax document, string packageId)
    {
        foreach (var element in document.Descendants())
        {
            if (element.NameNode?.LocalName is "PackageVersion" or "GlobalPackageReference" &&
                (Matches(element.GetAttributeValue("Include"), packageId) ||
                    Matches(element.GetAttributeValue("Update"), packageId)))
            {
                return element;
            }
        }

        return null;
    }

    /// <summary>
    /// The nearest <c>Directory.Packages.props</c> above a project. Only a fallback: an evaluated
    /// package reference already names the file its version came from, which is correct even when
    /// the import chain is unusual.
    /// </summary>
    public static string? FindNearest(string projectPath)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? "");

        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "Directory.Packages.props");
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        return null;
    }

    private static bool Matches(string? value, string packageId) =>
        value is not null && value.Equals(packageId, StringComparison.OrdinalIgnoreCase);
}
