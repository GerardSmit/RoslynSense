using System.Collections.Immutable;
using RoslynMCP.Languages.MsBuild.Core;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Counting what a suppression is hiding, and over the right set of projects.
/// </summary>
/// <remarks>
/// The fixture is two projects under one <c>Directory.Build.props</c> that suppresses
/// <c>CS0168</c> — one unused local in Alpha, two in Beta. That shape is the whole question: the
/// same entry read in <c>Alpha.csproj</c> and read in the props file governs different projects,
/// and a count that ignored the difference would be right in one place and wrong in the other.
/// </remarks>
[Collection(SharedState.Name)]
public class WarningOccurrenceTests
{
    /// <summary>Generous: the first count compiles the project, and a cold compile is not fast.</summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(90);

    private static async Task OpenAsync(string projectPath) =>
        await WorkspaceService.GetOrOpenProjectAsync(
            projectPath, diagnosticWriter: Console.Error, cancellationToken: default);

    [Fact]
    public async Task AProjectFileCountsOnlyItsOwnProject()
    {
        WarningOccurrenceCache.Clear();
        await OpenAsync(FixturePaths.SuppressedAlphaFile);

        var occurrences = await WarningOccurrenceCache.GetAsync(
            FixturePaths.SuppressedAlphaFile, "CS0168", Wait, CancellationToken.None);

        Assert.NotNull(occurrences);
        Assert.Equal(1, occurrences!.Count);
        Assert.Equal(1, occurrences.Projects);
        Assert.False(occurrences.Partial);
    }

    /// <summary>
    /// The reach of a <c>Directory.Build.props</c>: the property applies to everything beneath it,
    /// so the count does too — three occurrences across the two projects, not one project's worth.
    /// </summary>
    [Fact]
    public async Task DirectoryBuildPropsCountsEveryProjectBeneathIt()
    {
        WarningOccurrenceCache.Clear();
        await OpenAsync(FixturePaths.SuppressedAlphaFile);
        await OpenAsync(FixturePaths.SuppressedBetaFile);

        var occurrences = await WarningOccurrenceCache.GetAsync(
            FixturePaths.SuppressedWarningsPropsFile, "CS0168", Wait, CancellationToken.None);

        Assert.NotNull(occurrences);
        Assert.Equal(2, occurrences!.Projects);
        Assert.Equal(3, occurrences.Count);
    }

    /// <summary>
    /// A code with nothing left to suppress counts zero rather than going quiet, because zero is
    /// the answer the reader wants — it means the line can go.
    /// </summary>
    [Fact]
    public async Task ACodeWithNoOccurrencesCountsZero()
    {
        WarningOccurrenceCache.Clear();
        await OpenAsync(FixturePaths.SuppressedAlphaFile);

        // CS0169 is the field version of the same idea, and the fixture has no unused fields.
        var occurrences = await WarningOccurrenceCache.GetAsync(
            FixturePaths.SuppressedAlphaFile, "CS0169", Wait, CancellationToken.None);

        Assert.NotNull(occurrences);
        Assert.Equal(0, occurrences!.Count);
        Assert.Equal(1, occurrences.Projects);
    }

    /// <summary>
    /// MSBuild's own codes are not counted. Only a full build produces them, and a hover is not a
    /// reason to start one — where a count of zero would read as "the suppression is dead", which
    /// is the one wrong answer worth refusing to give.
    /// </summary>
    [Fact]
    public async Task BuildCodesAreNotCounted()
    {
        WarningOccurrenceCache.Clear();
        await OpenAsync(FixturePaths.SuppressedAlphaFile);

        Assert.False(WarningOccurrenceCache.IsCountable("MSB3277"));
        Assert.True(WarningOccurrenceCache.IsCountable("CS0168"));
        Assert.True(WarningOccurrenceCache.IsCountable("CA1822"));
        Assert.True(WarningOccurrenceCache.IsCountable("NU1605"));

        var occurrences = await WarningOccurrenceCache.GetAsync(
            FixturePaths.SuppressedAlphaFile, "MSB3277", TimeSpan.FromMilliseconds(200), CancellationToken.None);

        Assert.Null(occurrences);
        Assert.Equal(0, Interlocked.Read(ref WarningOccurrenceCache.Counts));
    }

    /// <summary>
    /// A NuGet code is counted from the restore rather than from a compilation, because that is
    /// where it comes from. The restore itself is seeded here: the counting is what this asserts,
    /// and a test that shelled out to <c>dotnet restore</c> would be asserting the network.
    /// </summary>
    [Fact]
    public async Task NuGetCodesAreCountedFromTheRestoreLog()
    {
        WarningOccurrenceCache.Clear();
        RestoreWarningCounts.Clear();
        await OpenAsync(FixturePaths.SuppressedAlphaFile);

        RestoreWarningCounts.Seed(
            FixturePaths.SuppressedAlphaFile,
            new Dictionary<string, int> { ["NU1605"] = 2, ["NU1903"] = 1 }
                .ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));

        var occurrences = await WarningOccurrenceCache.GetAsync(
            FixturePaths.SuppressedAlphaFile, "NU1605", Wait, CancellationToken.None);

        Assert.NotNull(occurrences);
        Assert.Equal(2, occurrences!.Count);
        Assert.Equal(1, occurrences.Projects);
        Assert.Equal(0, Interlocked.Read(ref RestoreWarningCounts.Restores));
    }

    /// <summary>
    /// A code the restore did not report counts zero — the restore ran and found nothing, which is
    /// exactly the answer that makes a suppression removable.
    /// </summary>
    [Fact]
    public async Task ANuGetCodeMissingFromTheLogCountsZero()
    {
        WarningOccurrenceCache.Clear();
        RestoreWarningCounts.Clear();
        await OpenAsync(FixturePaths.SuppressedAlphaFile);

        RestoreWarningCounts.Seed(
            FixturePaths.SuppressedAlphaFile,
            ImmutableDictionary<string, int>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase));

        var occurrences = await WarningOccurrenceCache.GetAsync(
            FixturePaths.SuppressedAlphaFile, "NU1701", Wait, CancellationToken.None);

        Assert.NotNull(occurrences);
        Assert.Equal(0, occurrences!.Count);
    }

    /// <summary>A warm read answers from the cache and compiles nothing.</summary>
    [Fact]
    public async Task AWarmReadCountsNothingAgain()
    {
        WarningOccurrenceCache.Clear();
        await OpenAsync(FixturePaths.SuppressedAlphaFile);

        await WarningOccurrenceCache.GetAsync(
            FixturePaths.SuppressedAlphaFile, "CS0168", Wait, CancellationToken.None);

        long after = Interlocked.Read(ref WarningOccurrenceCache.Counts);
        Assert.Equal(1, after);

        Assert.NotNull(WarningOccurrenceCache.TryGet(FixturePaths.SuppressedAlphaFile, "CS0168"));
        Assert.Equal(after, Interlocked.Read(ref WarningOccurrenceCache.Counts));
    }
}
