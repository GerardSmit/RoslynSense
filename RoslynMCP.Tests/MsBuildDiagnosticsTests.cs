using System.Collections.Immutable;
using System.Diagnostics;
using NuGet.Versioning;
using RoslynMCP.Languages.MsBuild.Core;
using RoslynMCP.Languages.MsBuild.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services.Packages;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// What a project file's package references are worth saying, and — more importantly — what they
/// are not.
/// </summary>
/// <remarks>
/// The two tests that matter most here are the ones about silence: a cold cache must answer
/// instantly and say nothing, and a feed that did not answer must never be reported as a package
/// that does not exist. Both failures are the kind that look like features until someone is on a
/// VPN or a plane.
/// </remarks>
[Collection(SharedState.Name)]
public class MsBuildDiagnosticsTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "roslynsense-msbdiag-" + Guid.NewGuid().ToString("N")[..8]);

    public MsBuildDiagnosticsTests()
    {
        PackageStatusCache.Clear();
        MsBuildDocumentCache.Clear();
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        PackageStatusCache.Clear();

        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private string Project(string version = "12.0.2", string name = "App.csproj")
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="{version}" />
              </ItemGroup>
            </Project>
            """);

        MsBuildDocumentCache.Invalidate(path);
        return path;
    }

    private static PackageStatus Status(
        string[] versions,
        bool exists = true,
        bool healthy = true,
        PackageVulnerabilityInfo[]? vulnerabilities = null,
        PackageDeprecationInfo? deprecation = null) =>
        new(
            [.. versions.Select(NuGetVersion.Parse)],
            exists,
            vulnerabilities?.ToImmutableArray() ?? [],
            deprecation,
            healthy,
            DateTime.UtcNow);

    /// <summary>
    /// The invariant the whole design exists to hold. A cold cache reports nothing and returns
    /// immediately; the alternative is a feed round trip on the debounced publish path, which is a
    /// stall on every keystroke in a project file.
    /// </summary>
    [Fact]
    public void AColdCacheAnswersImmediatelyAndSaysNothing()
    {
        string path = Project();

        var stopwatch = Stopwatch.StartNew();
        var diagnostics = MsBuildDiagnosticsHandler.Compute(path);
        stopwatch.Stop();

        Assert.Empty(diagnostics);
        Assert.True(
            stopwatch.ElapsedMilliseconds < 500,
            $"a cold pass took {stopwatch.ElapsedMilliseconds}ms; it must not be waiting on a feed");
    }

    [Fact]
    public void AnOutdatedReferenceIsAHintOnTheVersionItself()
    {
        string path = Project("12.0.2");
        PackageStatusCache.Seed("Newtonsoft.Json", "12.0.2", Status(["12.0.2", "13.0.3"]));

        var diagnostic = Assert.Single(MsBuildDiagnosticsHandler.Compute(path));

        Assert.Equal(MsBuildDiagnosticCodes.OutdatedMajor, diagnostic.Code);
        Assert.Equal(4, diagnostic.Severity);
        Assert.Contains("13.0.3", diagnostic.Message, StringComparison.Ordinal);

        // On the version, not the whole element: the squiggle marks what a fix would replace.
        string text = File.ReadAllText(path);
        var lines = Microsoft.CodeAnalysis.Text.SourceText.From(text).Lines;
        int start = lines.GetPosition(new Microsoft.CodeAnalysis.Text.LinePosition(
            diagnostic.Range.Start.Line, diagnostic.Range.Start.Character));
        int end = lines.GetPosition(new Microsoft.CodeAnalysis.Text.LinePosition(
            diagnostic.Range.End.Line, diagnostic.Range.End.Character));

        Assert.Equal("12.0.2", text[start..end]);
    }

    [Theory]
    [InlineData("1.0.0", "1.0.1", "MSB-NUGET001")]
    [InlineData("1.0.0", "1.2.0", "MSB-NUGET002")]
    [InlineData("1.0.0", "2.0.0", "MSB-NUGET003")]
    public void HowFarBehindDecidesTheCode(string current, string newest, string expected)
    {
        string path = Project(current);
        PackageStatusCache.Seed("Newtonsoft.Json", current, Status([current, newest]));

        Assert.Equal(expected, Assert.Single(MsBuildDiagnosticsHandler.Compute(path)).Code);
    }

    [Fact]
    public void AVulnerableVersionCarriesTheAdvisoryAsALink()
    {
        string path = Project("12.0.2");
        PackageStatusCache.Seed("Newtonsoft.Json", "12.0.2", Status(
            ["12.0.2"],
            vulnerabilities: [new PackageVulnerabilityInfo(2, "https://github.com/advisories/GHSA-x")]));

        var diagnostic = Assert.Single(MsBuildDiagnosticsHandler.Compute(path));

        Assert.Equal(MsBuildDiagnosticCodes.Vulnerable, diagnostic.Code);
        Assert.Equal(2, diagnostic.Severity);
        Assert.Equal("https://github.com/advisories/GHSA-x", diagnostic.CodeDescription!.Href);
    }

    /// <summary>Critical is an error, because shipping it is not a judgement call.</summary>
    [Fact]
    public void ACriticalAdvisoryIsAnError()
    {
        string path = Project("12.0.2");
        PackageStatusCache.Seed("Newtonsoft.Json", "12.0.2", Status(
            ["12.0.2"],
            vulnerabilities: [new PackageVulnerabilityInfo(3, "https://example.invalid/a")]));

        Assert.Equal(1, Assert.Single(MsBuildDiagnosticsHandler.Compute(path)).Severity);
    }

    [Fact]
    public void ADeprecatedPackageIsTaggedAndNamesItsReplacement()
    {
        string path = Project("12.0.2");
        PackageStatusCache.Seed("Newtonsoft.Json", "12.0.2", Status(
            ["12.0.2"],
            deprecation: new PackageDeprecationInfo(["Legacy"], null, "System.Text.Json", null)));

        var diagnostic = Assert.Single(MsBuildDiagnosticsHandler.Compute(path));

        Assert.Equal(MsBuildDiagnosticCodes.Deprecated, diagnostic.Code);
        Assert.Contains("System.Text.Json", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(LspDiagnosticTag.Deprecated, Assert.Single(diagnostic.Tags!));
    }

    [Fact]
    public void AVersionNoFeedPublishesIsAnError()
    {
        string path = Project("99.0.0");
        PackageStatusCache.Seed("Newtonsoft.Json", "99.0.0", Status(["12.0.2", "13.0.3"], exists: false));

        var diagnostic = Assert.Single(MsBuildDiagnosticsHandler.Compute(path));

        Assert.Equal(MsBuildDiagnosticCodes.UnknownVersion, diagnostic.Code);
        Assert.Equal(1, diagnostic.Severity);
    }

    /// <summary>
    /// The highest-value test in the suite. A feed that did not answer — a private source behind a
    /// VPN, an expired credential, a laptop on a plane — must never be reported as a package that
    /// does not exist, because that puts a red error on a perfectly valid reference.
    /// </summary>
    [Fact]
    public void AFeedThatDidNotAnswerIsNeverReportedAsAMissingVersion()
    {
        string path = Project("99.0.0");
        PackageStatusCache.Seed(
            "Newtonsoft.Json", "99.0.0", Status(["12.0.2"], exists: false, healthy: false));

        Assert.Empty(MsBuildDiagnosticsHandler.Compute(path));
    }

    /// <summary>
    /// And nothing else is reported either. A half-answered lookup cannot say a package is up to
    /// date any more than it can say it is missing.
    /// </summary>
    [Fact]
    public void AnUnhealthyLookupReportsNothingAtAll()
    {
        string path = Project("1.0.0");
        PackageStatusCache.Seed(
            "Newtonsoft.Json", "1.0.0", Status(["1.0.0", "2.0.0"], healthy: false));

        Assert.Empty(MsBuildDiagnosticsHandler.Compute(path));
    }

    /// <summary>
    /// Someone on a stable version is not "behind" a release candidate. Reporting it would put a
    /// hint on every up-to-date reference to a package that publishes nightlies.
    /// </summary>
    [Fact]
    public void APrereleaseDoesNotMakeAStableVersionOutdated()
    {
        string path = Project("1.0.0");
        PackageStatusCache.Seed("Newtonsoft.Json", "1.0.0", Status(["1.0.0", "2.0.0-beta.1"]));

        Assert.Empty(MsBuildDiagnosticsHandler.Compute(path));
    }

    [Fact]
    public void AFloatingVersionIsNotComparedAgainstAnything()
    {
        string path = Project("12.*");
        PackageStatusCache.Seed("Newtonsoft.Json", "12.*", Status(["13.0.3"]));

        // A floating range is a decision, not a mistake, and no version comparison applies to it.
        Assert.Empty(MsBuildDiagnosticsHandler.Compute(path));
    }

    /// <summary>
    /// Under central package management the csproj carries no version, so there is nothing here to
    /// report on — the props file is where both the version and the diagnostic belong.
    /// </summary>
    [Fact]
    public void ACentrallyManagedReferenceReportsNothingInTheProject()
    {
        string path = Path.Combine(_directory, "Cpm.csproj");
        File.WriteAllText(path, """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" />
              </ItemGroup>
            </Project>
            """);
        MsBuildDocumentCache.Invalidate(path);

        Assert.Empty(MsBuildDiagnosticsHandler.Compute(path));
    }

    [Fact]
    public void APropsFileIsDiagnosedLikeAProject()
    {
        string path = Path.Combine(_directory, "Directory.Packages.props");
        File.WriteAllText(path, """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Newtonsoft.Json" Version="12.0.2" />
              </ItemGroup>
            </Project>
            """);
        MsBuildDocumentCache.Invalidate(path);

        PackageStatusCache.Seed("Newtonsoft.Json", "12.0.2", Status(["12.0.2", "13.0.3"]));

        Assert.Equal(
            MsBuildDiagnosticCodes.OutdatedMajor,
            Assert.Single(MsBuildDiagnosticsHandler.Compute(path)).Code);
    }

    [Fact]
    public void PackagesConfigIsRead()
    {
        string path = Path.Combine(_directory, "packages.config");
        File.WriteAllText(path, """
            <?xml version="1.0" encoding="utf-8"?>
            <packages>
              <package id="Newtonsoft.Json" version="12.0.2" targetFramework="net48" />
            </packages>
            """);
        MsBuildDocumentCache.Invalidate(path);

        PackageStatusCache.Seed("Newtonsoft.Json", "12.0.2", Status(["12.0.2", "13.0.3"]));

        Assert.Equal(
            MsBuildDiagnosticCodes.OutdatedMajor,
            Assert.Single(MsBuildDiagnosticsHandler.Compute(path)).Code);
    }

    [Fact]
    public void AMalformedBufferProducesNoDiagnosticsAndNoException()
    {
        string path = Path.Combine(_directory, "Broken.csproj");
        File.WriteAllText(path, "<Project>\n  <ItemGroup>\n    <PackageReference Include=\"New");
        MsBuildDocumentCache.Invalidate(path);

        Assert.Empty(MsBuildDiagnosticsHandler.Compute(path));
    }
}
