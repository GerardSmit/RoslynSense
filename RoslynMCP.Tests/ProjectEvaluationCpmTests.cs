using RoslynMCP.Services.ProjectModel;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Central Package Management, read without a restore.
/// </summary>
/// <remarks>
/// The regression these guard: a centrally managed PackageReference carries no Version metadata,
/// because NuGet applies the central version during restore rather than during evaluation. Reading
/// only the reference's own metadata produced empty strings, which flowed into the Updates tab
/// (nothing parses, so nothing is ever outdated) and the Consolidate tab (one distinct empty
/// version, so nothing ever conflicts). Both came up silently blank on every CPM repository.
/// </remarks>
public class ProjectEvaluationCpmTests
{
    [Fact]
    public async Task CentralPackageVersionsResolveWithoutRestore()
    {
        ProjectEvaluationService.Clear();
        var evaluation = await ProjectEvaluationService.EvaluateAsync(FixturePaths.CpmManagedProjectFile);

        Assert.NotNull(evaluation);
        var json = Find(evaluation!, "Newtonsoft.Json");

        Assert.Equal("13.0.3", json.Version);
        Assert.True(json.IsCentrallyManaged);
        Assert.False(json.IsVersionOverride);
        Assert.EndsWith("Directory.Packages.props", json.VersionSource ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PackageVersionUpdateFormIsHonored()
    {
        ProjectEvaluationService.Clear();
        var evaluation = await ProjectEvaluationService.EvaluateAsync(FixturePaths.CpmManagedProjectFile);

        // Declared with Update= rather than Include=, which a reader that only looks at Include
        // silently drops.
        Assert.Equal("3.1.1", Find(evaluation!, "Serilog").Version);
    }

    [Fact]
    public async Task VersionOverrideBeatsTheCentralVersion()
    {
        ProjectEvaluationService.Clear();
        var evaluation = await ProjectEvaluationService.EvaluateAsync(FixturePaths.CpmOverriddenProjectFile);

        var json = Find(evaluation!, "Newtonsoft.Json");

        Assert.Equal("13.0.1", json.Version);
        Assert.True(json.IsVersionOverride);
        // The override lives in the csproj, so that is where an update has to be written.
        Assert.EndsWith(".csproj", json.VersionSource ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GlobalPackageReferenceIsListedAndFlagged()
    {
        ProjectEvaluationService.Clear();
        var evaluation = await ProjectEvaluationService.EvaluateAsync(FixturePaths.CpmManagedProjectFile);

        var versioning = Find(evaluation!, "Nerdbank.GitVersioning");

        // Not implicit: it is a real, user-authored dependency and belongs in the list. Flagged,
        // because it applies repository-wide and cannot be removed from one project.
        Assert.False(versioning.IsImplicit);
        Assert.True(versioning.IsGlobalPackageReference);
        Assert.Equal("3.6.133", versioning.Version);
    }

    [Fact]
    public async Task ManagePackageVersionsCentrallyIsCaptured()
    {
        ProjectEvaluationService.Clear();
        var evaluation = await ProjectEvaluationService.EvaluateAsync(FixturePaths.CpmManagedProjectFile);

        Assert.Equal("true", evaluation!.Properties.GetValueOrDefault("ManagePackageVersionsCentrally"));
    }

    [Fact]
    public async Task MultiTargetedProjectReportsEveryFramework()
    {
        ProjectEvaluationService.Clear();
        var evaluation = await ProjectEvaluationService.EvaluateAsync(FixturePaths.CpmMultiTfmProjectFile);

        Assert.Equal(["net10.0", "netstandard2.0"], evaluation!.TargetFrameworks);
        Assert.Equal("13.0.3", Find(evaluation, "Newtonsoft.Json").Version);
    }

    [Fact]
    public async Task EditingDirectoryPackagesPropsInvalidatesTheEvaluation()
    {
        ProjectEvaluationService.Clear();
        string props = FixturePaths.CpmDirectoryPackagesProps;
        string original = await File.ReadAllTextAsync(props);

        try
        {
            var before = await ProjectEvaluationService.EvaluateAsync(FixturePaths.CpmManagedProjectFile);
            Assert.Equal("13.0.3", Find(before!, "Newtonsoft.Json").Version);

            await File.WriteAllTextAsync(props, original.Replace("13.0.3", "13.0.2"));
            // Every imported file is mtime-stamped, so the props file is part of the cache key
            // even though the project itself did not change.
            File.SetLastWriteTimeUtc(props, DateTime.UtcNow.AddSeconds(2));

            var after = await ProjectEvaluationService.EvaluateAsync(FixturePaths.CpmManagedProjectFile);
            Assert.Equal("13.0.2", Find(after!, "Newtonsoft.Json").Version);
        }
        finally
        {
            await File.WriteAllTextAsync(props, original);
            ProjectEvaluationService.Clear();
        }
    }

    private static PackageReferenceInfo Find(ProjectEvaluation evaluation, string id)
    {
        var package = evaluation.PackageReferences
            .FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        Assert.True(package is not null, $"{id} was not in the evaluated package references.");
        return package!;
    }
}
