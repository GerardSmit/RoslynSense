using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>signatureHelp, pull-diagnostics resultId versioning, metadata inheritance
/// target resolution (decompile on demand).</summary>
public class LspSignatureAndPullTests
{
    [Fact]
    public async Task SignatureHelpShowsParametersInsideInvocation()
    {
        string uri = LspConverters.PathToUri(FixturePaths.CalculatorFile);
        string text = await File.ReadAllTextAsync(FixturePaths.CalculatorFile);
        var (line, character) = PositionOf(text, "Add(a, b), Subtract");

        var help = await SignatureHelpHandler.SignatureHelpAsync(
            new SignatureHelpParams(
                new TextDocumentIdentifier(uri),
                new Position(line, character + "Add(".Length)),
            default);

        Assert.NotNull(help);
        Assert.NotEmpty(help!.Signatures);
        var signature = help.Signatures[help.ActiveSignature];
        Assert.Contains("Add", signature.Label);
        Assert.Equal(2, signature.Parameters.Length);
        Assert.Contains("int a", signature.Parameters[0].Label);
    }

    [Fact]
    public async Task PullDiagnosticsReturnsUnchangedForSameResultId()
    {
        string uri = LspConverters.PathToUri(FixturePaths.BrokenSemanticFile);

        var first = await DiagnosticsHandler.PullAsync(
            new DocumentDiagnosticParams(new TextDocumentIdentifier(uri)), default);
        var full = Assert.IsType<FullDocumentDiagnosticReport>(first);
        Assert.NotNull(full.ResultId);
        Assert.NotEmpty(full.Items);

        var second = await DiagnosticsHandler.PullAsync(
            new DocumentDiagnosticParams(new TextDocumentIdentifier(uri), full.ResultId), default);
        var unchanged = Assert.IsType<UnchangedDocumentDiagnosticReport>(second);
        Assert.Equal(full.ResultId, unchanged.ResultId);
    }

    [Fact]
    public async Task MetadataInheritanceTargetResolvesViaDecompilation()
    {
        // Overlay Calculator.cs implementing IDisposable (a metadata-only interface):
        // the marker must list it with a null Uri, and resolveInheritanceTarget must
        // decompile it into a navigable location.
        string path = FixturePaths.CalculatorFile;
        string original = await File.ReadAllTextAsync(path);
        string modified = original
            .Replace("public class Calculator", "public class Calculator : System.IDisposable")
            .Replace("    public int Add(int a, int b) => a + b;",
                "    public void Dispose() { }\r\n\r\n    public int Add(int a, int b) => a + b;");
        Assert.NotEqual(original, modified);

        string session = Guid.NewGuid().ToString("N");
        try
        {
            OpenDocumentStore.Open(session, path, SourceText.From(modified), 1);
            string uri = LspConverters.PathToUri(path);

            var markers = await InheritanceMarkersHandler.MarkersAsync(
                new InheritanceMarkersParams(new TextDocumentIdentifier(uri)), default);

            var baseMarker = markers.FirstOrDefault(m =>
                m.Kind == "base" && m.Targets.Any(t => t.Title.Contains("IDisposable")));
            Assert.NotNull(baseMarker);
            int index = Array.FindIndex(baseMarker!.Targets, t => t.Title.Contains("IDisposable"));
            Assert.Null(baseMarker.Targets[index].Uri); // metadata — no location yet

            var location = await InheritanceMarkersHandler.ResolveTargetAsync(
                new ResolveInheritanceTargetParams(
                    new TextDocumentIdentifier(uri),
                    baseMarker.Line, baseMarker.Character, "base", index),
                default);

            Assert.NotNull(location);
            Assert.StartsWith("file:", location!.Uri);

            // The member-level "implements IDisposable.Dispose" marker exists too.
            Assert.Contains(markers, m =>
                m.Kind == "implements" && m.Targets.Any(t => t.Title.Contains("Dispose")));
        }
        finally
        {
            OpenDocumentStore.Close(session, path);
        }
    }

    [Fact]
    public async Task DefinitionOnMetadataSymbolDecompiles()
    {
        string uri = LspConverters.PathToUri(FixturePaths.FrameworkReferencesFile);
        string text = await File.ReadAllTextAsync(FixturePaths.FrameworkReferencesFile);
        var (line, character) = PositionOf(text, "Console.WriteLine");

        var locations = await NavigationHandlers.DefinitionAsync(
            new TextDocumentPositionParams(
                new TextDocumentIdentifier(uri),
                new Position(line, character + "Console.".Length + 1)),
            typeDefinition: false, default);

        var location = Assert.Single(locations);
        Assert.Contains("Decompiled", location.Uri); // generated source under the temp dir
    }

    private static (int Line, int Character) PositionOf(string text, string anchor)
    {
        int index = text.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(index >= 0, $"anchor '{anchor}' not found");
        int line = 0, lineStart = 0;
        for (int i = 0; i < index; i++)
        {
            if (text[i] == '\n') { line++; lineStart = i + 1; }
        }
        return (line, index - lineStart);
    }
}
