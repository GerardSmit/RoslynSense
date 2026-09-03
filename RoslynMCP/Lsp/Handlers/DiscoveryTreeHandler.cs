using RoslynMCP.Languages;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>
/// The Discovery view's server side: what the solution runs and exposes, as opposed to what it
/// contains.
/// </summary>
/// <remarks>
/// <para>
/// Everything here comes from a language pack — see <see cref="ILanguageDiscoveryContributor"/>,
/// which is the whole of the contract. This handler owns no subject of its own: it lists the
/// sections at the root and routes a click on any deeper node back to the pack that minted its id.
/// So adding a section is adding a pack, and nothing in here changes.
/// </para>
/// <para>
/// The sections were hung under the solution node in the Solution Explorer before this view
/// existed. Two things went wrong there and both are structural rather than cosmetic. A section
/// sat in a list of solution folders while being nothing of the kind, so the one row that was not
/// browsable by location looked exactly like the rows that were; and it could only ever be found
/// by expanding the solution, which is the node people collapse first on a repository big enough
/// for the question to matter.
/// </para>
/// </remarks>
internal static class DiscoveryTreeHandler
{
    public static async Task<SolutionTreeNode[]> ChildrenAsync(
        SolutionTreeParams p, CancellationToken ct, LanguageSession? languages = null)
    {
        // The bound path rather than a loaded workspace, for the reason the Solution Explorer
        // gives: the daemon starts with nothing loaded, and a view that waits for a compilation
        // before it draws anything is empty for as long as the load takes.
        string? solutionPath =
            WorkspaceService.BoundSolutionPath ?? WorkspaceService.TryGetSessionSolution()?.FilePath;

        if (solutionPath is null)
            return [];

        return string.IsNullOrEmpty(p.NodeId)
            ? await SectionsAsync(solutionPath, ct, languages)
            : await ChildrenOfAsync(p, ct, languages);
    }

    /// <summary>
    /// The sections, in registration order.
    /// </summary>
    /// <remarks>
    /// On the hot path: this runs every time the view is drawn, so a contributor with nothing to
    /// say has to say so without evaluating a project. One that throws costs its own section and
    /// nothing else — a pack must not be able to empty the view.
    /// </remarks>
    private static async Task<SolutionTreeNode[]> SectionsAsync(
        string solutionPath, CancellationToken ct, LanguageSession? languages)
    {
        var sections = new List<SolutionTreeNode>();

        foreach (var contributor in
            LanguageScope.Of(languages).Contributors<ILanguageDiscoveryContributor>())
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (await contributor.SectionAsync(solutionPath, ct) is { } section)
                    sections.Add(section);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                ServiceLog.Warn(
                    $"Could not list the '{contributor.NodeIdPrefix}' section: {ex.Message}",
                    key: $"discovery-section:{contributor.NodeIdPrefix}");
            }
        }

        return [.. sections];
    }

    /// <summary>The children of one pack's node, from the pack that minted its id.</summary>
    /// <remarks>
    /// A failure is swallowed to an empty list for the reason the Solution Explorer swallows its
    /// own: the client cannot tell a rejected request from a node with nothing in it, so a crash
    /// in a pack would read as "this project schedules nothing" — an answer, and a wrong one.
    /// Saying so in the log is the difference between a bug report and a mystery.
    /// </remarks>
    private static async Task<SolutionTreeNode[]> ChildrenOfAsync(
        SolutionTreeParams p, CancellationToken ct, LanguageSession? languages)
    {
        string nodeId = p.NodeId!;

        var contributor = LanguageScope.Of(languages)
            .Contributors<ILanguageDiscoveryContributor>()
            .FirstOrDefault(c => nodeId.StartsWith(c.NodeIdPrefix, StringComparison.Ordinal));

        if (contributor is null)
            return [];

        try
        {
            return await contributor.ChildrenAsync(nodeId, p, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ServiceLog.Warn(
                $"Could not list '{nodeId}': {ex.Message}",
                key: $"discovery:{contributor.NodeIdPrefix}");
            return [];
        }
    }
}
