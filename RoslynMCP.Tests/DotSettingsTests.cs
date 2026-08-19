using RoslynMCP.Languages.DotSettings.Core;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The escaping every value inside a settings key goes through.
/// </summary>
public class DotSettingsEscapingTests
{
    [Theory]
    [InlineData("Common_002EDotSettings", "Common.DotSettings")]
    [InlineData("controllers_005Cresults", @"controllers\results")]
    [InlineData("Zapto_002EApi_003B_002A", "Zapto.Api;*")]
    [InlineData("App_005FModule", "App_Module")]
    [InlineData("plain", "plain")]
    public void DecodesTheFormsRealFilesUse(string encoded, string expected) =>
        Assert.Equal(expected, DotSettingsEscaping.Decode(encoded));

    [Theory]
    [InlineData(@"d:Services\f:Impl.cs")]
    [InlineData("A;*;B;*")]
    [InlineData("under_score")]
    public void RoundTrips(string text) =>
        Assert.Equal(text, DotSettingsEscaping.Decode(DotSettingsEscaping.Encode(text)));

    /// <summary>
    /// The underscore encodes itself, so a decoder that stopped at the first underscore would turn
    /// <c>App_Module</c> into two names. This is the case that catches that.
    /// </summary>
    [Fact]
    public void EncodesTheUnderscoreItself() =>
        Assert.Equal("a_005Fb", DotSettingsEscaping.Encode("a_b"));

    /// <summary>A merge artifact costs one unreadable name, not the file.</summary>
    [Fact]
    public void PassesThroughATruncatedEscape() =>
        Assert.Equal("tail_00", DotSettingsEscaping.Decode("tail_00"));
}

/// <summary>Key paths taken apart: pure text in, entries out. No files.</summary>
public class DotSettingsReaderTests
{
    [Fact]
    public void ParsesAScalarEntry()
    {
        var entry = Assert.NotNull(
            DotSettingsReader.Parse("/Default/CodeInspection/CSharpLanguageProject/LanguageLevel/@EntryValue"));

        Assert.Equal("CodeInspection/CSharpLanguageProject/LanguageLevel", entry.Path);
        Assert.Null(entry.Index);
        Assert.Equal("EntryValue", entry.Accessor);
    }

    [Fact]
    public void ParsesAnIndexedEntryAndDecodesItsIndex()
    {
        var entry = Assert.NotNull(DotSettingsReader.Parse(
            "/Default/CodeInspection/NamespaceProvider/NamespaceFoldersToSkip/=controllers_005Cresults/@EntryIndexedValue"));

        Assert.Equal("CodeInspection/NamespaceProvider/NamespaceFoldersToSkip", entry.Path);
        Assert.Equal(@"controllers\results", entry.Index);
    }

    /// <summary>
    /// An index can sit in the middle of a path — <c>/CodeStyle/Generate/=DisposePattern/Options</c>
    /// — so the path is not simply everything before the first <c>=</c>.
    /// </summary>
    [Fact]
    public void KeepsPathSegmentsThatFollowAnIndex()
    {
        var entry = Assert.NotNull(
            DotSettingsReader.Parse("/Default/CodeStyle/Generate/=DisposePattern/Options/@EntryValue"));

        Assert.Equal("CodeStyle/Generate/Options", entry.Path);
        Assert.Equal("DisposePattern", entry.Index);
    }

    [Fact]
    public void IgnoresAKeyNotRootedAtDefault() =>
        Assert.Null(DotSettingsReader.Parse("/Custom/Whatever/@EntryValue"));

    [Fact]
    public void ReadsEntriesOutOfTheResourceDictionary()
    {
        var entries = DotSettingsReader.Read(Layer(
            """<s:Boolean x:Key="/Default/CodeInspection/NamespaceProvider/NamespaceFoldersToSkip/=extensions/@EntryIndexedValue">True</s:Boolean>"""));

        var entry = Assert.Single(entries);
        Assert.Equal("extensions", entry.Index);
        Assert.Equal("True", entry.Value);
        Assert.True(entry.IsPresentIndex);
    }

    /// <summary>A conflict marker costs the layer, not the solution.</summary>
    [Fact]
    public void ReturnsNothingForMalformedXml() =>
        Assert.Empty(DotSettingsReader.Read("<wpf:ResourceDictionary><<<"));

    internal static string Layer(params string[] entries) =>
        """
        <wpf:ResourceDictionary xml:space="preserve"
            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
            xmlns:s="clr-namespace:System;assembly=mscorlib"
            xmlns:wpf="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
        """
        + string.Join('\n', entries)
        + "</wpf:ResourceDictionary>";
}

/// <summary>
/// What a resolved stack does to the answers RoslynSense gives, exercised through real files on
/// disk because layering and invalidation are the parts worth testing.
/// </summary>
public class ReSharperSettingsTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("dotsettings-tests-").FullName;

    private readonly string _projectPath;

    public ReSharperSettingsTests()
    {
        _projectPath = Path.Combine(_root, "Acme.Api", "Acme.Api.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(_projectPath)!);
        File.WriteAllText(_projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        ReSharperSettings.Clear();
    }

    public void Dispose()
    {
        ReSharperSettings.Clear();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private void WriteProjectLayer(params string[] entries) =>
        File.WriteAllText(_projectPath + ".DotSettings", DotSettingsReaderTests.Layer(entries));

    private void WritePersonalProjectLayer(params string[] entries) =>
        File.WriteAllText(_projectPath + ".DotSettings.user", DotSettingsReaderTests.Layer(entries));

    private static string SkipFolder(string encoded) =>
        $"""<s:Boolean x:Key="/Default/CodeInspection/NamespaceProvider/NamespaceFoldersToSkip/={encoded}/@EntryIndexedValue">True</s:Boolean>""";

    [Fact]
    public void NoLayersMeansEveryFolderIsANamespace()
    {
        var settings = ReSharperSettings.ForProject(_projectPath);

        Assert.True(settings.IsEmpty);
        Assert.Equal(["Api", "Extensions"], settings.NamespaceSegments(@"Api\Extensions"));
    }

    [Fact]
    public void DropsAMarkedFolderFromTheNamespace()
    {
        WriteProjectLayer(SkipFolder("extensions"));

        Assert.Equal(
            ["Api"],
            ReSharperSettings.ForProject(_projectPath).NamespaceSegments(@"Extensions\Api"));
    }

    /// <summary>
    /// The stored value is the folder's whole project-relative path, so marking <c>Extensions</c>
    /// at the root must leave <c>Api\Extensions</c> alone. Matching on the segment name would fail
    /// this, and would quietly move types out of the namespace they are declared in.
    /// </summary>
    [Fact]
    public void MatchesTheWholePrefixRatherThanTheSegmentName()
    {
        WriteProjectLayer(SkipFolder("extensions"));

        Assert.Equal(
            ["Api", "Extensions"],
            ReSharperSettings.ForProject(_projectPath).NamespaceSegments(@"Api\Extensions"));
    }

    [Fact]
    public void DropsANestedFolderNamedByItsFullPath()
    {
        WriteProjectLayer(SkipFolder("api_005Cextensions"));

        Assert.Equal(
            ["Api"],
            ReSharperSettings.ForProject(_projectPath).NamespaceSegments(@"Api\Extensions"));
    }

    [Fact]
    public void AFileAtTheProjectRootHasNoSegments()
    {
        WriteProjectLayer(SkipFolder("extensions"));

        Assert.Empty(ReSharperSettings.ForProject(_projectPath).NamespaceSegments("."));
    }

    /// <summary>
    /// The personal layer sits over the team-shared one, and a removal is written as a real entry
    /// rather than an absence — otherwise a stronger layer could only add.
    /// </summary>
    [Fact]
    public void ThePersonalLayerCanTakeBackWhatTheSharedOneAdded()
    {
        WriteProjectLayer(SkipFolder("extensions"));
        WritePersonalProjectLayer(
            """<s:Boolean x:Key="/Default/CodeInspection/NamespaceProvider/NamespaceFoldersToSkip/=extensions/@EntryIndexedValue">False</s:Boolean>""");

        Assert.Equal(
            ["Extensions"],
            ReSharperSettings.ForProject(_projectPath).NamespaceSegments("Extensions"));
    }

    [Fact]
    public void RereadsALayerAfterItChanges()
    {
        WriteProjectLayer(SkipFolder("extensions"));
        Assert.Empty(ReSharperSettings.ForProject(_projectPath).NamespaceSegments("Extensions"));

        // A second write inside the same tick would not move LastWriteTimeUtc on every filesystem.
        File.SetLastWriteTimeUtc(_projectPath + ".DotSettings", DateTime.UtcNow.AddSeconds(1));
        WriteProjectLayer(SkipFolder("something_002Delse"));
        File.SetLastWriteTimeUtc(_projectPath + ".DotSettings", DateTime.UtcNow.AddSeconds(2));

        Assert.Equal(
            ["Extensions"],
            ReSharperSettings.ForProject(_projectPath).NamespaceSegments("Extensions"));
    }

    [Fact]
    public void SkipsAFileMatchingAMask()
    {
        WriteProjectLayer(
            """<s:Boolean x:Key="/Default/CodeInspection/ExcludedFiles/FileMasksToSkip/=_002A_002EDesigner_002Ecs/@EntryIndexedValue">True</s:Boolean>""");

        var settings = ReSharperSettings.ForProject(_projectPath);

        Assert.True(settings.IsExcluded(Path.Combine(_root, "Acme.Api", "Form.Designer.cs")));
        Assert.False(settings.IsExcluded(Path.Combine(_root, "Acme.Api", "Form.cs")));
    }

    [Theory]
    // Module, namespace and type all translate.
    [InlineData("Zapto_002EApi_003BZapto_002EGen_003BFoo_003B_002A", "[Zapto.Api]Zapto.Gen.Foo")]
    // A wildcard namespace still has to produce a type filter coverlet accepts.
    [InlineData("Zapto_002EApi_003B_002A_003BFoo_003B_002A", "[Zapto.Api]*.Foo")]
    public void TranslatesACoverageExclusionCoverletCanExpress(string encoded, string expected)
    {
        WriteProjectLayer(
            $"""<s:Boolean x:Key="/Default/Environment/Filtering/ExcludeCoverageFilters/={encoded}/@EntryIndexedValue">True</s:Boolean>""");

        Assert.Equal(
            expected,
            Assert.Single(ReSharperSettings.ForProject(_projectPath).CoverletExcludeFilters));
    }

    /// <summary>
    /// coverlet cannot exclude a single method, and widening the filter to the whole type would
    /// hide more code than the team asked to hide — which moves a coverage number upward.
    /// </summary>
    [Fact]
    public void DropsAMethodLevelCoverageExclusionRatherThanWideningIt()
    {
        WriteProjectLayer(
            """<s:Boolean x:Key="/Default/Environment/Filtering/ExcludeCoverageFilters/=Zapto_002EApi_003B_002A_003BFoo_003BBar/@EntryIndexedValue">True</s:Boolean>""");

        var settings = ReSharperSettings.ForProject(_projectPath);

        Assert.Single(settings.CoverageExclusions);
        Assert.Empty(settings.CoverletExcludeFilters);
    }

    [Fact]
    public void ResolvesAnExcludedFileThroughItsProjectGuid()
    {
        const string guid = "155A78F7-41F0-40CE-835B-0F7C74E60CE0";

        File.WriteAllText(Path.Combine(_root, "Acme.sln"),
            "Microsoft Visual Studio Solution File, Format Version 12.00\n"
            + $"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"Acme.Api\", "
            + $"\"Acme.Api\\Acme.Api.csproj\", \"{{{guid}}}\"\nEndProject\n");

        string encoded = DotSettingsEscaping.Encode($"{guid}/d:Services/f:Impl.cs");
        WriteProjectLayer(
            $"""<s:Boolean x:Key="/Default/CodeInspection/ExcludedFiles/FilesAndFoldersToSkip2/={encoded}/@EntryIndexedValue">True</s:Boolean>""");

        var settings = ReSharperSettings.ForProject(_projectPath);

        Assert.True(settings.IsExcluded(Path.Combine(_root, "Acme.Api", "Services", "Impl.cs")));
        Assert.False(settings.IsExcluded(Path.Combine(_root, "Acme.Api", "Services", "Other.cs")));
    }

    /// <summary>A spec may stop at a folder, and then everything under it goes with it.</summary>
    [Fact]
    public void ExcludingAFolderExcludesWhatIsUnderIt()
    {
        const string guid = "155A78F7-41F0-40CE-835B-0F7C74E60CE0";

        File.WriteAllText(Path.Combine(_root, "Acme.sln"),
            "Microsoft Visual Studio Solution File, Format Version 12.00\n"
            + $"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"Acme.Api\", "
            + $"\"Acme.Api\\Acme.Api.csproj\", \"{{{guid}}}\"\nEndProject\n");

        string encoded = DotSettingsEscaping.Encode($"{guid}/d:Generated");
        WriteProjectLayer(
            $"""<s:Boolean x:Key="/Default/CodeInspection/ExcludedFiles/FilesAndFoldersToSkip2/={encoded}/@EntryIndexedValue">True</s:Boolean>""");

        Assert.True(ReSharperSettings.ForProject(_projectPath)
            .IsExcluded(Path.Combine(_root, "Acme.Api", "Generated", "Deep", "File.cs")));
    }

    /// <summary>A GUID no solution claims is a project that was removed, and names nothing.</summary>
    [Fact]
    public void IgnoresASpecWhoseProjectIsGone()
    {
        File.WriteAllText(Path.Combine(_root, "Acme.sln"),
            "Microsoft Visual Studio Solution File, Format Version 12.00\n");

        string encoded = DotSettingsEscaping.Encode(
            "00000000-0000-0000-0000-000000000000/d:Services/f:Impl.cs");
        WriteProjectLayer(
            $"""<s:Boolean x:Key="/Default/CodeInspection/ExcludedFiles/FilesAndFoldersToSkip2/={encoded}/@EntryIndexedValue">True</s:Boolean>""");

        Assert.False(ReSharperSettings.ForProject(_projectPath)
            .IsExcluded(Path.Combine(_root, "Acme.Api", "Services", "Impl.cs")));
    }
}
