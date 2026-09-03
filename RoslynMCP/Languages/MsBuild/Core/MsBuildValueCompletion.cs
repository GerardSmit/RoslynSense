using System.Collections.Concurrent;
using RoslynMCP.Services.ProjectModel;

namespace RoslynMCP.Languages.MsBuild.Core;

/// <summary>
/// The values a caret position accepts, for every site that does not need a feed.
/// </summary>
/// <remarks>
/// Everything here answers from memory or from one directory listing, which is what lets the
/// completion handler keep the NuGet arms isolated: a feed being slow costs the package list and
/// nothing else.
/// </remarks>
internal static class MsBuildValueCompletion
{
    public static IReadOnlyList<MsBuildValue> For(MsBuildDocument document, MsBuildContext context)
    {
        if (context.IsPropertyValue())
            return MsBuildWellKnownValues.For(context.ElementName, document.Flavour);

        if (context.IsAssemblyReference())
            return FrameworkReferenceCatalog.For(document.FilePath);

        if (context.IsPath())
            return MsBuildPathCompletion.For(document, context);

        return [];
    }
}

/// <summary>
/// The .NET Framework assemblies a <c>&lt;Reference Include="…"&gt;</c> can name.
/// </summary>
/// <remarks>
/// <para>
/// Wraps the enumeration <see cref="ProjectMutationService.AvailableAssemblyReferences"/> already
/// does for the <c>add_assembly_reference</c> tool, so the editor and the AI session offer the same
/// list. Memoized per project because it walks a directory of several hundred files, and completion
/// runs on a keystroke.
/// </para>
/// <para>
/// The curated head is there for the machine with no targeting packs installed, where the walk finds
/// nothing at all. Those seven are the assemblies a legacy project references without thinking about
/// it, and offering them from memory beats offering an empty list.
/// </para>
/// </remarks>
internal static class FrameworkReferenceCatalog
{
    private static readonly ConcurrentDictionary<string, IReadOnlyList<MsBuildValue>> s_cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] Common =
    [
        "System", "System.Core", "System.Data", "System.Xml", "System.Xml.Linq",
        "System.Web", "System.Configuration", "System.Drawing", "Microsoft.CSharp",
    ];

    public static IReadOnlyList<MsBuildValue> For(string projectPath) =>
        s_cache.GetOrAdd(projectPath, static path =>
        {
            var found = new List<string>();

            try
            {
                found.AddRange(ProjectMutationService.AvailableAssemblyReferences(path));
            }
            catch (IOException)
            {
                // No targeting packs, or a directory we cannot read. The curated head still answers.
            }
            catch (UnauthorizedAccessException)
            {
            }

            var ordered = new List<MsBuildValue>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // The everyday ones first, then whatever else is installed, so the list opens on the
            // assembly the user almost certainly wants.
            foreach (string name in Common.Concat(found.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)))
            {
                if (seen.Add(name))
                    ordered.Add(new MsBuildValue(name));
            }

            return ordered;
        });

    internal static void Clear() => s_cache.Clear();
}
