using System.IO.Compression;
using RoslynMCP.Services.Packages;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Package detail, and the parts of it that only exist inside the .nupkg.
/// </summary>
/// <remarks>
/// The registration index carries a description and an SPDX expression, but not the README, not a
/// licence shipped as a file, and — on most private feeds — not an icon URL either. Those come
/// from the package itself, which is the only reason the payload service exists.
/// </remarks>
public class NuGetMetadataServiceTests : IDisposable
{
    private readonly PackageFeedFixture _feed = new();

    [Fact]
    public void ANupkgCarriesItsReadmeIconAndLicenseFile()
    {
        string path = _feed.Add(new PackageFeedFixture.PackageSpec(
            "Contoso.Widgets", "1.0.0",
            WithIcon: true,
            Readme: "# Widgets\n\nDoes widget things.",
            LicenseFileText: "All rights reserved."));

        using var archive = ZipFile.OpenRead(path);

        Assert.NotNull(archive.GetEntry("readme.md"));
        Assert.NotNull(archive.GetEntry("icon.png"));
        Assert.NotNull(archive.GetEntry("LICENSE.txt"));
        Assert.Contains("<readme>readme.md</readme>", Nuspec(archive));
        Assert.Contains("""<license type="file">LICENSE.txt</license>""", Nuspec(archive));
    }

    [Fact]
    public void AnSpdxExpressionIsDeclaredAsAnExpressionNotAFile()
    {
        string path = _feed.Add(new PackageFeedFixture.PackageSpec(
            "Contoso.Widgets", "1.0.0", LicenseExpression: "MIT"));

        using var archive = ZipFile.OpenRead(path);

        Assert.Contains("""<license type="expression">MIT</license>""", Nuspec(archive));
        Assert.Null(archive.GetEntry("LICENSE.txt"));
    }

    [Fact]
    public void DependencyGroupsAreWrittenPerTargetFramework()
    {
        string path = _feed.Add(new PackageFeedFixture.PackageSpec(
            "Contoso.Widgets", "1.0.0",
            LibFrameworks: ["net10.0", "netstandard2.0"],
            Dependencies:
            [
                ("net10.0", "Newtonsoft.Json", "13.0.3"),
                ("netstandard2.0", "Newtonsoft.Json", "12.0.0"),
            ]));

        string nuspec = Nuspec(ZipFile.OpenRead(path));

        Assert.Contains("""<group targetFramework="net10.0">""", nuspec);
        Assert.Contains("""<group targetFramework="netstandard2.0">""", nuspec);
    }

    [Fact]
    public async Task AnUnknownPackageIsNullRatherThanAnEmptyRecord()
    {
        // The panel distinguishes "no metadata" from "metadata that happens to be blank", because
        // only the first one should leave the previous selection's pane in place.
        var metadata = await NuGetMetadataService.GetAsync(
            $"Nonexistent.Package.{Guid.NewGuid():N}", "1.0.0",
            includePrerelease: false, includeReadme: false, refresh: false, default);

        Assert.Null(metadata);
    }

    [Fact]
    public async Task DependencyGroupsOfAnUnknownPackageAreEmptyNotNull()
    {
        // The compatibility check reads this and treats "no groups" as compatible, so it must
        // never have to null-check.
        var groups = await NuGetMetadataService.DependencyGroupsAsync(
            $"Nonexistent.Package.{Guid.NewGuid():N}", "1.0.0", default);

        Assert.Empty(groups);
    }

    [Fact]
    public async Task PayloadOfAnUnknownPackageIsNullRatherThanThrowing()
    {
        var payload = await NuGetPayloadService.ReadAsync(
            $"Nonexistent.Package.{Guid.NewGuid():N}", "1.0.0", default);

        Assert.Null(payload);
    }

    [Fact]
    public async Task AnUnparseableVersionIsRejectedBeforeAnyNetworkCall()
    {
        Assert.Null(await NuGetPayloadService.EnsureNupkgAsync("Contoso.Widgets", "not-a-version", default));
    }

    private static string Nuspec(ZipArchive archive)
    {
        var entry = archive.Entries.First(e => e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    public void Dispose() => _feed.Dispose();
}
