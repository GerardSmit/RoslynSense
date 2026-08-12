using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.MsBuild.Core;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// What the caret is on, which is the mapping every other feature in the pack is built on.
/// </summary>
/// <remarks>
/// The cases here are the ones a project file is actually edited through, and most of them are
/// states no finished document is ever in: a quote that has not been closed, a tag with no bracket,
/// a doubled <c>&lt;</c>. They are worth pinning individually because a wrong answer does not fail
/// loudly — it offers the wrong completions, or silently replaces the wrong characters.
/// </remarks>
public class MsBuildContextTests
{
    private const string Project = @"C:\src\App\App.csproj";

    /// <summary>Resolves the caret marked by <c>|</c>, which is removed from the text first.</summary>
    private static MsBuildContext At(string marked, string path = Project)
    {
        int caret = marked.IndexOf('|', StringComparison.Ordinal);
        Assert.True(caret >= 0, "the fixture has to mark the caret with |");

        string text = marked.Remove(caret, 1);
        var document = MsBuildDocumentCache.For(path, SourceText.From(text));
        return MsBuildContextResolver.Resolve(document, caret);
    }

    /// <summary>
    /// The headline case: a version being typed at the end of the file, with no closing quote and no
    /// closing bracket. The sibling <c>Include=</c> has to be readable, because it is what says
    /// which package's versions to offer.
    /// </summary>
    [Fact]
    public void AVersionBeingTypedResolvesWithItsPackageIdReadable()
    {
        var context = At("""
            <Project>
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="|
            """);

        Assert.True(context.Is(MsBuildLocationFlags.Attribute | MsBuildLocationFlags.Value));
        Assert.Equal("Version", context.AttributeName);
        Assert.Equal("PackageReference", context.ElementName);
        Assert.Equal("Newtonsoft.Json", context.Sibling("Include"));
        Assert.Equal("Project/ItemGroup/PackageReference", context.Path);

        // Nothing typed yet, so nothing to replace — but at a real position, not a default span.
        Assert.Equal(0, context.ReplaceSpan.Length);
    }

    /// <summary>
    /// A caret inside an existing value replaces the whole of it. Replacing only the prefix would
    /// accept <c>13.0.3</c> over <c>13.0</c> and leave <c>.1</c> behind.
    /// </summary>
    [Fact]
    public void ACaretInsideAValueReplacesAllOfIt()
    {
        const string text = """
            <Project>
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="13.0|.1" />
              </ItemGroup>
            </Project>
            """;

        var context = At(text);
        string source = text.Replace("|", string.Empty);

        Assert.True(context.Is(MsBuildLocationFlags.Attribute | MsBuildLocationFlags.Value));
        Assert.Equal("13.0.1", source.Substring(context.ReplaceSpan.Start, context.ReplaceSpan.Length));
    }

    [Fact]
    public void ACaretOnAnAttributeNameIsNotACaretOnItsValue()
    {
        var context = At("""
            <Project>
              <ItemGroup>
                <PackageReference Include="X" Ver|sion="1.0.0" />
              </ItemGroup>
            </Project>
            """);

        Assert.True(context.Is(MsBuildLocationFlags.Attribute | MsBuildLocationFlags.Name));
        Assert.False(context.Is(MsBuildLocationFlags.Value));
    }

    /// <summary>
    /// An empty line inside a <c>PropertyGroup</c> is where the next property is typed. Treating it
    /// as a gap rather than a position is the difference between the feature working and appearing
    /// not to.
    /// </summary>
    [Fact]
    public void WhitespaceInsideAnElementIsAPlaceToComplete()
    {
        var context = At("""
            <Project>
              <PropertyGroup>
                |
              </PropertyGroup>
            </Project>
            """);

        Assert.True(context.Is(MsBuildLocationFlags.Whitespace));
        Assert.Equal("PropertyGroup", context.ElementName);
        Assert.Equal("Project/PropertyGroup", context.Path);
    }

    [Fact]
    public void ACaretInsideAnElementsTextIsOnItsValue()
    {
        var context = At("""
            <Project>
              <PropertyGroup>
                <LangVersion>lat|est</LangVersion>
              </PropertyGroup>
            </Project>
            """);

        Assert.True(context.Is(MsBuildLocationFlags.Element | MsBuildLocationFlags.Value));
        Assert.Equal("LangVersion", context.ElementName);
        Assert.Equal("Project/PropertyGroup/LangVersion", context.Path);
    }

    /// <summary>
    /// A doubled <c>&lt;</c> is a real mid-typing state. The parser wraps the intended element in
    /// one it could not name, and completion has to answer about the inner one.
    /// </summary>
    [Fact]
    public void ADoubledAngleBracketStillResolvesToTheIntendedElement()
    {
        var context = At("""
            <Project>
              <ItemGroup>
                <<PackageReference Include="X" Version="|1.0.0" />
              </ItemGroup>
            </Project>
            """);

        Assert.True(context.Is(MsBuildLocationFlags.Invalid));
        Assert.Equal("PackageReference", context.ElementName);
        Assert.Equal("X", context.Sibling("Include"));
    }

    [Theory]
    [InlineData("<!-- <PackageReference Include=\"X|\" /> -->")]
    [InlineData("<![CDATA[ Version=\"1.0|.0\" ]]>")]
    public void NothingIsOfferedInsideACommentOrCData(string body)
    {
        var context = At($"<Project>\n  {body}\n</Project>");

        Assert.True(context.Is(MsBuildLocationFlags.Comment));
        Assert.Equal(MsBuildLocationFlags.Comment, context.Flags);
    }

    /// <summary>
    /// A condition is full of quotes and parentheses that look like other syntax. The caret mapping
    /// has to stay on the attribute it is actually in.
    /// </summary>
    [Fact]
    public void ANestedQuoteInAConditionDoesNotDerailTheMapping()
    {
        var context = At("""
            <Project>
              <PropertyGroup Condition="'$(Configuration)' == 'Deb|ug'">
                <Optimize>false</Optimize>
              </PropertyGroup>
            </Project>
            """);

        Assert.True(context.Is(MsBuildLocationFlags.Attribute | MsBuildLocationFlags.Value));
        Assert.Equal("Condition", context.AttributeName);
        Assert.Equal("PropertyGroup", context.ElementName);
    }

    /// <summary>
    /// The character that opened the completion list is already in the buffer and behind the caret,
    /// so a replacement starting at the caret would leave it there: accepting <c>net8.0</c> after
    /// typing <c>net8.</c> would give <c>net8.net8.0</c>. What prevents that is the replacement
    /// covering the whole value rather than the caret, which is worth pinning because the failure is
    /// silent — the list looks right and the accepted text is wrong.
    /// </summary>
    [Theory]
    [InlineData("<TargetFramework>net8.|", "net8.")]
    [InlineData("<TargetFramework>|", "")]
    public void ACompletionReplacesTheTriggerCharacterRatherThanAppendingAfterIt(
        string line, string expected)
    {
        string marked = $"<Project>\n  <PropertyGroup>\n    {line}";
        var context = At(marked);
        string source = marked.Replace("|", string.Empty);

        Assert.Equal(
            expected,
            source.Substring(context.ReplaceSpan.Start, context.ReplaceSpan.Length));

        // Which is to say: applying the edit yields the item, not the item glued onto the prefix.
        string applied = source
            .Remove(context.ReplaceSpan.Start, context.ReplaceSpan.Length)
            .Insert(context.ReplaceSpan.Start, "net8.0");

        Assert.EndsWith("net8.0", applied, StringComparison.Ordinal);
        Assert.DoesNotContain("net8.net8.0", applied, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both line endings, because a span that is right under one and wrong under the other is the
    /// classic way this breaks — and the fixtures a repository carries are whichever its authors'
    /// editors wrote.
    /// </summary>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void SpansAreCorrectUnderBothLineEndings(string newLine)
    {
        string text = string.Join(newLine,
            "<Project>",
            "  <ItemGroup>",
            "    <PackageReference Include=\"Serilog\" Version=\"2.0.0\" />",
            "  </ItemGroup>",
            "</Project>");

        int caret = text.IndexOf("2.0.0", StringComparison.Ordinal) + 2;
        var document = MsBuildDocumentCache.For(Project, SourceText.From(text));
        var context = MsBuildContextResolver.Resolve(document, caret);

        Assert.Equal("Version", context.AttributeName);
        Assert.Equal("2.0.0", text.Substring(context.ReplaceSpan.Start, context.ReplaceSpan.Length));
        Assert.Equal("Serilog", context.Sibling("Include"));
    }

    [Fact]
    public void AnEntityInASiblingValueIsDecoded()
    {
        var context = At("""
            <Project>
              <ItemGroup>
                <None Include="a&amp;b.txt" Condition="|" />
              </ItemGroup>
            </Project>
            """);

        // The span is raw and the value is decoded; a caller comparing this against a path on disk
        // needs the decoded one.
        Assert.Equal("a&b.txt", context.Sibling("Include"));
    }

    [Fact]
    public void AFileThePackDoesNotOwnResolvesToNothing()
    {
        Assert.Equal("None", MsBuildFile.KindOf(@"C:\src\Program.cs").ToString());
        Assert.Equal("None", MsBuildFile.KindOf(@"C:\src\web.config").ToString());
        Assert.Null(MsBuildDocumentCache.Get(@"C:\src\web.config"));
    }

    // Named rather than typed: the enums are internal to the pack, and a public [Theory] cannot
    // take an internal parameter.
    [Theory]
    [InlineData(@"C:\src\App.csproj", "Project", "CSharp")]
    [InlineData(@"C:\src\App.fsproj", "Project", "FSharp")]
    [InlineData(@"C:\src\App.vbproj", "Project", "VisualBasic")]
    [InlineData(@"C:\src\Directory.Packages.props", "Properties", "None")]
    [InlineData(@"C:\src\Build.targets", "Targets", "None")]
    [InlineData(@"C:\src\packages.config", "PackagesConfig", "None")]
    [InlineData(@"C:\src\NuGet.Config", "NuGetConfig", "None")]
    [InlineData(@"C:\src\nuget.config", "NuGetConfig", "None")]
    public void EachFileTypeIsClassifiedWithItsLanguage(string path, string kind, string flavour)
    {
        Assert.Equal(kind, MsBuildFile.KindOf(path).ToString());
        Assert.Equal(flavour, MsBuildFile.FlavourOf(path).ToString());
    }
}
