namespace RoslynMCP.Languages.MsBuild.Core;

/// <summary>
/// The element names that can be written where the caret is.
/// </summary>
/// <remarks>
/// Driven by the vendored corpus rather than a hand-written list, which is most of why the corpus is
/// worth carrying: MSBuild defines several hundred properties and no API enumerates them, so the
/// alternative is a table that starts at fifteen and never catches up.
/// </remarks>
internal static class MsBuildNameCompletion
{
    /// <summary>
    /// The elements a <c>Project</c> can contain directly.
    /// </summary>
    /// <remarks>
    /// Short and fixed, unlike the properties and items inside them — this is the schema rather
    /// than the vocabulary.
    /// </remarks>
    private static readonly MsBuildValue[] TopLevel =
    [
        new("PropertyGroup", "a group of properties"),
        new("ItemGroup", "a group of items"),
        new("Import", "another project file"),
        new("Target", "a build step"),
        new("Choose", "conditional alternatives"),
        new("UsingTask", "a task assembly"),
        new("ProjectExtensions"),
    ];

    public static IReadOnlyList<MsBuildValue> For(MsBuildDocument document, MsBuildContext context)
    {
        if (!context.IsElementName())
            return [];

        return context.GroupOf() switch
        {
            "PropertyGroup" => Properties(),
            "ItemGroup" => Items(),
            _ => context.ElementName.Equals("Project", StringComparison.OrdinalIgnoreCase) ? TopLevel : [],
        };
    }

    /// <summary>
    /// Every property the corpus documents.
    /// </summary>
    /// <remarks>
    /// Underscore-prefixed names are excluded. MSBuild's convention is that a leading underscore
    /// means internal to the targets that define it, and offering a few hundred of them buries the
    /// ones anybody should be setting.
    /// </remarks>
    private static IReadOnlyList<MsBuildValue> Properties()
    {
        var byName = new SortedDictionary<string, MsBuildValue>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, entry) in MsBuildSchemaHelp.Properties)
        {
            if (!name.StartsWith('_'))
                byName[name] = new MsBuildValue(name, null, entry.Description);
        }

        // The corpus predates most of the SDK-style properties, so the ones this pack documents
        // itself are unioned in rather than assumed present.
        foreach (string name in MsBuildWellKnownValues.Additional)
        {
            if (!byName.ContainsKey(name))
                byName[name] = new MsBuildValue(name);
        }

        return [.. byName.Values];
    }

    /// <inheritdoc cref="Properties"/>
    private static IReadOnlyList<MsBuildValue> Items() =>
    [
        .. MsBuildSchemaHelp.Items
            .Where(entry => entry.Key != "*" && !entry.Key.StartsWith('_'))
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new MsBuildValue(entry.Key, null, entry.Value.Description)),
    ];
}
