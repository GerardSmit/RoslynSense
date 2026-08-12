using System.Collections.Immutable;

namespace RoslynMCP.Languages.MsBuild.Core;

/// <summary>
/// Which of the pack's completion sites a caret is in.
/// </summary>
/// <remarks>
/// Element names rather than paths, because a project file's shape is not fixed: a
/// <c>PackageReference</c> is normally in an <c>ItemGroup</c> under <c>Project</c>, but it is just
/// as legal inside a <c>Choose</c>/<c>When</c>, or in a <c>Directory.Build.props</c> whose root is
/// the same but whose nesting is not. Matching on the element that carries the attribute is the
/// question actually being asked.
/// </remarks>
internal static class MsBuildSites
{
    private static readonly ImmutableHashSet<string> PackageElements =
        ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "PackageReference", "PackageVersion", "GlobalPackageReference", "PackageDownload");

    /// <summary>Item types whose <c>Include=</c> is a path, so completing one walks the disk.</summary>
    private static readonly ImmutableHashSet<string> PathElements =
        ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "ProjectReference", "Compile", "Content", "None", "EmbeddedResource",
            "Page", "Resource", "AdditionalFiles", "Folder");

    private static readonly ImmutableHashSet<string> SpecAttributes =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "Include", "Update", "Remove");

    /// <summary>The caret is on a package id — a <c>PackageReference</c>'s <c>Include=</c>, or a
    /// <c>packages.config</c> entry's <c>id=</c>.</summary>
    public static bool IsPackageId(this in MsBuildContext context)
    {
        if (!context.Is(MsBuildLocationFlags.Attribute | MsBuildLocationFlags.Value))
            return false;

        return (PackageElements.Contains(context.ElementName)
                && SpecAttributes.Contains(context.AttributeName ?? string.Empty))
            || (context.ElementName.Equals("package", StringComparison.OrdinalIgnoreCase)
                && context.AttributeName is "id");
    }

    /// <summary>The caret is on a package version.</summary>
    public static bool IsPackageVersion(this in MsBuildContext context)
    {
        if (!context.Is(MsBuildLocationFlags.Attribute | MsBuildLocationFlags.Value))
            return false;

        return (PackageElements.Contains(context.ElementName)
                && context.AttributeName is "Version" or "VersionOverride")
            || (context.ElementName.Equals("package", StringComparison.OrdinalIgnoreCase)
                && context.AttributeName is "version");
    }

    /// <summary>The caret is on an item spec that names a file or directory.</summary>
    public static bool IsPath(this in MsBuildContext context) =>
        context.Is(MsBuildLocationFlags.Attribute | MsBuildLocationFlags.Value)
        && PathElements.Contains(context.ElementName)
        && SpecAttributes.Contains(context.AttributeName ?? string.Empty);

    /// <summary>The caret is on a <c>&lt;Reference Include="…"&gt;</c> — a .NET Framework assembly
    /// name, not a path.</summary>
    public static bool IsAssemblyReference(this in MsBuildContext context) =>
        context.Is(MsBuildLocationFlags.Attribute | MsBuildLocationFlags.Value)
        && context.ElementName.Equals("Reference", StringComparison.OrdinalIgnoreCase)
        && context.AttributeName is "Include";

    /// <summary>The caret is in the text of a property element — <c>&lt;LangVersion&gt;|&lt;/…&gt;</c>.</summary>
    /// <remarks>
    /// A property is any element directly inside a <c>PropertyGroup</c>; MSBuild has no list of
    /// them, which is the point of the vendored corpus. The path suffix is what identifies one,
    /// because the property's own name is whatever the user typed.
    /// </remarks>
    public static bool IsPropertyValue(this in MsBuildContext context) =>
        context.Is(MsBuildLocationFlags.Element | MsBuildLocationFlags.Value)
        && !context.Is(MsBuildLocationFlags.Whitespace)
        && context.Path.Contains("PropertyGroup/", StringComparison.OrdinalIgnoreCase);

    /// <summary>The caret is where a new element name goes: on whitespace inside a group, or
    /// directly after a <c>&lt;</c>.</summary>
    public static bool IsElementName(this in MsBuildContext context) =>
        context.Is(MsBuildLocationFlags.Whitespace)
        || context.Is(MsBuildLocationFlags.Element | MsBuildLocationFlags.Name);

    /// <summary>The group a new element would be declared in, or null when the caret is not in one.</summary>
    public static string? GroupOf(this in MsBuildContext context)
    {
        if (context.ElementName.Equals("PropertyGroup", StringComparison.OrdinalIgnoreCase))
            return "PropertyGroup";

        if (context.ElementName.Equals("ItemGroup", StringComparison.OrdinalIgnoreCase))
            return "ItemGroup";

        return null;
    }
}
