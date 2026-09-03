using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Dbml;
using RoslynMCP.Languages.Dbml.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// textDocument/completion in a <c>.dbml</c>, for the half of it a schema could not supply: the
/// values that have to come from the model in the buffer.
/// </summary>
/// <remarks>
/// The key lists are what these assert on. <c>ThisKey</c> and <c>OtherKey</c> are read off the two
/// ends of the association being typed — the <c>&lt;Type&gt;</c> the element sits in, and the one its
/// <c>Type=</c> names — and both are read from the live node rather than the parsed model, because an
/// association mid-typing is not in the model yet. An empty list there is the failure worth pinning:
/// it looks exactly like a caret in a place with nothing to offer.
/// </remarks>
[Collection(SharedState.Name)]
public class DbmlCompletionTests : IDisposable
{
    private const string Model = """
        <?xml version="1.0" encoding="utf-8"?>
        <Database Name="Shop" Class="ShopDataContext" xmlns="http://schemas.microsoft.com/linqtosql/dbml/2007">
          <Table Name="dbo.Orders" Member="Orders">
            <Type Name="Order">
              <Column Name="Id" Type="System.Int32" DbType="Int NOT NULL" IsPrimaryKey="true" />
              <Column Name="CustomerId" Type="System.Int32" DbType="Int NOT NULL" />
              <Association Name="Customer_Order" Member="Customer" ThisKey="" Type="Customer" />
            </Type>
          </Table>
          <Table Name="dbo.Customers" Member="Customers">
            <Type Name="Customer">
              <Column Name="Id" Type="System.Int32" DbType="Int NOT NULL" IsPrimaryKey="true" />
              <Column Name="Name" Type="System.String" DbType="NVarChar(100)" />
            </Type>
          </Table>
        </Database>
        """;

    private readonly string _session = $"dbml-completion-{Guid.NewGuid():N}";
    private readonly string _path = Path.Combine(FixturePaths.DbmlProjectDir, "Completion.dbml");

    public DbmlCompletionTests() => DbmlDocumentCache.Clear();

    public void Dispose()
    {
        OpenDocumentStore.CloseSession(_session);

        // The parse is memoized per path, and this path names a file that does not exist.
        DbmlDocumentCache.Invalidate(_path);
    }

    [Fact]
    public async Task TheKeysOfferedAreTheColumnsOfTheTypeAtThatEndOfTheAssociation()
    {
        // Inside ThisKey="", which is this element's own type — Order.
        var mine = await CompleteAsync(@"ThisKey=""");
        Assert.Equal(["Id", "CustomerId"], mine);

        // The same caret asking about the other end resolves through Type="Customer".
        string swapped = Model.Replace(@"ThisKey=""""", @"OtherKey=""""");
        var theirs = await CompleteAsync(@"OtherKey=""", swapped);
        Assert.Equal(["Id", "Name"], theirs);
    }

    private async Task<string[]> CompleteAsync(string caretAfter, string? text = null)
    {
        var source = SourceText.From(text ?? Model);
        OpenDocumentStore.Open(_session, _path, source, 1);
        DbmlDocumentCache.Invalidate(_path);

        int index = (text ?? Model).IndexOf(caretAfter, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{caretAfter}' is not in the buffer");

        var caret = source.Lines.GetLinePosition(index + caretAfter.Length);

        var list = await new DbmlLanguage().CompletionAsync(
            new CompletionParams(
                new TextDocumentIdentifier(LspConverters.PathToUri(_path)),
                new Position(caret.Line, caret.Character)),
            new LspResolveCache(),
            default);

        return [.. list.Items.Select(item => item.Label)];
    }
}
