using Microsoft.CodeAnalysis;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Which solution answers when more than one is loaded — <see cref="WorkspaceService.TryGetSessionSolution"/>.
/// </summary>
/// <remarks>
/// This used to pick whichever workspace was accessed most recently, which is only ever a guess at
/// which solution the caller means, and a wrong one as soon as two loaded solutions share a
/// project. The shared file is then a document in both workspaces, each with its own version
/// stamps, so the answer — and every result id built from it — changed with whatever had been
/// touched last. Consecutive diagnostic sweeps handed the client two different ids for a file
/// nobody had opened, each mismatching the one before: every shared file re-reported and re-bound
/// on every pass, for the life of the session, with the id flipping between two fixed values.
///
/// Reproduced against a real solution before it was fixed, at 391 of 391 files re-reported per
/// pass and eleven seconds a pass; afterwards, none.
/// </remarks>
[Collection(SharedState.Name)]
public class SessionSolutionTests
{
    /// <summary>The project only <c>Alpha.sln</c> holds, so the answer names itself.</summary>
    private const string AlphaOnly = "OnlyAlpha";

    /// <summary>The project only <c>Beta.sln</c> holds.</summary>
    private const string BetaOnly = "OnlyBeta";

    [Fact]
    public async Task TheBoundSolutionAnswersEvenWhenAnotherWasUsedMoreRecently()
    {
        using var _ = WorkspaceService.BindSolutionForTesting(FixturePaths.AlphaSolutionFile);

        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.OnlyAlphaProjectFile);
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.OnlyBetaProjectFile);

        // Beta was loaded second, so it is the most recently used entry — the state that used to
        // decide this.
        var answer = WorkspaceService.TryGetSessionSolution();

        Assert.NotNull(answer);
        Assert.Contains(answer!.Projects, p => p.Name == AlphaOnly);
        Assert.DoesNotContain(answer.Projects, p => p.Name == BetaOnly);
    }

    /// <summary>
    /// The shared project's version stamp is the same on every call, however the cache is used in
    /// between.
    /// </summary>
    /// <remarks>
    /// The assertion the churn is made of. A file's result id is built from its project's dependent
    /// semantic version, so two solutions answering by turns is not merely untidy — it is a
    /// different id for an unchanged file, which is what the client re-pulls on.
    /// </remarks>
    [Fact]
    public async Task TheSharedProjectsVersionDoesNotMoveWhenTheOtherSolutionIsTouched()
    {
        using var _ = WorkspaceService.BindSolutionForTesting(FixturePaths.AlphaSolutionFile);

        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.OnlyAlphaProjectFile);
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.OnlyBetaProjectFile);

        var before = await SharedVersionAsync();

        // Touch each in turn, which is what a sweep interleaved with any other request does: both
        // orderings used to produce a different answer, and the id alternated between the two.
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.OnlyBetaProjectFile);
        var afterBeta = await SharedVersionAsync();

        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.OnlyAlphaProjectFile);
        var afterAlpha = await SharedVersionAsync();

        Assert.Equal(before, afterBeta);
        Assert.Equal(before, afterAlpha);
    }

    /// <summary>
    /// A solution bound but not loaded yet answers nothing, rather than answering from a different
    /// solution that happens to be loaded.
    /// </summary>
    /// <remarks>
    /// The window is real and is exactly when it hurts: the seconds before the bound solution's
    /// first project lands are also when a second solution loading alongside it is the most
    /// recently used entry. Answering from that one hands back files that are not this session's,
    /// stamped with versions the bound solution contradicts as soon as it finishes — the same churn
    /// through the fallback. "Nothing loaded yet" is a state every caller already handles.
    /// </remarks>
    [Fact]
    public async Task ASolutionBoundButNotLoadedAnswersNothingRatherThanAnotherSolution()
    {
        // Loaded first, so it is in the cache and is the most recently used solution entry.
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.OnlyBetaProjectFile);

        using var _ = WorkspaceService.BindSolutionForTesting(FixturePaths.UnloadedSolutionFile);

        var answer = WorkspaceService.TryGetSessionSolution();

        // Nothing at all in a process holding only this fixture. A loose .csproj another test left
        // in the cache is not a competing solution and does still answer — that is the fallback's
        // whole purpose — so what is pinned here is that Beta never does, however the run is
        // ordered.
        Assert.True(
            answer is null || answer.Projects.All(p => p.Name != BetaOnly),
            "a session bound to one solution answered from another");
    }

    /// <summary>With no solution bound at all, the most-recently-used guess still stands.</summary>
    /// <remarks>
    /// A process that was never told which solution it serves — a loose project opened from a
    /// directory with no .sln — has nothing better to go on, and answering nothing there would
    /// empty the Solution Explorer for everyone who never passed <c>--solution</c>.
    /// </remarks>
    [Fact]
    public async Task WithNothingBoundTheMostRecentlyUsedSolutionStillAnswers()
    {
        using var _ = WorkspaceService.BindSolutionForTesting(null);

        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.OnlyBetaProjectFile);

        var answer = WorkspaceService.TryGetSessionSolution();

        Assert.NotNull(answer);
        Assert.NotEmpty(answer!.Projects);
    }

    private static async Task<VersionStamp> SharedVersionAsync()
    {
        var solution = WorkspaceService.TryGetSessionSolution();
        Assert.NotNull(solution);

        var shared = solution!.Projects.SingleOrDefault(p => p.Name == "Shared");
        Assert.NotNull(shared);

        return await shared!.GetDependentSemanticVersionAsync();
    }
}
