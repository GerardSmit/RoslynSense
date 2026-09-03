using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMCP.Languages.WebForms.Core;
using WebFormsCore.Nodes;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// A <c>&lt;script runat="server"&gt;</c> block written inside a control.
/// </summary>
/// <remarks>
/// The block declares members rather than markup, so the parser handles it apart from the element
/// tree and pushes nothing to close. Its <c>&lt;/script&gt;</c> still has to be consumed there: left
/// for the main loop it closed whatever container the script sat in, and every control after it was
/// attached to the wrong parent — which is why a Repeater's <c>OnItemDataBound</c> stopped
/// resolving and read as a property the type does not have.
/// </remarks>
public class AspxServerScriptTests
{
    private static Compilation Compilation()
    {
        string stubs = File.ReadAllText(Path.Combine(FixturePaths.AspxProjectDir, "SystemWebStubs.cs"));
        string runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

        return CSharpCompilation.Create("ServerScript",
            [CSharpSyntaxTree.ParseText(stubs)],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static AspxParseResult Parse(string markup) =>
        AspxSourceMappingService.Parse(@"C:\site\Controls\Script.ascx", markup, Compilation());

    private const string Markup = """
        <asp:Panel runat="server" ID="wrapper">
            <script runat="server">
                public string NameClass { get { return "col-md-4"; } }
            </script>
            <asp:Repeater runat="server" ID="menuItems" OnItemDataBound="menuItems_ItemDataBound">
                <ItemTemplate><span>x</span></ItemTemplate>
            </asp:Repeater>
        </asp:Panel>
        """;

    [Fact]
    public void AControlAfterAServerScriptStillResolvesItsEvents()
    {
        var result = Parse(Markup);

        // WFC0007 is "property not found": OnItemDataBound is an event on Repeater, and it only
        // read as a missing property while the control was parented to the wrong type.
        Assert.DoesNotContain(result.RawDiagnostics, d => d.Descriptor.Id == "WFC0007");
        Assert.DoesNotContain(result.RawDiagnostics, d => d.Descriptor.Id == "WFC0006");
    }

    [Fact]
    public void TheScriptDoesNotCloseTheControlAroundIt()
    {
        var result = Parse(Markup);

        var panel = AspxSymbolResolver.EnumerateControls(result.ParseTree!)
            .Single(c => c.Id == "wrapper");
        var repeater = AspxSymbolResolver.EnumerateControls(result.ParseTree!)
            .Single(c => c.Id == "menuItems");

        // The repeater is written after the script and inside the panel, so that is where it
        // belongs in the tree.
        Assert.Same(panel, repeater.Parent);
    }

    [Fact]
    public void TheScriptBodyIsStillCollected()
    {
        var result = Parse(Markup);

        Assert.Contains(result.ParseTree!.ScriptBlocks, s => s.Value.Contains("NameClass"));
    }
}
