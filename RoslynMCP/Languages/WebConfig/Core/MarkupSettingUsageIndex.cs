using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Services;
using WebFormsCore.Nodes;

namespace RoslynMCP.Languages.WebConfig.Core;

/// <summary>
/// Where WebForms markup names a setting: <c>&lt;%$ AppSettings: CdnRoot %&gt;</c> and
/// <c>&lt;%$ ConnectionStrings: Main %&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// Read from the markup parse trees the WebForms pack already builds, not from a text scan of its
/// own. An expression builder looks lexical enough to match with a regular expression, and the two
/// answers differ exactly where it matters: the builder appears in two syntactic positions — an
/// attribute value and element content — the parser tells them apart, and it hands back the
/// argument's real range rather than one reconstructed from a match offset. Sharing the tree also
/// means one parse serves both packs.
/// </para>
/// <para>
/// The site-wide parse is <see cref="ProjectIndexCacheService"/>'s, which is incremental: a saved
/// page re-parses itself and the rest of the index carries over. This index is memoized against
/// that one by reference, so it is rebuilt exactly when the parse behind it moved.
/// </para>
/// </remarks>
internal static class MarkupSettingUsageIndex
{
    private static readonly ConditionalWeakTable<AspxProjectIndex, ImmutableArray<ConfigSettingUsage>[]> s_indexes =
        new();

    /// <summary>
    /// Every markup usage in the project. Empty for a project that hosts no WebForms — the
    /// metadata check that keeps a C#-only solution off the file system.
    /// </summary>
    public static async Task<ImmutableArray<ConfigSettingUsage>> ForProjectAsync(
        Project project, CancellationToken ct)
    {
        if (project.FilePath is null || !await AspxReferenceService.HostsWebFormsAsync(project, ct))
            return [];

        var parsed = await ProjectIndexCacheService.GetAspxIndexAsync(project, ct);

        if (s_indexes.TryGetValue(parsed, out var cached))
            return cached[0];

        var usages = ImmutableArray.CreateBuilder<ConfigSettingUsage>();

        foreach (var file in parsed.Files)
        {
            ct.ThrowIfCancellationRequested();

            if (file.ParseTree is { } root)
                usages.AddRange(Read(root, file.FilePath));
        }

        // Boxed in a single-element array because a ConditionalWeakTable value has to be a
        // reference type, and an ImmutableArray is not one.
        var result = usages.ToImmutable();
        return s_indexes.GetValue(parsed, _ => [result])[0];
    }

    /// <summary>The settings one parsed markup file reads.</summary>
    public static IEnumerable<ConfigSettingUsage> Read(RootNode root, string filePath)
    {
        foreach (var (prefix, argument, _) in AspxResourceService.Builders(root))
        {
            var section = prefix.Value switch
            {
                _ when prefix.Value.Equals("AppSettings", StringComparison.OrdinalIgnoreCase)
                    => WebConfigSection.AppSettings,
                _ when prefix.Value.Equals("ConnectionStrings", StringComparison.OrdinalIgnoreCase)
                    => WebConfigSection.ConnectionStrings,
                _ => (WebConfigSection?)null,
            };

            if (section is not { } named || argument.Value.Length == 0)
                continue;

            // `<%$ ConnectionStrings: Main.ProviderName %>` reads another field of one entry
            // rather than an entry of its own.
            string name = named == WebConfigSection.ConnectionStrings
                && argument.Value.EndsWith(".ProviderName", StringComparison.OrdinalIgnoreCase)
                    ? argument.Value[..^".ProviderName".Length]
                    : argument.Value;

            yield return new ConfigSettingUsage(
                name, named, filePath, AspxSymbolResolver.Span(argument.Range), argument.Range);
        }
    }
}
