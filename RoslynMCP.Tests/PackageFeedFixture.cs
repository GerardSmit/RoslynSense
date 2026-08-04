using System.IO.Compression;
using System.Text;

namespace RoslynMCP.Tests;

/// <summary>
/// A directory-based NuGet feed built on disk, with real .nupkg files.
/// </summary>
/// <remarks>
/// Writing the zip directly rather than shelling `dotnet pack` keeps the suite off the network and
/// makes it possible to author the awkward cases on purpose: an embedded icon, an embedded README,
/// both license forms, and lib folders whose target frameworks decide compatibility.
/// </remarks>
internal sealed class PackageFeedFixture : IDisposable
{
    /// <summary>A 1x1 transparent PNG — real image bytes, so an icon reader has something to read.</summary>
    public static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    public string Directory { get; }

    public PackageFeedFixture()
    {
        Directory = Path.Combine(Path.GetTempPath(), $"feed-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(Directory);
    }

    public sealed record PackageSpec(
        string Id,
        string Version,
        bool WithIcon = false,
        string? Readme = null,
        string? LicenseExpression = null,
        string? LicenseFileText = null,
        IReadOnlyList<string>? LibFrameworks = null,
        IReadOnlyList<(string TargetFramework, string DependencyId, string Range)>? Dependencies = null);

    public string Add(PackageSpec spec)
    {
        string path = Path.Combine(Directory, $"{spec.Id}.{spec.Version}.nupkg");

        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        Write(archive, $"{spec.Id}.nuspec", Nuspec(spec));

        if (spec.WithIcon)
        {
            using var entry = archive.CreateEntry("icon.png").Open();
            entry.Write(TinyPng);
        }

        if (spec.Readme is { Length: > 0 })
            Write(archive, "readme.md", spec.Readme);

        if (spec.LicenseFileText is { Length: > 0 })
            Write(archive, "LICENSE.txt", spec.LicenseFileText);

        foreach (string framework in spec.LibFrameworks ?? [])
            Write(archive, $"lib/{framework}/{spec.Id}.dll", "not a real assembly");

        return path;
    }

    /// <summary>Adds several versions of one package, which is what the update tests need.</summary>
    public void AddVersions(string id, params string[] versions)
    {
        foreach (string version in versions)
            Add(new PackageSpec(id, version));
    }

    private static string Nuspec(PackageSpec spec)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""<?xml version="1.0" encoding="utf-8"?>""");
        sb.AppendLine("""<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">""");
        sb.AppendLine("  <metadata>");
        sb.AppendLine($"    <id>{spec.Id}</id>");
        sb.AppendLine($"    <version>{spec.Version}</version>");
        sb.AppendLine("    <authors>RoslynSense</authors>");
        sb.AppendLine("    <description>Test package</description>");

        if (spec.WithIcon)
            sb.AppendLine("    <icon>icon.png</icon>");
        if (spec.Readme is { Length: > 0 })
            sb.AppendLine("    <readme>readme.md</readme>");
        if (spec.LicenseExpression is { Length: > 0 })
            sb.AppendLine($"""    <license type="expression">{spec.LicenseExpression}</license>""");
        else if (spec.LicenseFileText is { Length: > 0 })
            sb.AppendLine("""    <license type="file">LICENSE.txt</license>""");

        if (spec.Dependencies is { Count: > 0 })
        {
            sb.AppendLine("    <dependencies>");
            foreach (var group in spec.Dependencies.GroupBy(d => d.TargetFramework))
            {
                sb.AppendLine($"""      <group targetFramework="{group.Key}">""");
                foreach (var dependency in group)
                    sb.AppendLine($"""        <dependency id="{dependency.DependencyId}" version="{dependency.Range}" />""");
                sb.AppendLine("      </group>");
            }
            sb.AppendLine("    </dependencies>");
        }

        sb.AppendLine("  </metadata>");
        sb.AppendLine("</package>");
        return sb.ToString();
    }

    private static void Write(ZipArchive archive, string entryName, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(entryName).Open());
        writer.Write(content);
    }

    public void Dispose()
    {
        try { System.IO.Directory.Delete(Directory, recursive: true); } catch { }
    }
}
