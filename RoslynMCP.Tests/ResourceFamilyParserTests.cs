using System.Collections.Immutable;
using RoslynMCP.Languages.Resources;
using RoslynMCP.Languages.Resources.Core;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Which <c>.resx</c> files belong together, decided from names alone.
/// </summary>
/// <remarks>
/// The trap this guards is that <see cref="System.Globalization.CultureInfo.GetCultureInfo(string)"/>
/// throwing is not a usable signal: on .NET with ICU any well-formed unknown subtag comes back as a
/// synthesized neutral culture, so a lone name is happy to decompose into a family that does not
/// exist. The golden case is a real DNN checkout, because a corpus nobody curated is the only one
/// that contains the names nobody thought of.
/// </remarks>
public class ResourceFamilyParserTests
{
    private static ImmutableArray<ResourceOverrideRule> Overrides =>
        ResourceDiscoveryOptions.Default.Overrides;

    /// <summary>Pins the corpus. A checkout with a different count is a different golden.</summary>
    private const int DnnResxCount = 202;

    [DnnPlatformFact]
    public void TheRealDnnFileNamesDecomposeIntoTheFamiliesItsRuntimeReads()
    {
        var files = Directory
            .EnumerateFiles(DnnPlatform.Directory!, "*.resx", SearchOption.AllDirectories)
            .ToList();

        Assert.True(
            files.Count == DnnResxCount,
            $"The golden corpus is pinned at {DnnResxCount} .resx files and this checkout has "
            + $"{files.Count}; the expectations below were written against the pinned set.");

        var families = files
            .GroupBy(file => Path.GetDirectoryName(file)!, StringComparer.OrdinalIgnoreCase)
            .SelectMany(directory => ResourceFamilyParser.Decompose(directory.Key, [.. directory], Overrides))
            .ToList();

        // Every file is in exactly one family. A grouping that loses a file loses the resource it
        // declares, and one that duplicates it renames the same key twice.
        Assert.Equal(
            files.Order(StringComparer.OrdinalIgnoreCase),
            families.SelectMany(f => f.Files).Select(f => f.FilePath).Order(StringComparer.OrdinalIgnoreCase));

        // The only cultures in two hundred names. Not `ascx`, not `aspx`, not `template` — each of
        // which is a subtag ICU will invent a culture for on request.
        Assert.Equal(
            ["de-DE", "es-ES", "fr-FR", "it-IT", "nl-NL", "pl-PL", "ru-RU", "tr-TR"],
            families
                .SelectMany(f => f.Files)
                .Select(f => f.Culture?.Name)
                .OfType<string>()
                .Distinct()
                .Order(StringComparer.Ordinal));

        // `Settings.ascx.resx` is a file called Settings.ascx, and `View.ascx.resx` is one called
        // View.ascx — neither is a translation of a shorter name nobody wrote.
        Assert.Contains(families, f => f.BaseName == "View.ascx");
        Assert.DoesNotContain(families, f => f.BaseName == "View");

        // A lone `X.en-US.resx` is the same story from the other side: one file agreeing with
        // nothing is a base, because a phantom family is worse than a missed one.
        var template = Assert.Single(families, f => f.BaseName == "Blank Website.template.en-US");
        Assert.Null(Assert.Single(template.Files).Culture);

        var translated = families
            .Where(f => f.Files.Length > 1)
            .OrderBy(f => f.BaseName, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            ["Browser.aspx", "EditorConfigManager.ascx", "Installwizard.aspx", "Options.aspx", "UpgradeWizard.aspx"],
            translated.Select(f => f.BaseName));

        Assert.Equal(
            new string?[] { null, "de-DE", "pl-PL", "ru-RU" },
            Assert.Single(translated, f => f.BaseName == "Browser.aspx")
                .Files.Select(f => f.Culture?.Name));

        Assert.Equal(
            new string?[] { null, "de-DE", "es-ES", "fr-FR", "it-IT", "nl-NL", "tr-TR" },
            Assert.Single(translated, f => f.BaseName == "Installwizard.aspx")
                .Files.Select(f => f.Culture?.Name));

        // DNN ships no customizations of its own — those are written per installation — so every
        // file in the platform is rank 0. A rank appearing here means the pattern matched a name.
        Assert.All(families.SelectMany(f => f.Files), f => Assert.Equal(0, f.OverrideRank));
    }

    [Fact]
    public void AMiddleSegmentIsNotACultureJustBecauseIcuWillInventOneForIt()
    {
        const string directory = @"C:\site\Resources";

        var families = ResourceFamilyParser.Decompose(
            directory,
            [
                Path.Combine(directory, "My.Company.resx"),
                Path.Combine(directory, "My.Company.nl-NL.resx"),
                Path.Combine(directory, "My.Company.Strings.resx"),
            ],
            Overrides);

        // `GetCultureInfo("Company")` answers with a synthesized neutral culture rather than
        // throwing, so the shape rule and the tail parse are the only things standing between
        // `My.Company.Strings` and a translation of `My` into a language that does not exist.
        Assert.Equal(["My.Company", "My.Company.Strings"], families.Select(f => f.BaseName));

        Assert.Equal(
            new string?[] { null, "nl-NL" }, families[0].Files.Select(f => f.Culture?.Name));
        Assert.Null(Assert.Single(families[1].Files).Culture);
    }

    [Fact]
    public void TheFixturesFiveFileFamilyOrdersItselfTheWayTheRuntimeProbesIt()
    {
        var family = ResourceDocuments.FamilyOf(FixturePaths.LocalizedResxFile, Overrides);

        Assert.NotNull(family);
        Assert.Equal("Localized.aspx", family!.BaseName);

        // Neutral, then translations by name, then customizations by rank — and within a rank the
        // uncustomized language before a translated one.
        Assert.Equal(
            [
                "Localized.aspx.resx",
                "Localized.aspx.nl-NL.resx",
                "Localized.aspx.Host.resx",
                "Localized.aspx.Portal-3.resx",
                "Localized.aspx.nl-NL.Portal-3.resx",
            ],
            family.Files.Select(f => Path.GetFileName(f.FilePath)));

        Assert.Equal(
            new string?[] { null, "nl-NL", null, null, "nl-NL" },
            family.Files.Select(f => f.Culture?.Name));
        Assert.Equal([0, 0, 1, 2, 2], family.Files.Select(f => f.OverrideRank));
        Assert.Equal(
            new string?[] { null, null, "Host", "Portal-3", "Portal-3" },
            family.Files.Select(f => f.OverrideTag));

        // The two other base names in the same folder stayed out of it: a family is every file
        // sharing a base name, not every file sharing a directory.
        Assert.Null(ResourceDocuments.Member(family, FixturePaths.DefaultAspxResxFile));
        Assert.Null(ResourceDocuments.Member(family, FixturePaths.SharedResourcesResxFile));
    }
}
