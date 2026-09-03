using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Navigation answered against an edited, unsaved buffer. Serving completion from a cached index
/// rather than a freshly forced one only trades staleness in the completion list; nothing that
/// binds symbols may go with it, so the two features that resolve positions to declarations are
/// asserted after an edit that moves every line below it.
/// </summary>
[Collection(SharedState.Name)]
public class EditedBufferNavigationTests
{
    /// <summary>Inserted above the type, so every declaration and every call site in the file
    /// shifts — a definition computed against the pre-edit text lands on the wrong line.</summary>
    private const string Header = "// shifted\r\n// shifted\r\n// shifted\r\n";

    [Fact]
    public async Task DefinitionLandsOnTheDeclarationAtItsPostEditLine()
    {
        string path = FixturePaths.CalculatorFile;
        string session = $"nav-def-{Guid.NewGuid():N}";
        string source = await File.ReadAllTextAsync(path);

        OpenDocumentStore.Open(session, path, SourceText.From(source), version: 1);
        try
        {
            var edited = ShiftEverythingDown(path, source);

            var locations = await NavigationHandlers.DefinitionAsync(
                PositionOf(path, edited, "Add(a, b), Subtract"), typeDefinition: false, default);

            var location = Assert.Single(locations);
            Assert.EndsWith("Calculator.cs", LspConverters.UriToPath(location.Uri), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(LineOf(edited, "public int Add"), location.Range.Start.Line);

            // The line is only the right answer if it is the one the edit moved it to.
            Assert.Equal(3, LineOf(edited, "public int Add") - LineOf(SourceText.From(source), "public int Add"));
            Assert.StartsWith(
                "    public int Add", edited.Lines[location.Range.Start.Line].ToString());
        }
        finally
        {
            OpenDocumentStore.Close(session, path);
        }
    }

    [Fact]
    public async Task ReferencesReportTheirPostEditLines()
    {
        string path = FixturePaths.CalculatorFile;
        string session = $"nav-ref-{Guid.NewGuid():N}";
        string source = await File.ReadAllTextAsync(path);

        OpenDocumentStore.Open(session, path, SourceText.From(source), version: 1);
        try
        {
            var edited = ShiftEverythingDown(path, source);

            var declaration = PositionOf(path, edited, "Add(int a, int b)");
            var locations = await NavigationHandlers.ReferencesAsync(
                new ReferenceParams(
                    declaration.TextDocument, declaration.Position,
                    new ReferenceContext(IncludeDeclaration: true)),
                default);

            var lines = locations
                .Where(l => LspConverters.UriToPath(l.Uri)
                    .EndsWith("Calculator.cs", StringComparison.OrdinalIgnoreCase))
                .Select(l => l.Range.Start.Line)
                .ToHashSet();

            Assert.Contains(LineOf(edited, "public int Add"), lines);
            Assert.Contains(LineOf(edited, "return new Result(Add"), lines);
        }
        finally
        {
            OpenDocumentStore.Close(session, path);
        }
    }

    /// <summary>Prepends <see cref="Header"/> through the store, the way didChange does: a ranged
    /// change against the open buffer, with the version advanced.</summary>
    private static SourceText ShiftEverythingDown(string path, string source)
    {
        var edited = OpenDocumentStore.Change(path, version: 2,
            original => original.WithChanges(new TextChange(new TextSpan(0, 0), Header)));
        Assert.NotNull(edited);
        Assert.NotEqual(source, edited!.ToString());
        return edited;
    }

    private static int LineOf(SourceText text, string anchor) =>
        text.Lines.GetLinePosition(text.ToString().IndexOf(anchor, StringComparison.Ordinal)).Line;

    private static TextDocumentPositionParams PositionOf(string path, SourceText text, string anchor)
    {
        var position = text.Lines.GetLinePosition(
            text.ToString().IndexOf(anchor, StringComparison.Ordinal));
        return new TextDocumentPositionParams(
            new TextDocumentIdentifier(LspConverters.PathToUri(path)),
            new Position(position.Line, position.Character));
    }
}
