using RoslynMCP.Config;
using RoslynMCP.Languages.Resources;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The configured form of a key an application composes rather than writes out:
/// <c>Header[Control.UniqueName].Text</c>.
/// </summary>
/// <remarks>
/// A setting rather than two hard-coded shapes because the two that prompted it are not the only
/// two. A codebase that puts its fixed part after the attribute writes <c>[Control.ID].Header</c>,
/// and one that composes on both sides writes both — neither is expressible as a prefix, which is
/// what the first cut of this was.
/// </remarks>
public class ResourceMarkupBindingTests
{
    [Theory]
    [InlineData("[Control.ID].Text", "", "ID", ".Text")]
    [InlineData("Header[Control.UniqueName].Text", "Header", "UniqueName", ".Text")]
    [InlineData("[Control.UniqueID].Header", "", "UniqueID", ".Header")]
    [InlineData("[Control.ID]", "", "ID", "")]
    // The Control. in front is how the shape reads to someone who has one of these, and dropping
    // it silently is friendlier than rejecting a pattern over punctuation.
    [InlineData("[ID].Text", "", "ID", ".Text")]
    public void APatternIsTheKeyItProducesWithTheAttributeInTheMiddle(
        string pattern, string prefix, string attribute, string suffix)
    {
        var binding = ResourceMarkupBinding.Parse(pattern, out string? problem);

        Assert.Null(problem);
        Assert.NotNull(binding);
        Assert.Equal(prefix, binding!.Prefix);
        Assert.Equal(attribute, binding.Attribute);
        Assert.Equal(suffix, binding.Suffix);
    }

    [Theory]
    [InlineData("litStock.Text", "attribute names no")]
    [InlineData("[Control.ID][Control.Name].Text", "more than one")]
    [InlineData("[].Text", "is empty")]
    public void APatternThatNamesNoOneAttributeSaysWhy(string pattern, string _)
    {
        Assert.Null(ResourceMarkupBinding.Parse(pattern, out string? problem));
        Assert.NotNull(problem);
    }

    /// <summary>What each pattern would have had to read for a given key to be its own.</summary>
    [Theory]
    [InlineData("[Control.ID].Text", "litStock.Text", "litStock")]
    [InlineData("Header[Control.UniqueName].Text", "HeaderAmount.Text", "Amount")]
    // The prefix is there and the suffix is not, which is the case a prefix-only rule got wrong.
    [InlineData("Header[Control.UniqueName].Text", "HeaderAmount.ToolTip", null)]
    [InlineData("[Control.ID].Text", "Heading", null)]
    // Nothing between the fixed parts is nothing to look for, not an empty id that matches every
    // attribute written out in full.
    [InlineData("[Control.ID].Text", ".Text", null)]
    public void TheMiddleIsWhatIsLeftWhenBothEndsMatch(string pattern, string key, string? middle)
    {
        var binding = ResourceMarkupBinding.Parse(pattern, out _);

        Assert.NotNull(binding);
        Assert.Equal(middle, binding!.Middle(key));
    }

    /// <summary>
    /// The shipped set, held to the spelling the documentation gives it.
    /// </summary>
    /// <remarks>
    /// The presets construct these directly rather than parsing, because a preset that failed to
    /// parse would fail in a static initializer and take the process with it. This is what keeps
    /// the two forms from drifting: what the README tells someone to write has to produce what
    /// ships.
    /// </remarks>
    [Fact]
    public void ThePresetShipsTheFourShapesTheDocumentationSpells()
    {
        var warnings = new List<string>();

        var shipped = ResourceSettings.Resolve(
            enabled: true, new ResourcesConfig { Preset = "webforms" }, warnings).MarkupBindings;

        Assert.Empty(warnings);

        var written = new[]
        {
            "[Control.ID].Text",
            "[Control.ID].ToolTip",
            "Header[Control.UniqueName].Text",
            "Header[Control.Name].Text",
        }.Select(p => ResourceMarkupBinding.Parse(p, out _));

        Assert.Equal(written, shipped);
    }

    /// <summary>
    /// DNN's own two, on top of the four. Both were found by counting keys in a DNN site: 2160
    /// <c>.Help</c> and 486 <c>.Header</c> that no call site in the solution mentions.
    /// </summary>
    [Fact]
    public void TheDnnPresetAddsTheTwoShapesItsOwnControlsCompose()
    {
        var warnings = new List<string>();

        var shipped = ResourceSettings.Resolve(
            enabled: true, new ResourcesConfig { Preset = "dnn" }, warnings).MarkupBindings;

        Assert.Empty(warnings);

        // A dnn:label asks for its caption and its help text under the same ID.
        Assert.Contains(ResourceMarkupBinding.Parse("[Control.ID].Help", out _), shipped);

        // A bound column has no UniqueName to be found by, so the field it binds is the id.
        Assert.Contains(ResourceMarkupBinding.Parse("[Control.DataField].Header", out _), shipped);

        // And the four every WebForms page has are still there.
        Assert.Contains(ResourceMarkupBinding.Parse("[Control.ID].Text", out _), shipped);
    }

    /// <summary>Stock WebForms has neither convention, and inventing them would report a control
    /// for a key its framework never composes.</summary>
    [Fact]
    public void TheWebFormsPresetDoesNotClaimDnnSShapes()
    {
        var shipped = ResourceSettings.Resolve(
            enabled: true, new ResourcesConfig { Preset = "webforms" }, new List<string>())
            .MarkupBindings;

        Assert.DoesNotContain(ResourceMarkupBinding.Parse("[Control.ID].Help", out _), shipped);
    }

    [Fact]
    public void AConfiguredPatternLayersOntoThePresetAndAMalformedOneIsDropped()
    {
        var warnings = new List<string>();

        var settings = ResourceSettings.Resolve(
            enabled: true,
            new ResourcesConfig
            {
                Preset = "webforms",
                MarkupBindings = ["[Control.ID].Header", "litStock.Text"],
            },
            warnings);

        // Dropped and explained, rather than failing the load: one typo must not leave the solution
        // with no navigation at all.
        Assert.Contains("litStock.Text", Assert.Single(warnings), StringComparison.Ordinal);

        Assert.Contains(
            ResourceMarkupBinding.Parse("[Control.ID].Header", out _), settings.MarkupBindings);

        // Layered, not replaced.
        Assert.Contains(
            ResourceMarkupBinding.Parse("[Control.ID].Text", out _), settings.MarkupBindings);
    }

    /// <summary>The presets overlap, and a duplicate would report the same attribute twice.</summary>
    [Fact]
    public void MergingEveryPresetDoesNotShipTheSameShapeTwice()
    {
        var bindings = ResourceSettings
            .Resolve(enabled: true, new ResourcesConfig(), new List<string>())
            .MarkupBindings;

        Assert.NotEmpty(bindings);
        Assert.Equal(bindings.Length, bindings.Distinct().Count());
    }
}
