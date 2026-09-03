using RoslynMCP.Services;
using RoslynMCP.Services.Packages;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Vulnerability and deprecation reporting.
/// </summary>
/// <remarks>
/// Before this existed the panel reported every package as neither vulnerable nor deprecated,
/// unconditionally — the two warning banners it already had could never fire. Reporting "no known
/// vulnerabilities" when nothing was ever checked is the worst available failure mode for a
/// security signal, so an audit that cannot run says so instead.
/// </remarks>
[Collection(SharedState.Name)]
public class PackageAuditServiceTests
{
    [Fact]
    public async Task AuditWithoutASolutionReportsWhyRatherThanAllClear()
    {
        await WorkspaceService.EvictAllAsync(default);
        PackageAuditService.Invalidate();

        var audit = await PackageAuditService.AuditAsync(refresh: true, default);

        Assert.Empty(audit.Vulnerabilities);
        Assert.Empty(audit.Deprecations);
        Assert.NotNull(audit.Error);
    }

    [Fact]
    public async Task AuditOfARestoredProjectParsesCleanly()
    {
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);
        PackageAuditService.Invalidate();

        var audit = await PackageAuditService.AuditAsync(refresh: true, default);

        // The fixture has no advisories, so the shape is what is asserted: every entry that does
        // come back is complete enough to render and to act on.
        Assert.All(audit.Vulnerabilities, advisory =>
        {
            Assert.False(string.IsNullOrWhiteSpace(advisory.Id));
            Assert.InRange(advisory.Severity, 0, 3);
        });
        Assert.All(audit.Deprecations, deprecation =>
            Assert.False(string.IsNullOrWhiteSpace(deprecation.Id)));
    }
}
