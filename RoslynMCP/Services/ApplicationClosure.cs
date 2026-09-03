using Microsoft.CodeAnalysis;

namespace RoslynMCP.Services;

/// <summary>
/// The projects whose code runs inside one application: the anchor, what it references, and what
/// those reference — through project references <em>and</em> through assembly references that
/// resolve back to a project in the same solution.
/// </summary>
/// <remarks>
/// <para>
/// The second half is what <see cref="Project.ProjectReferences"/> alone misses, and it is the
/// normal shape of a legacy solution: a web application references <c>Auth</c> and <c>Logging</c>
/// by their build output — <c>&lt;Reference Include="Auth"&gt;</c> with a <c>HintPath</c> into
/// <c>bin</c> — rather than by <c>&lt;ProjectReference&gt;</c>. Roslyn reports those as metadata,
/// so a walk over project references stops at the application itself and every question answered
/// over the closure — which settings the app reads, which keys are dead — answers as if the
/// libraries were not there. Their source is in the solution; only the edge to it was missing.
/// </para>
/// <para>
/// Matched on assembly name, which is what a reference actually names: a <c>HintPath</c> points at
/// one configuration's output directory and is stale as often as not, while
/// <see cref="Project.AssemblyName"/> is what both sides agree on. A reference that matches no
/// project is a real third-party assembly and is left alone — there is no source to read.
/// </para>
/// <para>
/// Direction matters and is deliberately one-way: this walks what the application <em>uses</em>.
/// A search for the consumers of a shared project walks the other way and belongs to
/// <see cref="SearchScopeService"/>, which loads projects to answer it. Nothing here loads
/// anything; a project the workspace has not opened is not in the solution and is not found.
/// </para>
/// </remarks>
internal static class ApplicationClosure
{
    public static IEnumerable<Project> Of(Project project)
    {
        var byAssemblyName = AssemblyNameLookup(project.Solution);

        var seen = new HashSet<ProjectId> { project.Id };
        var queue = new Queue<Project>();
        queue.Enqueue(project);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            yield return current;

            foreach (var reference in current.ProjectReferences)
            {
                if (seen.Add(reference.ProjectId)
                    && current.Solution.GetProject(reference.ProjectId) is { } referenced)
                {
                    queue.Enqueue(referenced);
                }
            }

            foreach (var reference in current.MetadataReferences)
            {
                if (AssemblyNameOf(reference) is not { Length: > 0 } name
                    || !byAssemblyName.TryGetValue(name, out var id)
                    || !seen.Add(id)
                    || current.Solution.GetProject(id) is not { } sibling)
                {
                    continue;
                }

                queue.Enqueue(sibling);
            }
        }
    }

    /// <summary>
    /// The assembly a reference names, from its path. <see cref="MetadataReference.Display"/> is
    /// the file path for a reference that came from MSBuild, and the file name without its
    /// extension is the assembly name for every reference an ordinary build produces.
    /// </summary>
    private static string? AssemblyNameOf(MetadataReference reference)
    {
        string? display = reference switch
        {
            PortableExecutableReference { FilePath: { Length: > 0 } path } => path,
            _ => reference.Display,
        };

        if (display is not { Length: > 0 })
            return null;

        try
        {
            return Path.GetFileNameWithoutExtension(display);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// One project per assembly name. Ambiguity is resolved by dropping both: two projects
    /// building the same assembly name is a solution that cannot say which one a reference means,
    /// and guessing would attribute one library's reads to the other.
    /// </summary>
    private static Dictionary<string, ProjectId> AssemblyNameLookup(Solution solution)
    {
        var lookup = new Dictionary<string, ProjectId>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in solution.Projects)
        {
            if (project.AssemblyName is not { Length: > 0 } name)
                continue;

            if (!lookup.TryAdd(name, project.Id))
                ambiguous.Add(name);
        }

        foreach (string name in ambiguous)
            lookup.Remove(name);

        return lookup;
    }
}
