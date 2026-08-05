using NuGet.Versioning;
using RoslynMCP.Services.Packages;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Version selection and how far a package has moved.
/// </summary>
/// <remarks>
/// The resolution is the interesting half: the version lock picks the newest version *within* the
/// bound rather than hiding the ones that went too far. Locked to the current major, 1.0.0 must be
/// offered 1.1.0 — not nothing.
/// </remarks>
public class PackageUpdateServiceTests
{
    private static readonly IReadOnlyList<NuGetVersion> Available =
    [
        NuGetVersion.Parse("1.0.0"),
        NuGetVersion.Parse("1.0.1"),
        NuGetVersion.Parse("1.1.0"),
        NuGetVersion.Parse("2.0.0"),
        NuGetVersion.Parse("2.1.0-beta.1"),
    ];

    [Theory]
    [InlineData(VersionLock.None, "2.0.0")]
    [InlineData(VersionLock.Major, "1.1.0")]
    [InlineData(VersionLock.Minor, "1.0.1")]
    public void VersionLockBoundsTheCandidate(VersionLock versionLock, string expected)
    {
        var current = NuGetVersion.Parse("1.0.0");

        var resolved = PackageUpdateService.Resolve(
            current, "1.0.0", Available, new UpdateQuery(Lock: versionLock));

        Assert.Equal(expected, resolved?.ToNormalizedString());
    }

    /// <summary>
    /// The platform band bounds the candidate by the .NET major the project targets rather than
    /// by the major the reference happens to be on — and it does so under the default lock, not
    /// behind a mode of its own. "Latest" on a net8.0 project must not mean 9.x of the platform
    /// families.
    /// </summary>
    [Fact]
    public void ThePlatformBandCapsTheCandidateUnderAnyLock()
    {
        IReadOnlyList<NuGetVersion> band =
        [
            NuGetVersion.Parse("6.0.0"),
            NuGetVersion.Parse("8.0.1"),
            NuGetVersion.Parse("8.0.11"),
            NuGetVersion.Parse("9.0.0"),
        ];

        var unlocked = PackageUpdateService.Resolve(
            NuGetVersion.Parse("8.0.1"), "8.0.1", band,
            new UpdateQuery(), platformMajor: 8);
        Assert.Equal("8.0.11", unlocked?.ToNormalizedString());

        // The legacy wire value still parses; it adds nothing on top of the cap.
        var legacy = PackageUpdateService.Resolve(
            NuGetVersion.Parse("8.0.1"), "8.0.1", band,
            new UpdateQuery(Lock: VersionLock.Framework), platformMajor: 8);
        Assert.Equal("8.0.11", legacy?.ToNormalizedString());
    }

    /// <summary>
    /// The cap also pulls a reference that fell behind the platform forward to it — the point is
    /// the band the project targets, not the band the reference is on.
    /// </summary>
    [Fact]
    public void ThePlatformBandLiftsAReferenceThatIsBehindIt()
    {
        IReadOnlyList<NuGetVersion> band =
        [
            NuGetVersion.Parse("6.0.0"),
            NuGetVersion.Parse("8.0.11"),
            NuGetVersion.Parse("9.0.0"),
        ];

        var resolved = PackageUpdateService.Resolve(
            NuGetVersion.Parse("6.0.0"), "6.0.0", band,
            new UpdateQuery(), platformMajor: 8);

        Assert.Equal("8.0.11", resolved?.ToNormalizedString());
    }

    /// <summary>
    /// A package that does not version with the platform must not be bounded by it. Capping a
    /// library whose 8.x was never released reports it as up to date, which is worse than
    /// offering too much.
    /// </summary>
    [Fact]
    public void APackageWithoutThatBandIsNotCapped()
    {
        var resolved = PackageUpdateService.Resolve(
            NuGetVersion.Parse("1.0.0"), "1.0.0", Available,
            new UpdateQuery(), platformMajor: 8);

        Assert.Equal("2.0.0", resolved?.ToNormalizedString());
    }

    [Fact]
    public void ThePlatformBandIsUnboundedWithoutAProjectTarget()
    {
        var resolved = PackageUpdateService.Resolve(
            NuGetVersion.Parse("1.0.0"), "1.0.0", Available,
            new UpdateQuery(), platformMajor: null);

        Assert.Equal("2.0.0", resolved?.ToNormalizedString());
    }

    [Fact]
    public void PrereleaseAutoFollowsTheReferencedStability()
    {
        // A stable reference stays stable...
        var stable = PackageUpdateService.Resolve(
            NuGetVersion.Parse("1.0.0"), "1.0.0", Available, new UpdateQuery());
        Assert.Equal("2.0.0", stable?.ToNormalizedString());

        // ...and a prerelease reference is allowed to move to another prerelease.
        var prerelease = PackageUpdateService.Resolve(
            NuGetVersion.Parse("2.0.0-beta.1"), "2.0.0-beta.1", Available, new UpdateQuery());
        Assert.NotNull(prerelease);
        Assert.True(prerelease! >= NuGetVersion.Parse("2.0.0-beta.1"));
    }

    [Fact]
    public void PrereleaseNeverIgnoresPrereleases()
    {
        var resolved = PackageUpdateService.Resolve(
            NuGetVersion.Parse("2.0.0"),
            "2.0.0",
            Available,
            new UpdateQuery(Prerelease: PrereleaseReporting.Never));

        Assert.Equal("2.0.0", resolved?.ToNormalizedString());
    }

    [Fact]
    public void AVersionRangeKeepsItsUpperBound()
    {
        // A PackageReference can carry a range rather than a version; the bound must survive.
        var resolved = PackageUpdateService.Resolve(
            NuGetVersion.Parse("1.0.0"), "[1.0.0,2.0.0)", Available, new UpdateQuery());

        Assert.Equal("1.1.0", resolved?.ToNormalizedString());
    }

    [Theory]
    [InlineData("1.0.0", "2.0.0", UpdateSeverity.Major)]
    [InlineData("1.0.0", "1.1.0", UpdateSeverity.Minor)]
    [InlineData("1.0.0", "1.0.1", UpdateSeverity.Patch)]
    [InlineData("1.0.0.1", "1.0.0.2", UpdateSeverity.Patch)]
    [InlineData("1.0.0", "1.0.0", UpdateSeverity.None)]
    public void SeverityClassifiesTheMove(string current, string latest, UpdateSeverity expected) =>
        Assert.Equal(
            expected,
            PackageUpdateService.SeverityOf(NuGetVersion.Parse(current), NuGetVersion.Parse(latest)));

    [Fact]
    public void LeavingAPrereleaseCountsAsMajor() =>
        // Regardless of the numbers: moving off a prerelease is the change worth flagging.
        Assert.Equal(
            UpdateSeverity.Major,
            PackageUpdateService.SeverityOf(
                NuGetVersion.Parse("2.0.0-beta.1"), NuGetVersion.Parse("2.0.0")));

    [Fact]
    public void AVersionLockIsNotLeakedWhenTheCurrentVersionIsGone()
    {
        // Unlisted after a bad release, a never-published local build, a feed migration: the
        // referenced version is simply not in the list. NuGet's float range then falls back to the
        // lowest version above the base range, which ignores the lock — so "same major only"
        // would quietly offer a new major.
        var available = new[] { NuGetVersion.Parse("2.0.0"), NuGetVersion.Parse("2.1.0") };

        Assert.Null(PackageUpdateService.Resolve(
            NuGetVersion.Parse("1.0.0"), "1.0.0", available, new UpdateQuery(Lock: VersionLock.Major)));

        // Unlocked, moving to a new major is exactly what was asked for.
        Assert.Equal("2.1.0", PackageUpdateService.Resolve(
            NuGetVersion.Parse("1.0.0"), "1.0.0", available, new UpdateQuery())?.ToNormalizedString());
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("[1.0.0,2.0.0)", "1.0.0")]
    [InlineData("[1.2.3]", "1.2.3")]
    public void AReferenceThatCarriesARangeStillHasACurrentVersion(string raw, string expected) =>
        // Dropping these reported the package as up to date, which is the one answer that is
        // certainly wrong.
        Assert.Equal(expected, PackageUpdateService.Current(raw)?.ToNormalizedString());

    [Fact]
    public void SomethingThatIsNeitherAVersionNorARangeHasNoCurrentVersion() =>
        Assert.Null(PackageUpdateService.Current("not a version"));

    [Fact]
    public async Task UpdateAllWithNothingSelectedFailsCleanly()
    {
        var result = await PackageUpdateService.UpdateAllAsync([], restore: false, default);

        Assert.False(result.Success);
        Assert.Contains("Nothing selected", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
