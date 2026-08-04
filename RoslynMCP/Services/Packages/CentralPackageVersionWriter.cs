using System.Xml.Linq;

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

            // PreserveWhitespace plus DisableFormatting is what keeps the rest of the file
            // byte-identical; without them a single version bump reindents the whole document.
            var document = XDocument.Load(propsPath, LoadOptions.PreserveWhitespace);
            if (document.Root is null)
                return false;

            var element = document.Root
                .Descendants()
                .FirstOrDefault(e =>
                    (e.Name.LocalName is "PackageVersion" or "GlobalPackageReference") &&
                    (Matches(e.Attribute("Include"), packageId) || Matches(e.Attribute("Update"), packageId)));

            if (element is null)
                return false;

            if (element.Attribute("Version") is { } attribute)
            {
                if (attribute.Value == version)
                    return true;
                attribute.Value = version;
            }
            else
            {
                element.SetAttributeValue("Version", version);
            }

            Save(document, propsPath);
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
    /// Writes the document back without adding anything the original did not have. Most
    /// Directory.Packages.props files carry no XML declaration, and inserting one would show up as
    /// a change on every line-ending-sensitive diff for a version bump nobody asked to reformat.
    /// </summary>
    private static void Save(XDocument document, string path)
    {
        var settings = new System.Xml.XmlWriterSettings
        {
            OmitXmlDeclaration = document.Declaration is null,
            Indent = false,
            NewLineHandling = System.Xml.NewLineHandling.None,
            // XmlWriter's default UTF8 encoding emits a preamble. Almost no Directory.Packages.props
            // has a BOM, and adding one turns a version bump into a first-line diff nobody asked for.
            Encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        using var writer = System.Xml.XmlWriter.Create(path, settings);
        document.Save(writer);
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

    private static bool Matches(XAttribute? attribute, string packageId) =>
        attribute is not null &&
        attribute.Value.Equals(packageId, StringComparison.OrdinalIgnoreCase);
}
