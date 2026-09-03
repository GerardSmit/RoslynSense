using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Config;
using RoslynMCP.Languages.MsBuild.Core;
using RoslynMCP.Languages.MsBuild.Lsp;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Hovering a diagnostic code inside a suppression list.
/// </summary>
/// <remarks>
/// No solution is opened anywhere here, which is the point: a <c>&lt;NoWarn&gt;</c> is read in a
/// project file that is often the only thing open, and the answer for <c>NU1605</c> and
/// <c>CS0168</c> has to arrive without one. The analyzer-backed half — <c>CA</c> and third-party
/// rules — needs a loaded project by construction and is covered against the catalog directly.
/// </remarks>
[Collection(SharedState.Name)]
public class MsBuildWarningCodeHoverTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "roslynsense-nowarn-" + Guid.NewGuid().ToString("N")[..8]);

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

    /// <summary>Hovers at the <c>|</c> in the given project text.</summary>
    private Hover? Hover(string marked, string name = "App.csproj") =>
        HoverAsync(marked, name).GetAwaiter().GetResult();

    private async Task<Hover?> HoverAsync(string marked, string name = "App.csproj")
    {
        int caret = marked.IndexOf('|', StringComparison.Ordinal);
        Assert.True(caret >= 0, "the fixture has to mark the caret with |");

        string text = marked.Remove(caret, 1);
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, text);
        MsBuildDocumentCache.Invalidate(path);

        var source = SourceText.From(text);
        var position = LspConverters.ToPosition(source.Lines.GetLinePosition(caret));

        return await MsBuildHoverHandler.ComputeAsync(
            new TextDocumentPositionParams(
                new TextDocumentIdentifier(LspConverters.PathToUri(path)), position),
            CancellationToken.None);
    }

    private static string Markdown(Hover? hover)
    {
        Assert.NotNull(hover);
        return hover!.Contents.Value;
    }

    [Fact]
    public void NuGetCodeIsDescribedFromTheVendoredDocumentation()
    {
        string markdown = Markdown(Hover("""
            <Project>
              <PropertyGroup>
                <NoWarn>$(NoWarn);NU16|05</NoWarn>
              </PropertyGroup>
            </Project>
            """));

        Assert.Contains("**NU1605**", markdown, StringComparison.Ordinal);
        Assert.Contains("warning", markdown, StringComparison.Ordinal);
        Assert.Contains("downgrade", markdown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "https://learn.microsoft.com/nuget/reference/errors-and-warnings/nu1605",
            markdown,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A compiler code is answered from Roslyn's own resources, so it says what the Problems panel
    /// says rather than a copy of it that can drift.
    /// </summary>
    [Fact]
    public void CompilerCodeIsDescribedFromRoslyn()
    {
        string markdown = Markdown(Hover("""
            <Project>
              <PropertyGroup>
                <NoWarn>CS01|68</NoWarn>
              </PropertyGroup>
            </Project>
            """));

        Assert.Contains("**CS0168**", markdown, StringComparison.Ordinal);
        Assert.Contains("never used", markdown, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The hover covers the code the caret is on, not the whole list.</summary>
    [Fact]
    public void TheRangeIsTheCodeAndNotTheValue()
    {
        const string line = "    <NoWarn>NU1605;CS0168;NU1701</NoWarn>";
        var hover = Hover($"""
            <Project>
              <PropertyGroup>
            {line.Replace("CS0168", "CS0|168")}
              </PropertyGroup>
            </Project>
            """);

        Assert.NotNull(hover);
        Assert.Equal(2, hover!.Range!.Start.Line);
        Assert.Equal(line.IndexOf("CS0168", StringComparison.Ordinal), hover.Range.Start.Character);
        Assert.Equal(hover.Range.Start.Character + "CS0168".Length, hover.Range.End.Character);
        Assert.Contains("**CS0168**", Markdown(hover), StringComparison.Ordinal);
    }

    /// <summary>
    /// A code minted after this build shipped still gets its documentation link, which is the only
    /// thing that can be right about a code nothing here has heard of.
    /// </summary>
    [Fact]
    public void UnknownCodeStillLinksToItsFamilysDocumentation()
    {
        string markdown = Markdown(Hover("""
            <Project>
              <PropertyGroup>
                <NoWarn>CA99|99</NoWarn>
              </PropertyGroup>
            </Project>
            """));

        Assert.Contains("**CA9999**", markdown, StringComparison.Ordinal);
        Assert.Contains(
            "https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca9999",
            markdown,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>$(NoWarn)</c> is not a code — it means "whatever was already suppressed" — so the list
    /// branch declines it and the property's own documentation answers, as it does anywhere else
    /// in the value.
    /// </summary>
    [Fact]
    public void PropertyReferenceInTheListDescribesThePropertyInstead()
    {
        string markdown = Markdown(Hover("""
            <Project>
              <PropertyGroup>
                <NoWarn>$(NoW|arn);NU1605</NoWarn>
              </PropertyGroup>
            </Project>
            """));

        Assert.Contains("**NoWarn**", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("NU1605", markdown, StringComparison.Ordinal);
    }

    /// <summary>NoWarn is also metadata on a reference, written as an attribute.</summary>
    [Fact]
    public void NoWarnAttributeOnAPackageReferenceIsAListToo()
    {
        string markdown = Markdown(Hover("""
            <Project>
              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.0.0" NoWarn="NU17|01" />
              </ItemGroup>
            </Project>
            """));

        Assert.Contains("**NU1701**", markdown, StringComparison.Ordinal);
        Assert.Contains("AssetTargetFallback", markdown, StringComparison.Ordinal);
    }

    /// <summary>
    /// The property's own documentation still answers on the element name — the code branch takes
    /// the value and nothing else.
    /// </summary>
    [Fact]
    public void HoveringTheElementNameStillDescribesTheProperty()
    {
        string markdown = Markdown(Hover("""
            <Project>
              <PropertyGroup>
                <NoW|arn>NU1605</NoWarn>
              </PropertyGroup>
            </Project>
            """));

        Assert.Contains("**NoWarn**", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("NU1605", markdown, StringComparison.Ordinal);
    }

    /// <summary>
    /// The count, once it is known, is the line the reader is actually after: whether the entry is
    /// still doing anything.
    /// </summary>
    [Fact]
    public void AKnownCountIsShownBesideTheDescription()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "App.csproj");

        WarningOccurrenceCache.Seed(
            path, "CS0168", new WarningOccurrences(Count: 3, Projects: 1, Scope: 1, DateTime.UtcNow));

        string markdown = Markdown(Hover("""
            <Project>
              <PropertyGroup>
                <NoWarn>CS01|68</NoWarn>
              </PropertyGroup>
            </Project>
            """));

        Assert.Contains("Suppressing 3 occurrences in this project.", markdown, StringComparison.Ordinal);
    }

    /// <summary>A suppression with nothing left to suppress says so in as many words.</summary>
    [Fact]
    public void ADeadSuppressionSaysItMayNoLongerBeNeeded()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "Directory.Build.props");

        WarningOccurrenceCache.Seed(
            path, "CS0168", new WarningOccurrences(Count: 0, Projects: 4, Scope: 4, DateTime.UtcNow));

        string markdown = Markdown(Hover("""
            <Project>
              <PropertyGroup>
                <NoWarn>CS01|68</NoWarn>
              </PropertyGroup>
            </Project>
            """, "Directory.Build.props"));

        Assert.Contains("Not reported in 4 projects", markdown, StringComparison.Ordinal);
        Assert.Contains("may no longer be needed", markdown, StringComparison.Ordinal);
    }

    /// <summary>
    /// Metadata on one reference is not counted: nothing outside the project file can lift it, so
    /// the number would be taken with the suppression still in force.
    /// </summary>
    [Fact]
    public void AReferencesOwnNoWarnIsNotCounted()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "App.csproj");

        WarningOccurrenceCache.Seed(
            path, "NU1701", new WarningOccurrences(Count: 0, Projects: 1, Scope: 1, DateTime.UtcNow));

        string markdown = Markdown(Hover("""
            <Project>
              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.0.0">
                  <NoWarn>NU17|01</NoWarn>
                </PackageReference>
              </ItemGroup>
            </Project>
            """));

        Assert.Contains("**NU1701**", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("no longer be needed", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogRejectsWhatIsNotACode()
    {
        Assert.False(DiagnosticCodeCatalog.IsCode("$(NoWarn)"));
        Assert.False(DiagnosticCodeCatalog.IsCode("NU"));
        Assert.False(DiagnosticCodeCatalog.IsCode("1605"));
        Assert.False(DiagnosticCodeCatalog.IsCode(""));
        Assert.True(DiagnosticCodeCatalog.IsCode("nu1605"));
        Assert.True(DiagnosticCodeCatalog.IsCode("SYSLIB0011"));
    }

    /// <summary>
    /// A rule an analyzer defines is described by that analyzer, which is the only place a
    /// third-party rule is written down at all.
    /// </summary>
    [Fact]
    public void AnalyzerRuleIsDescribedByItsOwnDescriptor()
    {
        Assert.True(LspFeatureOptions.CodeStyleDiagnostics, "the IDE analyzers are what this reads");

        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("Analyzed", LanguageNames.CSharp);

        // IDE0005 is defined by an analyzer that ships inside the Features assemblies, so the
        // descriptor path is exercised without a project on disk carrying analyzer references.
        var info = DiagnosticCodeCatalog.Lookup("IDE0005", project);

        Assert.NotNull(info);
        Assert.Equal("IDE0005", info!.Value.Code);
        Assert.False(info.Value.IsEmpty);
        Assert.Contains("using", info.Value.Title ?? "", StringComparison.OrdinalIgnoreCase);
    }
}
