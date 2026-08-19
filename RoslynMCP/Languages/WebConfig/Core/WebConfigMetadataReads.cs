using RoslynMCP.Services.MetadataConfiguration;

namespace RoslynMCP.Languages.WebConfig.Core;

/// <summary>
/// The bridge from a config file's sections to the keyspaces a compiled assembly reads.
/// </summary>
/// <remarks>
/// Two names for one thing, kept apart because they answer to different owners: a
/// <see cref="WebConfigSection"/> is a section element in a file, and a
/// <see cref="MetadataConfigurationKind"/> is what a call in someone else's IL was asking for.
/// </remarks>
internal static class WebConfigMetadataReads
{
    public static MetadataConfigurationKind KindOf(WebConfigSection section) =>
        section == WebConfigSection.ConnectionStrings
            ? MetadataConfigurationKind.ConnectionString
            : MetadataConfigurationKind.AppSetting;

    /// <summary>
    /// The names an application reads in one section and the file does not declare, each with
    /// where the read came from. Sorted, because a config file has no order worth preserving and
    /// an alphabetical list can be scanned.
    /// </summary>
    /// <remarks>
    /// External reads are offered on the same footing as the solution's own. A key a package
    /// needs is a key this file has to declare, and it is the one kind of missing key that no
    /// amount of reading the solution will ever suggest.
    /// </remarks>
    public static SortedDictionary<string, string> Wanted(
        WebConfigSection section,
        IEnumerable<WebConfigEntry> declared,
        IEnumerable<ConfigSettingUsage> usages,
        IEnumerable<ConfigSettingUsage> markup,
        IEnumerable<MetadataConfigurationRead> external)
    {
        var present = new HashSet<string>(
            declared.Where(entry => entry.Section == section).Select(entry => entry.Name),
            StringComparer.OrdinalIgnoreCase);

        var wanted = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var usage in usages.Concat(markup))
        {
            if (usage.Section == section && !present.Contains(usage.Name))
                wanted.TryAdd(usage.Name, "read by this solution");
        }

        foreach (var read in external)
        {
            if (read.Kind == KindOf(section) && !present.Contains(read.Name))
                wanted.TryAdd(read.Name, "read by " + read.AssemblyName);
        }

        return wanted;
    }
}
