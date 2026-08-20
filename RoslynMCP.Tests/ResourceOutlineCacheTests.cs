using RoslynMCP.Config;
using RoslynMCP.Languages.Resources;
using RoslynMCP.Languages.Resources.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The outline reads a <c>.resx</c> through the same checksum cache its diagnostics do.
/// </summary>
/// <remarks>
/// Asserting on the outline itself proves nothing — a reader that reparsed on every request
/// returns the same tree — so these count the parses instead, which is the same reason
/// <see cref="Languages.MsBuild.Core.MsBuildDocumentCache.FullParses"/> exists.
/// </remarks>
public class ResourceOutlineCacheTests : IDisposable
{
    private const string Contents =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <root>
          <data name="btnSave.Text" xml:space="preserve"><value>Save</value></data>
          <data name="btnSave.ToolTip" xml:space="preserve"><value>Save this</value></data>
          <data name="btnCancel.Text" xml:space="preserve"><value>Cancel</value></data>
        </root>
        """;

    private readonly string _root;
    private readonly ResourcesLanguage _pack = new(EffectiveSettings.Resolve([], null, out _));

    public ResourceOutlineCacheTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "roslynsense-resx-outline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        ResourceCatalogService.InvalidateAll();
    }

    public void Dispose()
    {
        ResourceCatalogService.InvalidateAll();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ASecondOutlineOfAnUnchangedFileDoesNotReparseIt()
    {
        string path = Write("View.ascx.resx", Contents);

        var first = await OutlineAsync(path);
        long afterFirst = ResourceCatalogService.FileParses;

        var second = await OutlineAsync(path);

        Assert.NotEmpty(first);
        Assert.Equal(first.Length, second.Length);

        // The outline used to call ResxReader.Read directly while the sibling diagnostics pass on
        // the same file read through the catalog — the one consumer in the pack that opted out of
        // its own memo, on the request the editor repeats most often.
        Assert.Equal(afterFirst, ResourceCatalogService.FileParses);
    }

    /// <summary>
    /// And it shares that cache with the diagnostics pass, rather than keeping a second entry
    /// under a differently-spelled key.
    /// </summary>
    [Fact]
    public async Task TheOutlineAndTheDiagnosticsShareOneParse()
    {
        string path = Write("Shared.ascx.resx", Contents);

        await _pack.DiagnosticsAsync(path, default);
        long afterDiagnostics = ResourceCatalogService.FileParses;
        Assert.True(afterDiagnostics > 0, "the diagnostics pass should have parsed the file once");

        await OutlineAsync(path);

        Assert.Equal(afterDiagnostics, ResourceCatalogService.FileParses);
    }

    private Task<DocumentSymbol[]> OutlineAsync(string path) =>
        _pack.DocumentSymbolAsync(
            new DocumentSymbolParams(new TextDocumentIdentifier(LspConverters.PathToUri(path))), default);

    private string Write(string name, string contents)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, contents);
        return path;
    }
}
