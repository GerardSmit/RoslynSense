using RoslynMCP.Languages.MsBuild.Core;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// That the vendored MSBuild documentation is actually in the assembly and actually parses.
/// </summary>
/// <remarks>
/// The failure this guards is silent: the loader reports a missing resource to stderr and returns
/// an empty corpus, so a build that forgot to embed the JSON has completion that simply knows
/// nothing — no exception, no error, just a list that never appears.
/// </remarks>
public class MsBuildSchemaHelpTests
{
    [Fact]
    public void TheEmbeddedCorpusLoads()
    {
        // Counts rather than exact numbers: the corpus is upstream's and a refresh moves them.
        Assert.True(MsBuildSchemaHelp.Properties.Count() > 100);
        Assert.True(MsBuildSchemaHelp.Items.Count() > 10);
    }

    [Fact]
    public void APropertyCarriesItsDescriptionAndItsValues()
    {
        var entry = MsBuildSchemaHelp.Property("AllowUnsafeBlocks");

        Assert.NotNull(entry);
        Assert.NotEmpty(entry!.Description);
        Assert.Equal(["true", "false"], entry.DefaultValues.ToArray());
    }

    /// <summary>
    /// The corpus is what makes value completion general. A property nobody wrote a case for still
    /// offers its values, because upstream recorded them.
    /// </summary>
    [Fact]
    public void APropertyNoOneHandWroteStillOffersItsValues()
    {
        var values = MsBuildWellKnownValues.For("AllowUnsafeBlocks", MsBuildFlavour.CSharp);

        Assert.Equal(["true", "false"], values.Select(v => v.Value));
    }

    [Fact]
    public void ItemMetadataIncludesWhatEveryItemTypeCarries()
    {
        var metadata = MsBuildSchemaHelp.Metadata("Compile");

        // The "*" entry, merged in for every item type.
        Assert.Contains("Identity", metadata.Keys);
        Assert.Contains("Filename", metadata.Keys);
    }

    /// <summary>
    /// <c>Condition</c> is legal on nearly every element, and the corpus says so once with a
    /// wildcard rather than repeating itself per element.
    /// </summary>
    [Fact]
    public void AWildcardAttributeResolvesOnAnyElement()
    {
        Assert.NotNull(MsBuildSchemaHelp.Element("PropertyGroup", "Condition"));
        Assert.NotNull(MsBuildSchemaHelp.Element("ItemGroup", "Condition"));
        Assert.NotNull(MsBuildSchemaHelp.Element("Target", "Condition"));
    }

    [Fact]
    public void LangVersionComesFromTheCompilerThisServerReferences()
    {
        var values = MsBuildWellKnownValues.For("LangVersion", MsBuildFlavour.CSharp);
        var names = values.Select(v => v.Value).ToList();

        Assert.Equal("latest", names[0]);
        Assert.Contains("preview", names);

        // Generated from Roslyn's own enum, so the spelling is the one the compiler accepts and an
        // upgrade extends the list without anyone editing it.
        Assert.Contains("13.0", names);
        Assert.Contains("7.3", names);

        // The prose is hand-written beside it.
        Assert.Contains(values, v => v.Value == "9.0" && v.Documentation!.Contains("Records"));
    }

    /// <summary>
    /// The gate that stops the C# list being offered where it is wrong. A `.vbproj` has a
    /// LangVersion too, and these are not its values.
    /// </summary>
    [Theory]
    [InlineData("FSharp")]
    [InlineData("VisualBasic")]
    [InlineData("None")]
    public void LangVersionIsOfferedOnlyForCSharp(string flavour)
    {
        var parsed = Enum.Parse<MsBuildFlavour>(flavour);

        Assert.Empty(MsBuildWellKnownValues.For("LangVersion", parsed));
        Assert.NotEmpty(MsBuildWellKnownValues.For("LangVersion", MsBuildFlavour.CSharp));
    }

    [Fact]
    public void TargetFrameworksAreOfferedNewestFirst()
    {
        var values = MsBuildWellKnownValues.For("TargetFramework", MsBuildFlavour.CSharp);
        var names = values.Select(v => v.Value).ToList();

        Assert.Contains("net8.0", names);
        Assert.Contains("netstandard2.0", names);
        Assert.True(names.IndexOf("net10.0") < names.IndexOf("net8.0"));

        // The same list for the plural property, which takes several of them.
        Assert.Equal(names, MsBuildWellKnownValues.For("TargetFrameworks", MsBuildFlavour.CSharp).Select(v => v.Value));
    }

    [Fact]
    public void APropertyWithNoFixedSetOffersNothing()
    {
        // A version, a path or free text: a list here would be a list of wrong guesses.
        Assert.Empty(MsBuildWellKnownValues.For("VersionPrefix", MsBuildFlavour.CSharp));
        Assert.Empty(MsBuildWellKnownValues.For("NotAPropertyAnyoneDefined", MsBuildFlavour.CSharp));
    }
}
