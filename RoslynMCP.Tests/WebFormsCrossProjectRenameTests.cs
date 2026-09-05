using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages;
using RoslynMCP.Languages.WebForms;
using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Languages.WebForms.Lsp;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

[Collection(SharedState.Name)]
public class WebFormsCrossProjectRenameTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenameCrossesProjectBoundaryAndUpdatesAllMarkupExtensions(bool fromMarkup)
    {
        new LanguageRegistry([new WebFormsLanguage(new MarkdownFormatter())]).Publish();
        string sourcePath = Path.Combine(FixturePaths.ControlLibAppDir, "ControlLib", "WebStubs.cs");
        string ascx = Path.Combine(FixturePaths.ControlLibAppDir, "WebApp", "Shared.ascx");
        string path = fromMarkup ? ascx : sourcePath;
        var source = SourceText.From(File.ReadAllText(path));
        int offset = source.ToString().IndexOf(fromMarkup ? "Text=" : "Text {", StringComparison.Ordinal) + 1;
        var line = source.Lines.GetLinePosition(offset);
        var request = new RenameParams(new(LspConverters.PathToUri(path)), new(line.Line, line.Character), "Caption");
        string session = Guid.NewGuid().ToString("N");
        string master = Path.ChangeExtension(ascx, ".master");
        var buffer = SourceText.From("<%-- unsaved heading --%>\n" + File.ReadAllText(master));
        try
        {
            OpenDocumentStore.Open(session, master, buffer, 1);
            var edit = fromMarkup
                ? await AspxLanguageHandler.RenameAsync(request, default)
                : await RenameHandler.RenameAsync(request, default);
            Assert.NotNull(edit);
            Assert.DoesNotContain(edit.Changes.Keys, uri => uri.Contains("/Unrelated/", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("string Caption", ProtoRenameTests.Apply(sourcePath, edit));
            Assert.Contains(".Caption", ProtoRenameTests.Apply(
                Path.Combine(FixturePaths.ControlLibAppDir, "WebApp", "Encoded.ascx"), edit));
            foreach (string extension in new[] { ".aspx", ".ascx", ".master" })
            {
                string markup = Path.ChangeExtension(ascx, extension);
                var text = extension == ".master" ? buffer : SourceText.From(File.ReadAllText(markup));
                var changes = edit.Changes[LspConverters.PathToUri(markup)];
                Assert.Single(changes);
                string updated = text.WithChanges(changes.Select(e => new TextChange(LspConverters.ToTextSpan(text, e.Range), e.NewText))).ToString();
                Assert.Contains("Caption=\"Text stays literal\"", updated);
            }
        }
        finally
        {
            OpenDocumentStore.Close(session, master);
            AspxDocumentService.Invalidate(master);
        }
    }
}
