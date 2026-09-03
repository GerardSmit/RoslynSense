using System.Collections.Immutable;
using Microsoft.Language.Xml;
using Microsoft.CodeAnalysis;

namespace RoslynMCP.Services;

/// <summary>
/// Re-binds project references that MSBuild evaluation lost because the referenced output was
/// never built, pointing them at the projects the workspace already holds.
/// </summary>
/// <remarks>
/// <para>
/// In an unbuilt checkout, a <c>&lt;ProjectReference&gt;</c> whose target assembly does not
/// exist on disk can come out of evaluation as nothing at all: the reference-resolution targets
/// drop it, the loader logs "found project reference without a matching metadata reference",
/// and every type from that project becomes CS0246 — even though the referenced project itself
/// is loaded and navigable in the same workspace. A plain <c>&lt;Reference&gt;</c> with a
/// <c>HintPath</c> into a sibling project's <c>bin</c> fails the same way, one step earlier.
/// The observed case: a legacy WebForms site referencing two VB projects that ship no binaries
/// in source control — every symbol from them red, while F12 lands in their source happily.
/// </para>
/// <para>
/// The heal reads the intent straight from each project file — the two reference item shapes —
/// and adds a real <see cref="ProjectReference"/> wherever the intent names a project the
/// workspace has loaded and the compilation cannot currently see. An intent naming a project
/// the workspace does not hold is returned to the caller to load, and the pass repeats — that
/// is how a dropped reference's target, which by definition was never chased into the
/// workspace, gets there at all. It deliberately touches nothing else: an intent whose assembly
/// exists on disk is left to the normal machinery (evaluation resolved it, or
/// <c>UpdateReferencesAfterAdd</c> will rewire it), and an add that would create a cycle is
/// skipped. Everything it adds is what Visual Studio would have shown for the same solution,
/// which is the standard the workspace is held to everywhere else.
/// </para>
/// <para>
/// Runs after every project add, over the whole solution, because a reference can only heal
/// once its target has arrived — a batch may add the referrer shards before the target's. It is
/// idempotent: an intent already satisfied is a no-op, so repeat passes cost one XML parse per
/// project and nothing more.
/// </para>
/// </remarks>
internal static class ProjectReferenceHealer
{
    /// <summary>
    /// Adds the missing references described above, and returns the project files the intents
    /// name that the workspace does not hold yet — the caller loads those and heals again,
    /// because a reference can only bind once its target exists. Call under the entry's load
    /// gate.
    /// </summary>
    public static List<string> Heal(Workspace workspace)
    {
        try
        {
            var (healed, missing) = HealCore(workspace);
            if (healed.Count > 0)
                Console.Error.WriteLine(
                    $"[WorkspaceService] Healed {healed.Count} project reference(s) that " +
                    "evaluation dropped over unbuilt output assemblies: " +
                    string.Join(", ", healed.Take(8)) + (healed.Count > 8 ? ", …" : "") + ".");
            return missing;
        }
        catch (Exception ex)
        {
            // Best effort on top of an already-loaded solution: a malformed project file or a
            // race with an eviction must not take down the load that just succeeded.
            ServiceLog.Warn($"Project reference healing failed: {ex.Message}", key: "ref-heal");
            return [];
        }
    }

    private static (List<string> Healed, List<string> Missing) HealCore(Workspace workspace)
    {
        var solution = workspace.CurrentSolution;

        var byPath = new Dictionary<string, ProjectId>(StringComparer.OrdinalIgnoreCase);
        var byOutput = new Dictionary<string, ProjectId>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in solution.Projects)
        {
            if (project.FilePath is { Length: > 0 } file)
                byPath.TryAdd(Path.GetFullPath(file), project.Id);
            if (project.OutputFilePath is { Length: > 0 } output)
                byOutput.TryAdd(Path.GetFullPath(output), project.Id);
            if (project.OutputRefFilePath is { Length: > 0 } refOutput)
                byOutput.TryAdd(Path.GetFullPath(refOutput), project.Id);
        }

        var healed = new List<string>();
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in solution.Projects)
        {
            if (project.FilePath is not { Length: > 0 } projectFile)
                continue;

            var (projectRefs, hintedAssemblies) = DeclaredReferences(projectFile);
            if (projectRefs.Count == 0 && hintedAssemblies.Count == 0)
                continue;

            List<ProjectId>? targets = null;
            foreach (string target in projectRefs)
            {
                if (byPath.TryGetValue(target, out var id))
                    (targets ??= []).Add(id);
                else if (File.Exists(target))
                    missing.Add(target);
            }

            foreach (string assembly in hintedAssemblies)
            {
                // An assembly that exists took the ordinary path — RAR resolved it, and if it is
                // also a loaded project's output, UpdateReferencesAfterAdd rewires it. Only a
                // hint pointing at a loaded project's *unbuilt* output needs help.
                if (File.Exists(assembly))
                    continue;

                if (byOutput.TryGetValue(assembly, out var id))
                    (targets ??= []).Add(id);
                else if (ProjectFileProducing(assembly) is { } candidate
                    && !byPath.ContainsKey(candidate))
                {
                    // The hinted assembly does not exist and no loaded project claims to produce
                    // it — but a project file of the same name sits beside the hinted path. Load
                    // it and let the next pass decide: it binds only if that project's real
                    // output path is the hinted one.
                    missing.Add(candidate);
                }
            }

            if (targets is null)
                continue;

            // Re-read per referrer: earlier heals in this same pass may have added references
            // (and cycles form through them), so both the duplicate check and the cycle check
            // have to see the solution as it is now.
            solution = workspace.CurrentSolution;
            var current = solution.GetProject(project.Id);
            if (current is null)
                continue;

            var existing = current.ProjectReferences.Select(r => r.ProjectId).ToHashSet();
            var seesAlready = current.MetadataReferences
                .OfType<PortableExecutableReference>()
                .Select(r => r.FilePath)
                .OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var target in targets.Distinct())
            {
                if (target == project.Id || existing.Contains(target))
                    continue;

                // Already visible through a built DLL: the compilation is whole, and adding a
                // project reference on top would double-define every type in it.
                var targetProject = solution.GetProject(target);
                if (targetProject?.OutputFilePath is { Length: > 0 } dll
                    && seesAlready.Contains(Path.GetFullPath(dll)))
                {
                    continue;
                }

                if (solution.GetProjectDependencyGraph()
                    .GetProjectsThatThisProjectTransitivelyDependsOn(target)
                    .Contains(project.Id))
                {
                    continue;
                }

                workspace.OnProjectReferenceAdded(project.Id, new ProjectReference(target));
                solution = workspace.CurrentSolution;
                existing.Add(target);
                healed.Add($"{project.Name} -> {targetProject?.Name}");
            }
        }

        return (healed, [.. missing]);
    }

    /// <summary>
    /// The project file that plausibly produces <paramref name="assemblyPath"/>: a
    /// <c>{assembly-name}.csproj/.vbproj/.fsproj</c> in one of the few directories above the
    /// hinted location (a hint points into <c>bin</c> or <c>bin\Configuration</c>, so the
    /// project file sits one or two levels up). Null when nothing matches — a hint into a
    /// packages folder or an SDK directory names no sibling project and stays untouched.
    /// </summary>
    private static string? ProjectFileProducing(string assemblyPath)
    {
        string name = Path.GetFileNameWithoutExtension(assemblyPath);
        var dir = new DirectoryInfo(Path.GetDirectoryName(assemblyPath) ?? "");

        for (int up = 0; up < 3 && dir is not null; up++, dir = dir.Parent)
        {
            foreach (string extension in (string[])[".csproj", ".vbproj", ".fsproj"])
            {
                string candidate = Path.Combine(dir.FullName, name + extension);
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    /// <summary>
    /// The reference intents declared in <paramref name="projectFile"/>: full paths of
    /// <c>&lt;ProjectReference&gt;</c> targets, and full paths of <c>&lt;Reference&gt;</c>
    /// <c>HintPath</c> assemblies. Read from the XML, namespace- and condition-blind — an
    /// intent behind a false condition resolves to a target the workspace does not hold, or to
    /// an add the project already has, and either way falls out at the checks above.
    /// </summary>
    private static (List<string> ProjectRefs, List<string> HintedAssemblies) DeclaredReferences(
        string projectFile)
    {
        var projectRefs = new List<string>();
        var hinted = new List<string>();

        string dir = Path.GetDirectoryName(projectFile) ?? "";

        string? Resolve(string? relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || relative.Contains('$'))
                return null;
            try { return Path.GetFullPath(Path.Combine(dir, relative.Trim())); }
            catch (Exception) { return null; }
        }

        try
        {
            var xml = Parser.ParseText(File.ReadAllText(projectFile));

            foreach (var element in xml.Descendants())
            {
                if (element.NameNode?.LocalName == "ProjectReference"
                    && Resolve(element.GetAttributeValue("Include")) is { } target)
                {
                    // ReferenceOutputAssembly="false" declares a build-ordering or analyzer
                    // relationship (source generators use it), not a compilation reference —
                    // healing one would hand the compiler a project it was told not to see.
                    string? refOutput = element.GetAttributeValue("ReferenceOutputAssembly")
                        ?? element.GetElementByLocalName("ReferenceOutputAssembly")?.Value;
                    if (!string.Equals(refOutput?.Trim(), "false", StringComparison.OrdinalIgnoreCase))
                        projectRefs.Add(target);
                }
                else if (element.NameNode?.LocalName == "Reference")
                {
                    var hint = element.GetElementByLocalName("HintPath")?.Value;
                    if (Resolve(hint) is { } assembly)
                        hinted.Add(assembly);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable: nothing to declare is the honest answer. Malformed needs no catch — the
            // parse is error-tolerant, and the references above the damage still count.
        }

        return (projectRefs, hinted);
    }
}
