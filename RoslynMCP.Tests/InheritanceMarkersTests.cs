using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using Xunit;

namespace RoslynMCP.Tests;

public class InheritanceMarkersTests
{
    [Fact]
    public async Task MarkersCoverBothDirectionsOfInterfaceImplementation()
    {
        string uri = LspConverters.PathToUri(FixturePaths.ServicesFile);
        var markers = await InheritanceMarkersHandler.MarkersAsync(
            new InheritanceMarkersParams(new TextDocumentIdentifier(uri)), default);

        // StatisticsCalculator : IStringFormatter — up marker on the class line.
        var baseMarker = markers.FirstOrDefault(m =>
            m.Kind == "base" && m.Targets.Any(t => t.Title.Contains("IStringFormatter")));
        Assert.NotNull(baseMarker);

        // FormatDisplayValue — member-level "implements" up marker.
        Assert.Contains(markers, m =>
            m.Kind == "implements"
            && m.Targets.Any(t => t.Title.Contains("IStringFormatter.FormatDisplayValue")));

        // IStringFormatter — down markers: type implemented by StatisticsCalculator,
        // interface member implemented by the calculator's method.
        Assert.Contains(markers, m =>
            m.Kind == "derived"
            && m.Targets.Any(t => t.Title.Contains("StatisticsCalculator")));
        Assert.Contains(markers, m =>
            m.Kind == "implemented"
            && m.Targets.Any(t => t.Title.Contains("StatisticsCalculator.FormatDisplayValue")));

        // Source targets carry a navigable location; metadata targets (null Uri) are
        // resolved lazily via roslynSense/resolveInheritanceTarget.
        Assert.All(markers.SelectMany(m => m.Targets), t =>
        {
            if (t.Uri is not null)
                Assert.StartsWith("file:", t.Uri);
            Assert.True(t.Line >= 0);
        });
    }

    /// <summary>
    /// The click behind a lens asks by position, and the answer is the same whether the position
    /// is the identifier the lens sits above or a point inside the member's body — the keyboard
    /// command fires from wherever the cursor happens to be.
    /// </summary>
    [Fact]
    public async Task MarkersAtAPositionDescribeTheEnclosingDeclaration()
    {
        string uri = LspConverters.PathToUri(FixturePaths.ServicesFile);
        string source = await File.ReadAllTextAsync(FixturePaths.ServicesFile);
        var (line, character) = PositionOf(source, "FormatDisplayValue(int value) =>");

        var atIdentifier = await InheritanceMarkersHandler.MarkersAtAsync(
            new InheritanceAtParams(new TextDocumentIdentifier(uri), line, character), default);
        var inBody = await InheritanceMarkersHandler.MarkersAtAsync(
            new InheritanceAtParams(new TextDocumentIdentifier(uri), line, character + 30), default);

        foreach (var markers in new[] { atIdentifier, inBody })
        {
            // StatisticsCalculator.FormatDisplayValue implements the interface member and nothing
            // overrides it: one up marker, anchored where the file-wide pass anchors it.
            var marker = Assert.Single(markers);
            Assert.Equal("implements", marker.Kind);
            Assert.Equal((line, character), (marker.Line, marker.Character));
            Assert.Contains(marker.Targets, t => t.Title.Contains("IStringFormatter.FormatDisplayValue"));
        }
    }

    /// <summary>
    /// A position answers with the downward relations too, and with the same targets the
    /// file-wide pass finds — the lens count and the list behind the click come from one search.
    /// </summary>
    [Fact]
    public async Task MarkersAtAnInterfaceMemberFindItsImplementations()
    {
        string uri = LspConverters.PathToUri(FixturePaths.ServicesFile);
        string source = await File.ReadAllTextAsync(FixturePaths.ServicesFile);
        var (line, character) = PositionOf(source, "FormatDisplayValue(int value);");

        var markers = await InheritanceMarkersHandler.MarkersAtAsync(
            new InheritanceAtParams(new TextDocumentIdentifier(uri), line, character), default);

        var marker = Assert.Single(markers);
        Assert.Equal("implemented", marker.Kind);
        Assert.Contains(marker.Targets, t => t.Title.Contains("StatisticsCalculator.FormatDisplayValue"));

        var fileWide = await InheritanceMarkersHandler.MarkersAsync(
            new InheritanceMarkersParams(new TextDocumentIdentifier(uri)), default);
        var sameLine = Assert.Single(fileWide, m => m.Line == line && m.Kind == "implemented");
        Assert.Equal(sameLine.Targets.Select(t => t.Title), marker.Targets.Select(t => t.Title));
    }

    private static (int Line, int Character) PositionOf(string source, string snippet)
    {
        int offset = source.IndexOf(snippet, StringComparison.Ordinal);
        Assert.True(offset >= 0, $"the fixture no longer contains '{snippet}'");
        var position = SourceText.From(source).Lines.GetLinePosition(offset);
        return (position.Line, position.Character);
    }
}
