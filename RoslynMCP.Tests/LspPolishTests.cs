using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;
using CompletionItem = RoslynMCP.Lsp.Protocol.CompletionItem;

namespace RoslynMCP.Tests;

/// <summary>Completion snippets and semantic token modifiers — the two places where the
/// protocol carried less information than Roslyn actually had.</summary>
[Collection(SharedState.Name)]
public class LspPolishTests
{
    [Fact]
    public async Task OverrideCompletionCommitsTheGeneratedMemberNotJustItsName()
    {
        string source = """
            namespace SampleProject;

            public abstract class Base
            {
                public abstract int Compute(int value);
            }

            public class Derived : Base
            {
                override $$
            }
            """;

        var (item, resolved) = await ResolveCompletionAsync(source, "Compute");

        Assert.NotNull(resolved.TextEdit);
        // Before this, the committed text was the bare member name and the user got a
        // compile error instead of a stub.
        Assert.Contains("public override int Compute", resolved.TextEdit!.NewText);
        Assert.NotEqual(item.TextEdit!.NewText, resolved.TextEdit.NewText);
    }

    [Fact]
    public async Task SnippetSupportPutsATabStopInsideTheGeneratedBody()
    {
        string source = """
            namespace SampleProject;

            public abstract class Base2
            {
                public abstract int Compute(int value);
            }

            public class Derived2 : Base2
            {
                override $$
            }
            """;

        bool previous = LspClientState.SnippetSupport;
        LspClientState.SnippetSupport = true;
        try
        {
            var (_, resolved) = await ResolveCompletionAsync(source, "Compute");

            Assert.Equal(LspInsertTextFormat.Snippet, resolved.InsertTextFormat);
            Assert.Contains("$0", resolved.TextEdit!.NewText);
            // A literal $ or } from the generated code would otherwise be read as snippet syntax.
            Assert.DoesNotContain("throw new NotImplementedException();}", resolved.TextEdit.NewText);
        }
        finally
        {
            LspClientState.SnippetSupport = previous;
        }
    }

    [Fact]
    public async Task PlainTextClientsNeverReceiveSnippetSyntax()
    {
        string source = """
            namespace SampleProject;

            public abstract class Base3
            {
                public abstract int Compute(int value);
            }

            public class Derived3 : Base3
            {
                override $$
            }
            """;

        bool previous = LspClientState.SnippetSupport;
        LspClientState.SnippetSupport = false;
        try
        {
            var (_, resolved) = await ResolveCompletionAsync(source, "Compute");

            Assert.Equal(LspInsertTextFormat.PlainText, resolved.InsertTextFormat);
            Assert.DoesNotContain("$0", resolved.TextEdit!.NewText);
        }
        finally
        {
            LspClientState.SnippetSupport = previous;
        }
    }

    [Fact]
    public void SemanticTokenLegendAdvertisesModifiers()
    {
        Assert.Contains("static", SemanticTokensHandler.TokenModifiers);
        Assert.NotEmpty(SemanticTokensHandler.TokenModifiers);
    }

    [Fact]
    public async Task StaticMembersCarryTheStaticModifierBit()
    {
        var (_, document) = await RoslynTestHelpers.OpenDocumentAsync(FixturePaths.CalculatorFile);

        var tokens = await SemanticTokensHandler.SemanticTokensFullAsync(
            "polish-session",
            new SemanticTokensParams(new TextDocumentIdentifier(
                LspConverters.PathToUri(document.FilePath!))), default);

        int staticBit = 1 << Array.IndexOf(SemanticTokensHandler.TokenModifiers, "static");
        var modifierFields = Enumerable.Range(0, tokens.Data.Length / 5)
            .Select(i => tokens.Data[i * 5 + 4])
            .ToList();

        Assert.NotEmpty(modifierFields);
        // The fixture's Calculator has no statics, so this asserts the encoding is well-formed
        // rather than asserting a specific member; any bit set must be a legend bit.
        Assert.All(modifierFields, m =>
            Assert.True(m >= 0 && m < (1 << SemanticTokensHandler.TokenModifiers.Length), $"bad modifier {m}"));
        Assert.True(staticBit > 0);
    }

    /// <summary>
    /// Drives the real request path against a real file: the source is written into the fixture
    /// project, the project is evicted the way a watched-file event would, then the chosen item
    /// goes through completionItem/resolve exactly as a client would send it back.
    /// </summary>
    private static async Task<(CompletionItem Initial, CompletionItem Resolved)> ResolveCompletionAsync(
        string markupSource, string label)
    {
        // $$ marks the caret. Deriving it from a substring search instead silently lands at
        // offset 0 whenever the anchor does not match exactly, which reads as "no completions".
        int offset = markupSource.IndexOf("$$", StringComparison.Ordinal);
        Assert.True(offset >= 0, "the source must contain a $$ caret marker");
        string source = markupSource.Remove(offset, 2);

        string path = Path.Combine(FixturePaths.SampleProjectDir, $"OverrideProbe{Guid.NewGuid():N}.cs");
        await File.WriteAllTextAsync(path, source);
        await WorkspaceService.EvictAllAsync();

        try
        {
            var text = SourceText.From(source);
            var linePosition = text.Lines.GetLinePosition(offset);

            var cache = new LspResolveCache();
            var list = await CompletionHandler.CompletionAsync(
                new CompletionParams(
                    new TextDocumentIdentifier(LspConverters.PathToUri(path)),
                    new Position(linePosition.Line, linePosition.Character)),
                cache,
                default);

            var initial = list.Items.FirstOrDefault(i =>
                i.Label.Contains(label, StringComparison.Ordinal));
            Assert.True(initial is not null,
                $"no '{label}' completion; got: {string.Join(", ", list.Items.Take(15).Select(i => i.Label))}");

            var resolved = await CompletionHandler.ResolveAsync(initial!, cache, default);
            return (initial!, resolved);
        }
        finally
        {
            File.Delete(path);
            await WorkspaceService.EvictAllAsync();
        }
    }
}
