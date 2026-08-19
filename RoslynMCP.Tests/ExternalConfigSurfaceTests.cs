using System.Text;
using RoslynMCP.Languages;
using RoslynMCP.Languages.AppSettings;
using RoslynMCP.Languages.WebConfig.Core;
using RoslynMCP.Services.MetadataConfiguration;
using Xunit;
using LspRange = RoslynMCP.Lsp.Protocol.Range;
using Position = RoslynMCP.Lsp.Protocol.Position;

namespace RoslynMCP.Tests;

/// <summary>
/// What a reader is shown for a setting that only compiled code reads: the lens, the hover line,
/// and the completion entry offering a key the solution's own source will never mention.
/// </summary>
public class ExternalConfigSurfaceTests
{
    private static readonly LspRange s_range =
        new(new Position(3, 4), new Position(3, 12));

    private static MetadataConfigurationRead Read(
        string name, string assembly, MetadataConfigurationKind kind = MetadataConfigurationKind.Path) =>
        new(kind, name, name, assembly, $@"C:\packages\{assembly}.dll", assembly + ".Loader", "Load");

    // ---- The lens ----------------------------------------------------------------------------

    [Fact]
    public void OneExternalReadCountsAsOne()
    {
        var lens = ExternalReferences.Lens([Read("Kestrel", "Microsoft.AspNetCore")], "file:///a.json", s_range);

        Assert.NotNull(lens);
        Assert.Equal("1 external reference", lens!.Command!.Title);
        Assert.Equal("roslynSense.showExternalConfigReads", lens.Command.Name);

        // The click carries where it was clicked; the reads themselves need decompiling first.
        Assert.Equal(["file:///a.json", 3, 4], lens.Command.Arguments);
    }

    [Fact]
    public void SeveralReadsAreCountedAndTheLensSaysNothingMore()
    {
        var lens = ExternalReferences.Lens(
            [Read("Logging", "Microsoft.Extensions.Hosting"), Read("Logging", "Serilog")],
            "file:///a.json", s_range);

        Assert.Equal("2 external references", lens!.Command!.Title);
    }

    [Fact]
    public void NothingReadingASettingFromOutsideMeansNoLensAtAll()
    {
        // Deliberately unlike the reference count, where a zero is the finding: almost every key
        // in a file has no external reader, and a lens per line saying so is a wall of noise.
        Assert.Null(ExternalReferences.Lens([], "file:///a.json", s_range));
    }

    // ---- The hover ---------------------------------------------------------------------------

    [Fact]
    public void TheHoverNamesTheAssembliesOnceEach()
    {
        var builder = new StringBuilder();

        ExternalReferences.Append(builder, [
            Read("Logging", "Serilog"),
            Read("Logging", "Microsoft.Extensions.Hosting"),
            Read("Logging", "Serilog"),
        ]);

        string markdown = builder.ToString();

        Assert.Contains("Read by `Microsoft.Extensions.Hosting`, `Serilog`", markdown);
        Assert.Contains("no source in this solution", markdown);
    }

    [Fact]
    public void AHoverWithNoExternalReaderSaysNothing()
    {
        var builder = new StringBuilder();

        ExternalReferences.Append(builder, []);

        Assert.Equal("", builder.ToString());
    }

    // ---- Completion, web.config --------------------------------------------------------------

    private static WebConfigEntry Entry(string name, WebConfigSection section) =>
        new(name, "value", null, section, @"C:\site\web.config", default);

    private static ConfigSettingUsage Usage(string name, WebConfigSection section) =>
        new(name, section, @"C:\site\Reader.cs", default, default);

    [Fact]
    public void AKeyOnlyAPackageReadsIsOfferedWithThePackageNamed()
    {
        var wanted = WebConfigMetadataReads.Wanted(
            WebConfigSection.AppSettings,
            declared: [Entry("CdnRoot", WebConfigSection.AppSettings)],
            usages: [Usage("RetryCount", WebConfigSection.AppSettings)],
            markup: [],
            external:
            [
                Read("Timeout", "Contoso.Auth", MetadataConfigurationKind.AppSetting),
                Read("Main", "Contoso.Auth", MetadataConfigurationKind.ConnectionString),
            ]);

        Assert.Equal("read by this solution", wanted["RetryCount"]);
        Assert.Equal("read by Contoso.Auth", wanted["Timeout"]);

        // Declared already, and a connection string is not an appSettings key.
        Assert.DoesNotContain("CdnRoot", wanted.Keys);
        Assert.DoesNotContain("Main", wanted.Keys);
    }

    [Fact]
    public void AKeyBothTheSolutionAndAPackageReadIsOfferedOnce()
    {
        var wanted = WebConfigMetadataReads.Wanted(
            WebConfigSection.AppSettings,
            declared: [],
            usages: [Usage("Timeout", WebConfigSection.AppSettings)],
            markup: [],
            external: [Read("Timeout", "Contoso.Auth", MetadataConfigurationKind.AppSetting)]);

        // The solution's own read is the more useful attribution: it can be navigated to.
        Assert.Equal("read by this solution", Assert.Single(wanted).Value);
    }

    [Fact]
    public void ADeclaredKeyIsNeverOfferedHoweverManyThingsReadIt()
    {
        var wanted = WebConfigMetadataReads.Wanted(
            WebConfigSection.ConnectionStrings,
            declared: [Entry("Main", WebConfigSection.ConnectionStrings)],
            usages: [Usage("Main", WebConfigSection.ConnectionStrings)],
            markup: [Usage("Main", WebConfigSection.ConnectionStrings)],
            external: [Read("Main", "Contoso.Data", MetadataConfigurationKind.ConnectionString)]);

        Assert.Empty(wanted);
    }

    // ---- Completion, appsettings -------------------------------------------------------------

    [Theory]
    [InlineData("Kestrel", "", "Kestrel")]                       // a top-level section
    [InlineData("Logging:LogLevel:Default", "", "Logging")]      // deeper paths still start here
    [InlineData("Logging:LogLevel:Default", "Logging", "LogLevel")]
    [InlineData("Logging:LogLevel:Default", "Logging:LogLevel", "Default")]
    [InlineData("Kestrel", "Logging", null)]                     // a different subtree entirely
    [InlineData("Logging", "Logging", null)]                     // the section is not inside itself
    [InlineData("LoggingOther:Key", "Logging", null)]            // a prefix is not a parent
    public void OnlyThePathsInsideASectionOfferTheirNextSegment(
        string path, string sectionPath, string? expected) =>
        Assert.Equal(expected, AppSettingsLanguage.NextSegment(path, sectionPath));
}
