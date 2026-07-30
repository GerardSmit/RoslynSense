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
}
