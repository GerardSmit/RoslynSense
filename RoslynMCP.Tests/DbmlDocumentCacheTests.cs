using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Dbml.Core;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// What the parse cache reuses, which is not observable from its results — a cache that reparsed the
/// world on every keystroke would return exactly the same model, so counting the work is the only way
/// to pin the behaviour.
/// </summary>
public class DbmlDocumentCacheTests
{
    private const string Path = @"C:\src\Cache.dbml";

    private const string Original = """
        <?xml version="1.0" encoding="utf-8"?>
        <Database Name="Shop" Class="ShopDataContext" xmlns="http://schemas.microsoft.com/linqtosql/dbml/2007">
          <Table Name="dbo.Products" Member="Products">
            <Type Name="Product">
              <Column Name="Id" Type="System.Int32" />
            </Type>
          </Table>
        </Database>
        """;

    public DbmlDocumentCacheTests() => DbmlDocumentCache.Clear();

    [Fact]
    public void AnUnchangedBufferIsNotReparsed()
    {
        // Several providers fire for one keystroke — outline, lens, diagnostics — and each asks for
        // the same document.
        var text = SourceText.From(Original);

        DbmlDocumentCache.For(Path, text);
        long after = DbmlDocumentCache.FullParses;

        DbmlDocumentCache.For(Path, text);
        DbmlDocumentCache.For(Path, SourceText.From(Original));

        Assert.Equal(after, DbmlDocumentCache.FullParses);
        Assert.Equal(0, DbmlDocumentCache.IncrementalParses);
    }

    [Fact]
    public void AnEditSplicesIntoThePreviousTree()
    {
        var text = SourceText.From(Original);
        DbmlDocumentCache.For(Path, text);

        int offset = Original.IndexOf("Product\"", StringComparison.Ordinal) + "Product".Length;
        var edited = text.WithChanges(new TextChange(new TextSpan(offset, 0), "s"));

        var document = DbmlDocumentCache.For(Path, edited);

        Assert.Equal(1, DbmlDocumentCache.IncrementalParses);
        Assert.Equal("Products", document.Database.Tables[0].RowType!.Name);
    }

    [Fact]
    public void AnInvalidatedFileIsReadAgain()
    {
        var text = SourceText.From(Original);

        DbmlDocumentCache.For(Path, text);
        DbmlDocumentCache.Invalidate(Path);
        DbmlDocumentCache.For(Path, text);

        Assert.Equal(2, DbmlDocumentCache.FullParses);
    }
}
