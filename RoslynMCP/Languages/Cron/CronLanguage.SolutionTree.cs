using System.Globalization;
using Microsoft.CodeAnalysis;
using RoslynMCP.Languages.Cron.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Services.ProjectModel;

namespace RoslynMCP.Languages.Cron;

/// <summary>
/// The <b>Cron Jobs</b> section of the Solution Explorer: which projects schedule anything, and
/// what each of them runs.
/// </summary>
/// <remarks>
/// <para>
/// The half of the pack that is not about a string at all. A recurring job is registered by an
/// ordinary call in one startup file, so nothing in the tree stands for it, the job's own method
/// looks uncalled, and the question people actually have about a system that schedules work —
/// <i>what runs here, and when?</i> — is answered only by finding the file that happens to hold
/// the registrations.
/// </para>
/// <para>
/// The section is drawn on the root listing, which is bound by the tree's promise to evaluate no
/// project until something is expanded. So the decision to show it at all comes from
/// <see cref="CronProjectProbe"/> — a text scan of the manifests — or from the user having
/// configured a binding, which is the case a package probe would miss: an in-house wrapper lives
/// in a project referencing no scheduler, and hiding the section from the one person who
/// configured it would be the worst answer available.
/// </para>
/// </remarks>
internal sealed partial class CronLanguage : ILanguageSolutionTreeContributor
{
    /// <summary>The section, and the prefix of everything under it.</summary>
    private const string Prefix = "cron:";

    /// <summary>One project inside the section.</summary>
    private const string ProjectPrefix = Prefix + "p|";

    /// <summary>One job.</summary>
    private const string JobPrefix = Prefix + "j|";

    /// <summary>
    /// What wraps a value that is only knowable at run time.
    /// </summary>
    /// <remarks>
    /// Guillemets rather than dimming, and rather than nothing. Dimming already means "the
    /// workspace cannot answer about this" in this tree, so an unloaded project and a
    /// config-driven schedule would look identical; and a bare name would read as a literal
    /// somebody could grep for, which is the exact mistake the mark exists to prevent.
    /// </remarks>
    private const char Open = '⟨';

    private const char Close = '⟩';

    public string NodeIdPrefix => Prefix;

    public Task<SolutionTreeNode?> SectionAsync(string solutionPath, CancellationToken ct)
    {
        if (!Settings.Enabled || Projects(ct).Count == 0)
            return Task.FromResult<SolutionTreeNode?>(null);

        return Task.FromResult<SolutionTreeNode?>(new SolutionTreeNode(
            Id: Prefix + solutionPath,
            Kind: SolutionNodeKind.CronJobs,
            Label: "Cron Jobs",
            Description: null,
            ResourceUri: null,
            HasChildren: true,
            ContextValue: SolutionNodeKind.CronJobs));
    }

    public async Task<SolutionTreeNode[]> ChildrenAsync(
        string nodeId, SolutionTreeParams p, CancellationToken ct)
    {
        if (!Settings.Enabled)
            return [];

        return nodeId.StartsWith(ProjectPrefix, StringComparison.Ordinal)
            ? await JobsAsync(nodeId[ProjectPrefix.Length..], ct)
            : await ProjectNodesAsync(ct);
    }

    /// <summary>The projects the section lists, one row each.</summary>
    /// <remarks>
    /// A count off a project the workspace has already loaded, and "not loaded" off one it has
    /// not — the same two-speed answer the tree gives everywhere else. This runs after a click on
    /// the section rather than on the root listing, which is what makes the count affordable at
    /// all: it is a scan of a compilation, and the root is the place that refuses to do one.
    /// </remarks>
    private async Task<SolutionTreeNode[]> ProjectNodesAsync(CancellationToken ct)
    {
        var rows = new List<SolutionTreeNode>();

        foreach (var project in Projects(ct)
            .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase))
        {
            int? count = await CountAsync(project.Path, ct);

            rows.Add(new SolutionTreeNode(
                Id: ProjectPrefix + project.Path,
                Kind: SolutionNodeKind.CronProject,
                Label: project.Name,
                Description: count switch
                {
                    null => "not loaded",
                    1 => "1 job",
                    var many => $"{many} jobs",
                },
                ResourceUri: LspConverters.PathToUri(project.Path),
                HasChildren: count is not 0,
                ContextValue: SolutionNodeKind.CronProject,
                Dimmed: count is null));
        }

        return [.. rows];
    }

    /// <summary>How many jobs a project registers, or null when it is not loaded.</summary>
    private async Task<int?> CountAsync(string projectPath, CancellationToken ct)
    {
        if (Loaded(projectPath) is not { } project)
            return null;

        return await project.GetCompilationAsync(ct) is { } compilation
            ? Jobs.Of(compilation, projectPath, ct).Count
            : null;
    }

    /// <summary>The jobs of one project.</summary>
    private async Task<SolutionTreeNode[]> JobsAsync(string projectPath, CancellationToken ct)
    {
        if (Loaded(projectPath) is not { } project)
            return [];

        if (await project.GetCompilationAsync(ct) is not { } compilation)
            return [];

        var jobs = Jobs.Of(compilation, projectPath, ct);

        return
        [
            .. jobs
                .OrderBy(Label, StringComparer.OrdinalIgnoreCase)
                .ThenBy(job => job.Offset)
                .Select(Node),
        ];
    }

    /// <summary>One job, as a row.</summary>
    /// <remarks>
    /// A pure function from the job to the row, which is what makes the rendering decisions —
    /// what a dynamic value looks like, which target a click gets — checkable without a loaded
    /// workspace behind them.
    /// </remarks>
    internal static SolutionTreeNode Node(CronJob job) =>
        new(
            // The offset is what keeps two registrations with the same computed id distinct, and a
            // duplicate id makes the second branch fail to render rather than merely look odd.
            Id: string.Create(
                CultureInfo.InvariantCulture,
                $"{JobPrefix}{job.ProjectPath}|{job.FilePath}|{job.Offset}"),
            Kind: SolutionNodeKind.CronJob,
            Label: Label(job),
            Description: Description(job),
            ResourceUri: LspConverters.PathToUri(job.FilePath),
            HasChildren: false,
            ContextValue: ContextValue(job),

            // The registration rather than the job's method: it is where the id, the schedule and
            // the wiring live, it is what Ctrl+T cannot find, and it is the only target that exists
            // for every job — a dynamic method facet has no declaration to open.
            GoTo: new SolutionTreeNavigation(
                LspConverters.PathToUri(job.FilePath), job.Registration),
            GoToSecondary: job.Target is { } target && job.TargetUri is { } uri
                ? new SolutionTreeNavigation(uri, target)
                : null);

    /// <summary>
    /// What the menu is allowed to offer on this row, and how it is drawn: the base name plus a
    /// mark for anything only knowable at run time, plus one for a method there is somewhere to go
    /// to.
    /// </summary>
    private static string ContextValue(CronJob job) =>
        SolutionNodeKind.CronJob
        + (job.IsDynamic ? SolutionNodeKind.CronJobDynamicSuffix : string.Empty)
        + (job.Target is not null ? SolutionNodeKind.CronJobMethodSuffix : string.Empty);

    /// <summary>What the row is called: the job's id, or the method when it has no id.</summary>
    private static string Label(CronJob job)
    {
        if (job.JobId.Text is { Length: > 0 } id)
            return id;

        if (job.JobId.Detail is { Length: > 0 } detail)
            return $"{Open}{detail}{Close}";

        return job.Method.Text ?? $"{Open}unnamed{Close}";
    }

    /// <summary>
    /// The schedule and the method, which is what a reader came for.
    /// </summary>
    /// <remarks>
    /// A dynamic schedule is shown as where it comes from, never as a schedule. Printing "every 10
    /// minutes" for a value nobody read would be worse than printing nothing, because the row gives
    /// a reader no way to tell it is a guess.
    /// </remarks>
    private static string? Description(CronJob job)
    {
        var parts = new List<string>(2);

        if (job.Kind == CronRegistrationKind.Remove)
        {
            parts.Add("removed");
        }
        else if (job.Cron.Text is { Length: > 0 } schedule)
        {
            var parse = Cron.Parse(schedule, job.Dialect);
            parts.Add(CronDescription.Describe(parse) is { } sentence ? sentence : schedule);
        }
        else if (job.Cron.Origin == CronOrigin.Configuration)
        {
            parts.Add($"{Open}config: {job.Cron.Detail}{Close}");
        }
        else if (job.Cron.Origin != CronOrigin.Absent)
        {
            parts.Add($"{Open}{job.Cron.Detail ?? "computed"}{Close}");
        }

        if (job.Method.Text is { Length: > 0 } method)
            parts.Add(method);
        else if (job.Method.Detail is { Length: > 0 } detail)
            parts.Add($"{Open}{detail}{Close}");

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>
    /// The projects worth listing: those whose manifests name a scheduler, or all of them once the
    /// user has configured a binding of their own.
    /// </summary>
    /// <remarks>
    /// A configured binding names a wrapper method, and the project declaring it references no
    /// scheduling package — that is what made it a wrapper. So configuration widens the probe
    /// rather than narrowing it, and the section appears for the user who asked for it even when
    /// nothing on disk says the word Hangfire.
    /// </remarks>
    private List<(string Path, string Name)> Projects(CancellationToken ct)
    {
        bool configured = Settings.Bindings.Length > CronPresets.Bindings.Length;
        var found = new List<(string Path, string Name)>();

        foreach (var project in SolutionProjectIndex.Projects())
        {
            ct.ThrowIfCancellationRequested();

            if (configured || CronProjectProbe.Schedules(project.Path))
                found.Add(project);
        }

        return found;
    }

    /// <summary>
    /// The loaded project for a path, or null when the workspace has not got to it yet.
    /// </summary>
    /// <remarks>
    /// Read off the current snapshot rather than through the workspace cache's index, for the
    /// reason the tree's own <c>IsLoaded</c> gives: the index is behind the lock a solution load
    /// holds, so asking it here would stall the tree behind the load whose progress it is drawing.
    /// </remarks>
    private static Project? Loaded(string projectPath)
    {
        if (WorkspaceService.TryGetSessionSolution() is not { } solution)
            return null;

        foreach (var project in solution.Projects)
        {
            if (project.FilePath is { Length: > 0 } path
                && string.Equals(
                    Path.GetFullPath(path), Path.GetFullPath(projectPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return project;
            }
        }

        return null;
    }
}
