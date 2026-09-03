using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Which projects get a restore, and what wakes a project up when one happens outside this process.
/// </summary>
/// <remarks>
/// The behaviour these cover was reported as "every <c>Microsoft.Extensions</c> using is red in a
/// solution that builds fine": a legacy (non-SDK) project that uses <c>PackageReference</c> was
/// classified as packages.config-era, never restored, and therefore evaluated with no NuGet graph.
/// The shape of the project file — not whether it is SDK-style — is what decides.
/// </remarks>
[Collection(SharedState.Name)]
public class RestoreNeedTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "roslyn-sense-tests", "restore-need", Guid.NewGuid().ToString("N"));

    // Both ends, and serialized with the rest of the suite by the collection above, because the
    // watcher's registry is process-wide: these tests count handles, and any other test that loads a
    // project adds one. Resetting only on the way out would leave the first of these counting
    // somebody else's watchers.
    public RestoreNeedTests() => RestoreWatcher.ResetForTests();

    public void Dispose()
    {
        RestoreWatcher.ResetForTests();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void LegacyProjectUsingPackageReferenceNeedsAnAssetsRestore()
    {
        string project = WriteProject("Legacy", Legacy(packageReference: true));

        Assert.Equal(RestoreService.RestoreNeed.Assets, RestoreService.DetermineNeed(project));
    }

    /// <summary>
    /// The other half of the same decision: a legacy project with nothing but assembly references
    /// has no NuGet graph to restore, and restoring it on every load is what the old
    /// "legacy means skip" rule was protecting against.
    /// </summary>
    [Fact]
    public void LegacyProjectWithOnlyAssemblyReferencesNeedsNothing()
    {
        string project = WriteProject("LegacyPlain", Legacy(packageReference: false));

        Assert.Equal(RestoreService.RestoreNeed.None, RestoreService.DetermineNeed(project));
    }

    [Fact]
    public void SdkProjectWithoutAssetsNeedsAnAssetsRestore()
    {
        string project = WriteProject("Sdk", Sdk());

        Assert.Equal(RestoreService.RestoreNeed.Assets, RestoreService.DetermineNeed(project));
    }

    /// <summary>
    /// An assets file on disk is the whole definition of "already restored", for a legacy project
    /// exactly as for an SDK one — otherwise every load of the project would start a restore.
    /// </summary>
    [Fact]
    public void ProjectWithAnAssetsFileNeedsNothing()
    {
        string project = WriteProject("Restored", Legacy(packageReference: true));
        WriteAssets(project, "{}");

        Assert.Equal(RestoreService.RestoreNeed.None, RestoreService.DetermineNeed(project));
    }

    [Fact]
    public void PackagesConfigProjectWithAMissingPackageFolderNeedsThatRestore()
    {
        string project = WriteProject("Classic", Legacy(packageReference: false));
        File.WriteAllText(
            Path.Combine(Path.GetDirectoryName(project)!, "packages.config"),
            """
            <?xml version="1.0" encoding="utf-8"?>
            <packages>
              <package id="Newtonsoft.Json" version="13.0.3" targetFramework="net472" />
            </packages>
            """);

        Assert.Equal(RestoreService.RestoreNeed.PackagesConfig, RestoreService.DetermineNeed(project));

        // And once the folder is there, nothing is wanted — including when an assets file never
        // appears, which for a packages.config project it never does.
        Directory.CreateDirectory(Path.Combine(_root, "packages", "Newtonsoft.Json.13.0.3"));

        Assert.Equal(RestoreService.RestoreNeed.None, RestoreService.DetermineNeed(project));
    }

    /// <summary>
    /// A no-op restore rewrites <c>project.assets.json</c> with identical bytes. If that counted as
    /// a change, every build anybody ran would evict and reload every project in the solution.
    /// </summary>
    [Fact]
    public void FingerprintIgnoresARewriteWithIdenticalContent()
    {
        string project = WriteProject("Fingerprint", Sdk());
        WriteAssets(project, """{"version": 3}""");

        string before = RestoreWatcher.Fingerprint(project);
        WriteAssets(project, """{"version": 3}""");

        Assert.Equal(before, RestoreWatcher.Fingerprint(project));
    }

    [Fact]
    public void FingerprintMovesWhenTheGraphDoes()
    {
        string project = WriteProject("FingerprintMoves", Sdk());
        WriteAssets(project, """{"version": 3}""");

        string before = RestoreWatcher.Fingerprint(project);
        WriteAssets(project, """{"version": 3, "libraries": {"Newtonsoft.Json/13.0.3": {}}}""");

        Assert.NotEqual(before, RestoreWatcher.Fingerprint(project));
    }

    /// <summary>
    /// The never-restored project is the one that matters most — it is the one showing errors — and
    /// it has no <c>obj</c> directory to watch, so the watcher has to fall back to the project's own
    /// directory and wait for <c>obj</c> to appear.
    /// </summary>
    [Fact]
    public void AProjectWithNoObjDirectoryIsStillWatched()
    {
        string project = WriteProject("Unrestored", Sdk());

        RestoreWatcher.WatchForTests(project);

        Assert.Equal(1, RestoreWatcher.WatchedDirectoryCount);
    }

    /// <summary>
    /// One handle per directory however many times a project is loaded and evicted — the watcher
    /// outlives eviction on purpose, and re-registering must not accumulate handles.
    /// </summary>
    [Fact]
    public void WatchingTheSameProjectTwiceOpensOneHandle()
    {
        string project = WriteProject("Twice", Sdk());
        WriteAssets(project, "{}");

        RestoreWatcher.WatchForTests(project);
        RestoreWatcher.WatchForTests(project);

        Assert.Equal(1, RestoreWatcher.WatchedDirectoryCount);
    }

    /// <summary>
    /// A watcher on a directory that no longer exists reports nothing ever again, and leaving it in
    /// place costs more than the signal: its handle holds the deleted directory in Windows'
    /// pending-delete state, which is what makes the next restore's attempt to recreate <c>obj</c>
    /// fail with "access is denied".
    /// </summary>
    [Fact]
    public async Task DeletingTheWatchedObjDirectoryRebindsToTheProjectDirectory()
    {
        string project = WriteProject("Cleaned", Sdk());
        string projectDir = Path.GetDirectoryName(project)!;
        string objDir = Path.Combine(projectDir, "obj");
        WriteAssets(project, "{}");

        RestoreWatcher.WatchForTests(project);
        Assert.Contains(objDir, RestoreWatcher.WatchedDirectoriesForTests);

        // What `dotnet clean`, a branch switch, or a wiped tree does.
        Directory.Delete(objDir, recursive: true);

        Assert.True(
            await WaitAsync(() => RestoreWatcher.WatchedDirectoriesForTests.Contains(projectDir)),
            "The watcher never rebound to the project directory after obj/ was deleted.");

        // And the dead one is gone rather than kept alongside it, so the handle cap is not consumed
        // by directories that no longer exist.
        Assert.DoesNotContain(objDir, RestoreWatcher.WatchedDirectoriesForTests);
        Assert.Equal(1, RestoreWatcher.WatchedDirectoryCount);
    }

    /// <summary>
    /// The end of the road: when the project itself is gone there is nothing to rebind to, and
    /// reopening a handle on a deleted tree would be the leak this is meant to avoid.
    /// </summary>
    [Fact]
    public async Task DeletingTheWholeProjectDirectoryDropsTheWatchAltogether()
    {
        string project = WriteProject("Vanished", Sdk());
        string projectDir = Path.GetDirectoryName(project)!;
        WriteAssets(project, "{}");

        RestoreWatcher.WatchForTests(project);
        Assert.Equal(1, RestoreWatcher.WatchedDirectoryCount);

        Directory.Delete(projectDir, recursive: true);

        Assert.True(
            await WaitAsync(() => RestoreWatcher.WatchedDirectoryCount == 0),
            "A watcher was still registered after the project's whole directory was deleted.");
    }

    /// <summary>
    /// What is watched follows what is loaded, so dropping the whole cache drops the handles with
    /// it: otherwise a daemon that is pointed at one repository after another accumulates a tree's
    /// worth of watchers per switch and eventually spends its whole handle budget on trees nobody
    /// has open.
    /// </summary>
    [Fact]
    public void DroppingEverythingStopsWatching()
    {
        RestoreWatcher.WatchForTests(WriteProject("StopOne", Sdk()));
        RestoreWatcher.WatchForTests(WriteProject("StopTwo", Sdk()));
        Assert.Equal(2, RestoreWatcher.WatchedDirectoryCount);

        RestoreWatcher.StopAll();

        Assert.Equal(0, RestoreWatcher.WatchedDirectoryCount);

        // And it is a stop, not a disable: loading a project again watches it again.
        RestoreWatcher.WatchForTests(WriteProject("StopThree", Sdk()));
        Assert.Equal(1, RestoreWatcher.WatchedDirectoryCount);
    }

    private static async Task<bool> WaitAsync(Func<bool> condition)
    {
        // Generous: this waits on a filesystem notification, which is delivered on the OS's schedule.
        for (int i = 0; i < 100; i++)
        {
            if (condition())
                return true;

            await Task.Delay(100);
        }

        return false;
    }

    // ---- fixtures ----

    private string WriteProject(string name, string xml)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);

        // A .sln above it, so PackagesRootFor resolves the packages folder to the fake solution root
        // rather than to whatever directory the test process happens to sit in.
        string solution = Path.Combine(_root, "Fake.sln");
        if (!File.Exists(solution))
            File.WriteAllText(solution, "Microsoft Visual Studio Solution File, Format Version 12.00\n");

        string path = Path.Combine(dir, $"{name}.csproj");
        File.WriteAllText(path, xml);
        return path;
    }

    private static void WriteAssets(string projectPath, string content)
    {
        string objDir = Path.Combine(Path.GetDirectoryName(projectPath)!, "obj");
        Directory.CreateDirectory(objDir);
        File.WriteAllText(Path.Combine(objDir, "project.assets.json"), content);
    }

    private static string Legacy(bool packageReference) =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <PropertyGroup>
            <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>
            <OutputType>Library</OutputType>
          </PropertyGroup>
          <ItemGroup>
            <Reference Include="System" />
        {(packageReference ? """    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />""" : "")}
          </ItemGroup>
        </Project>
        """;

    private static string Sdk() =>
        """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;
}
