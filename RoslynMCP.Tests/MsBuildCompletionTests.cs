using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.MsBuild.Core;
using RoslynMCP.Languages.MsBuild.Lsp;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// What the pack offers where, for every site that answers without touching a feed.
/// </summary>
/// <remarks>
/// The feed-backed sites — package ids and versions — are covered by
/// <see cref="MsBuildPackageCompletionTests"/>, which needs a fixture feed. Split because these
/// have to keep passing on a machine with no network, and because the isolation between the two
/// halves is itself a thing worth asserting.
/// </remarks>
[Collection(SharedState.Name)]
public class MsBuildCompletionTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "roslynsense-msbuild-" + Guid.NewGuid().ToString("N")[..8]);

    private string Write(string name, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_directory, name))!);
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
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

    /// <summary>Completes at the <c>|</c> in the given project text.</summary>
    private async Task<CompletionList?> CompleteAsync(string marked, string name = "App.csproj")
    {
        int caret = marked.IndexOf('|', StringComparison.Ordinal);
        Assert.True(caret >= 0, "the fixture has to mark the caret with |");

        string text = marked.Remove(caret, 1);
        string path = Write(name, text);
        MsBuildDocumentCache.Invalidate(path);

        var source = SourceText.From(text);
        var position = LspConverters.ToPosition(source.Lines.GetLinePosition(caret));

        return await MsBuildCompletionHandler.CompleteAsync(
            new CompletionParams(
                new TextDocumentIdentifier(LspConverters.PathToUri(path)),
                position,
                null),
            CancellationToken.None);
    }

    private static string[] Labels(CompletionList? list) =>
        list is null ? [] : [.. list.Items.Select(i => i.Label)];

    [Fact]
    public async Task LangVersionOffersTheCompilersOwnVersionsWithTheirFeatures()
    {
        var list = await CompleteAsync("""
            <Project>
              <PropertyGroup>
                <LangVersion>|</LangVersion>
              </PropertyGroup>
            </Project>
            """);

        var labels = Labels(list);
        Assert.Contains("latest", labels);
        Assert.Contains("13.0", labels);

        var records = Assert.Single(list!.Items, i => i.Label == "9.0");
        Assert.Contains("Records", records.Documentation!.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same property in an F# project offers nothing, because the values are C#'s. Offering
    /// them would be worse than silence: they look authoritative and the build then fails on a
    /// value the editor suggested.
    /// </summary>
    [Fact]
    public async Task LangVersionOffersNothingInAnFSharpProject()
    {
        var list = await CompleteAsync("""
            <Project>
              <PropertyGroup>
                <LangVersion>|</LangVersion>
              </PropertyGroup>
            </Project>
            """, "App.fsproj");

        Assert.Null(list);
    }

    [Fact]
    public async Task TargetFrameworkOffersFrameworksNewestFirst()
    {
        var list = await CompleteAsync("""
            <Project>
              <PropertyGroup>
                <TargetFramework>|</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        var labels = Labels(list);
        Assert.Contains("net8.0", labels);
        Assert.Contains("netstandard2.0", labels);
        Assert.True(Array.IndexOf(labels, "net10.0") < Array.IndexOf(labels, "net8.0"));
    }

    /// <summary>
    /// A property nobody wrote a case for. It works because the vendored corpus records the values,
    /// which is the whole reason for carrying it.
    /// </summary>
    [Fact]
    public async Task APropertyFromTheCorpusOffersItsValues()
    {
        var list = await CompleteAsync("""
            <Project>
              <PropertyGroup>
                <AllowUnsafeBlocks>|</AllowUnsafeBlocks>
              </PropertyGroup>
            </Project>
            """);

        Assert.Equal(["true", "false"], Labels(list));
    }

    [Fact]
    public async Task AnEmptyLineInAPropertyGroupOffersPropertyNames()
    {
        var list = await CompleteAsync("""
            <Project>
              <PropertyGroup>
                |
              </PropertyGroup>
            </Project>
            """);

        var labels = Labels(list);
        Assert.Contains("TargetFramework", labels);
        Assert.Contains("Nullable", labels);

        // MSBuild's convention is that a leading underscore means internal to the targets that
        // define it; a few hundred of those would bury everything worth setting.
        Assert.DoesNotContain(labels, l => l.StartsWith('_'));
    }

    [Fact]
    public async Task AnEmptyLineInAnItemGroupOffersItemTypes()
    {
        var list = await CompleteAsync("""
            <Project>
              <ItemGroup>
                |
              </ItemGroup>
            </Project>
            """);

        var labels = Labels(list);
        Assert.Contains("PackageReference", labels);
        Assert.Contains("Compile", labels);
    }

    [Fact]
    public async Task ProjectReferenceOffersSiblingProjectsAndNeverBuildOutput()
    {
        Write(@"Lib\Lib.csproj", "<Project />");
        Write(@"bin\Debug\ignored.csproj", "<Project />");
        Directory.CreateDirectory(Path.Combine(_directory, "obj"));

        var list = await CompleteAsync("""
            <Project>
              <ItemGroup>
                <ProjectReference Include="|" />
              </ItemGroup>
            </Project>
            """);

        var labels = Labels(list);
        Assert.Contains(@"Lib\", labels);

        // bin and obj are build output; offering them is offering a path that will not survive a
        // clean.
        Assert.DoesNotContain(@"bin\", labels);
        Assert.DoesNotContain(@"obj\", labels);
    }

    [Fact]
    public async Task ACompileIncludeOffersFilesAsWellAsFolders()
    {
        Write("Program.cs", "// x");
        Write(@"Nested\Thing.cs", "// x");

        var list = await CompleteAsync("""
            <Project>
              <ItemGroup>
                <Compile Include="|" />
              </ItemGroup>
            </Project>
            """);

        var labels = Labels(list);
        Assert.Contains("Program.cs", labels);
        Assert.Contains(@"Nested\", labels);
    }

    /// <summary>
    /// A semicolon separates item specs, so an unescaped one silently turns one file into two items
    /// — neither of which exists.
    /// </summary>
    [Fact]
    public async Task APathWithASemicolonIsEscaped()
    {
        Write("od;d.txt", "x");

        var list = await CompleteAsync("""
            <Project>
              <ItemGroup>
                <None Include="|" />
              </ItemGroup>
            </Project>
            """);

        Assert.Contains("od%3Bd.txt", Labels(list));
        Assert.DoesNotContain("od;d.txt", Labels(list));
    }

    [Fact]
    public async Task NothingIsOfferedInsideAComment()
    {
        var list = await CompleteAsync("""
            <Project>
              <PropertyGroup>
                <!-- <TargetFramework>|</TargetFramework> -->
              </PropertyGroup>
            </Project>
            """);

        Assert.Null(list);
    }

    /// <summary>
    /// Null rather than an empty list, everywhere there is nothing to say.
    /// </summary>
    /// <remarks>
    /// An empty list makes VS Code fall back to word-based completion, scraping identifiers out of
    /// the buffer — which in a project file means offering XML tag names and version fragments. The
    /// difference is invisible in the protocol and obvious to the user.
    /// </remarks>
    [Fact]
    public async Task NothingToOfferIsNullAndNeverAnEmptyList()
    {
        var list = await CompleteAsync("""
            <Project>
              <PropertyGroup>
                <SomePropertyWithFreeText>|</SomePropertyWithFreeText>
              </PropertyGroup>
            </Project>
            """);

        Assert.Null(list);
    }

    [Fact]
    public async Task AFileThePackDoesNotOwnIsNotCompletedAtAll()
    {
        var list = await CompleteAsync("<configuration>\n  <runtime>|</runtime>\n</configuration>", "web.config");

        Assert.Null(list);
    }

    /// <summary>
    /// The state completion actually runs in: the end tag has not been typed yet.
    /// </summary>
    /// <remarks>
    /// To the parser this is not an element at all — the text after the start tag is recovered as
    /// content of the nearest ancestor that closes, so the caret comes back as whitespace inside
    /// the PropertyGroup and the list offered is every property name, in the one position where a
    /// property name is the wrong answer.
    /// </remarks>
    [Fact]
    public async Task AnUnclosedPropertyOffersItsValuesAndNotPropertyNames()
    {
        var list = await CompleteAsync("""
            <Project>
              <PropertyGroup>
                <RootNamespace>App</RootNamespace>
                <LangVersion>|
              </PropertyGroup>
            </Project>
            """);

        var labels = Labels(list);
        Assert.Contains("latest", labels);
        Assert.DoesNotContain("AllowUnsafeBlocks", labels);
    }

    /// <summary>The same, for an element outside a PropertyGroup: nothing, rather than the wrong
    /// thing.</summary>
    [Fact]
    public async Task AnUnclosedElementInATargetOffersNothing()
    {
        var list = await CompleteAsync("""
            <Project>
              <Target Name="Build">
                <Message>|
              </Target>
            </Project>
            """);

        Assert.Null(list);
    }

    /// <summary>
    /// A group that has not been closed yet still knows what goes inside it.
    /// </summary>
    /// <remarks>
    /// The line break is the whole distinction from the case above. Content on the start tag's own
    /// line is that element's value; content on a later line is where its next child is typed —
    /// which is what an unclosed <c>&lt;PropertyGroup&gt;</c> is, and offering property names there
    /// is right.
    /// </remarks>
    [Fact]
    public async Task AnUnclosedGroupStillOffersWhatGoesInsideIt()
    {
        var list = await CompleteAsync("""
            <Project>
              <PropertyGroup>
                |
            </Project>
            """);

        Assert.Contains("LangVersion", Labels(list));
    }

    /// <summary>
    /// A half-typed name is replaced, not appended to.
    /// </summary>
    /// <remarks>
    /// The caret alone does not say what a completion replaces. An edit anchored at the caret turns
    /// <c>&lt;Lang</c> plus <c>LangVersion</c> into <c>&lt;LangLangVersion</c>, which is the kind of
    /// wrong that only shows up on acceptance.
    /// </remarks>
    [Fact]
    public async Task AHalfTypedElementNameIsReplacedWhole()
    {
        var list = await CompleteAsync("""
            <Project>
              <PropertyGroup>
                <Lang|
              </PropertyGroup>
            </Project>
            """);

        var item = Assert.Single(list!.Items, i => i.Label == "LangVersion");
        var range = item.TextEdit!.Range;

        Assert.Equal(2, range.Start.Line);
        Assert.Equal(5, range.Start.Character);
        Assert.Equal(9, range.End.Character);
    }

    /// <summary>
    /// A <c>Directory.Build.props</c> is where these properties belong, more so than any one
    /// project file — so the values are offered there too. The file names no language, so the
    /// projects it sits above are asked.
    /// </summary>
    [Fact]
    public async Task APropsFileAboveCSharpProjectsOffersCSharpValues()
    {
        Write("App/App.csproj", "<Project />");

        var list = await CompleteAsync("""
            <Project>
              <PropertyGroup>
                <LangVersion>|</LangVersion>
              </PropertyGroup>
            </Project>
            """, "Directory.Build.props");

        Assert.Contains("latest", Labels(list));
    }

    /// <summary>
    /// Unanimity or nothing. A tree that mixes languages has no single right list, and the flavour
    /// gate exists precisely so a value that looks authoritative is never offered where it does not
    /// apply.
    /// </summary>
    [Fact]
    public async Task APropsFileAboveAMixedTreeOffersNothing()
    {
        Write("App/App.csproj", "<Project />");
        Write("Legacy/Legacy.vbproj", "<Project />");

        var list = await CompleteAsync("""
            <Project>
              <PropertyGroup>
                <LangVersion>|</LangVersion>
              </PropertyGroup>
            </Project>
            """, "Directory.Build.props");

        Assert.Null(list);
    }
}
