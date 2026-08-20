using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Dbml;
using RoslynMCP.Languages.Dbml.Core;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// DBML0006: a <c>Type=</c> that names nothing the project can see.
/// </summary>
/// <remarks>
/// The one thing the pack reports that is not a question about the database. It is an error rather
/// than a warning because it is not a difference of opinion with SQL Server: the name is either a
/// type or it is not, the compilation says which, and when it is not the generated designer fails
/// to build with a message naming a type nobody typed, in a file nobody edits.
/// </remarks>
public class DbmlTypeDiagnosticsTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "roslynsense-dbml-types-" + Guid.NewGuid().ToString("N"));

    /// <remarks>
    /// Four shapes on purpose: one SqlMetal spelling that resolves, one C# spelling a person would
    /// hand-write, one name that resolves nowhere, and one on a function parameter — which the model
    /// reader never records, so a pass driven off the parsed model rather than off the raw
    /// attributes would miss it.
    /// </remarks>
    private const string Model = """
        <?xml version="1.0" encoding="utf-8"?>
        <Database Name="Shop" Class="ShopDataContext" xmlns="http://schemas.microsoft.com/linqtosql/dbml/2007">
          <Table Name="dbo.Orders" Member="Orders">
            <Type Name="Order">
              <Column Name="Id" Type="System.Int32" DbType="Int NOT NULL" IsPrimaryKey="true" />
              <Column Name="Total" Type="decimal" DbType="Money" />
              <Column Name="Status" Type="global::Nowhere.OrderStatus" DbType="Int" />
            </Type>
          </Table>
          <Function Name="dbo.usp_Restock" Method="Restock">
            <Parameter Name="cutoff" Parameter="cutoff" Type="Nowhere.Cutoff" DbType="Int" />
            <Return Type="System.Int32" />
          </Function>
        </Database>
        """;

    private const string Designer = """
        namespace System.Data.Linq
        {
            public class DataContext { }
            public class Table<T> { }
        }

        namespace Shop
        {
            public partial class ShopDataContext : System.Data.Linq.DataContext { }
        }
        """;

    private string ModelPath => Path.Combine(_directory, "Shop.dbml");

    private string DesignerPath => Path.Combine(_directory, "Shop.designer.cs");

    public DbmlTypeDiagnosticsTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(ModelPath, Model);
        File.WriteAllText(DesignerPath, Designer);

        DbmlDocumentCache.Clear();
        DbmlGeneratedIndex.Clear();
        DbmlSourceMappingService.Clear();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task OnlyTheNamesNothingDeclaresAreReported()
    {
        var diagnostics = await DiagnoseAsync();

        Assert.Equal(
            ["global::Nowhere.OrderStatus", "Nowhere.Cutoff"],
            diagnostics.Select(NameIn).Order(StringComparer.Ordinal).Reverse());

        Assert.All(diagnostics, d => Assert.Equal("DBML0006", d.Code));
        Assert.All(diagnostics, d => Assert.Equal(1, d.Severity));
    }

    [Fact]
    public async Task AFunctionParameterIsCheckedToo()
    {
        // The model reader never descends into <Parameter>, so a pass driven off the parsed model
        // would report the column and stay silent here.
        var diagnostics = await DiagnoseAsync();

        Assert.Contains(diagnostics, d => NameIn(d) == "Nowhere.Cutoff");
    }

    [Fact]
    public async Task TheSquiggleCoversTheTypeNameAndNothingElse()
    {
        var text = SourceText.From(Model);
        var bad = await DiagnoseAsync();
        var range = Assert.Single(bad, d => NameIn(d) == "global::Nowhere.OrderStatus").Range;

        var line = text.Lines[range.Start.Line];
        string covered = line.ToString()[range.Start.Character..range.End.Character];

        Assert.Equal("global::Nowhere.OrderStatus", covered);
    }

    [Fact]
    public async Task AModelWithNoCompilationBehindItSaysNothing()
    {
        // A checkout that has never been built has no designer in the project, and reporting
        // against a compilation that does not exist would paint every column red.
        Assert.Empty(await DiagnoseAsync(includeDesigner: false));
    }

    private static string NameIn(RoslynMCP.Lsp.Protocol.Diagnostic diagnostic) =>
        diagnostic.Message[1..diagnostic.Message.IndexOf('\'', 1)];

    private async Task<List<RoslynMCP.Lsp.Protocol.Diagnostic>> DiagnoseAsync(bool includeDesigner = true)
    {
        using var workspace = new AdhocWorkspace();
        var project = CreateProject(workspace, includeDesigner);

        var document = DbmlDocumentCache.Get(ModelPath)!;
        var index = await DbmlGeneratedIndex.GetAsync(ModelPath, project, default);
        var view = new DbmlView(document, project, index);

        var diagnostics = new List<RoslynMCP.Lsp.Protocol.Diagnostic>();
        DbmlLanguage.AddClrTypeDiagnostics(view, view.Text.Lines, diagnostics, default);
        return diagnostics;
    }

    private Project CreateProject(AdhocWorkspace workspace, bool includeDesigner)
    {
        var projectId = ProjectId.CreateNewId();

        var info = ProjectInfo.Create(
            projectId, VersionStamp.Create(), "Shop", "Shop", LanguageNames.CSharp,
            filePath: Path.Combine(_directory, "Shop.csproj"),
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);

        var solution = workspace.CurrentSolution.AddProject(info);

        if (includeDesigner)
        {
            solution = solution.AddDocument(
                DocumentId.CreateNewId(projectId), "Shop.designer.cs",
                SourceText.From(Designer), filePath: DesignerPath);
        }

        return solution.GetProject(projectId)!;
    }
}
