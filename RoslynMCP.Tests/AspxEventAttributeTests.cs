using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMCP.Languages.WebForms.Core;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// <c>On…</c> attributes on a control.
/// </summary>
/// <remarks>
/// The control type says whether the attribute is an event; the code-behind says only whether the
/// handler exists. Confusing the two made a page whose <c>Inherits</c> class could not be resolved
/// — every page in a project that failed to load — report each of its handlers as a property the
/// control does not have.
/// </remarks>
public class AspxEventAttributeTests
{
    private const string PropertyNotFound = "WFC0002";
    private const string EventHandlerNotFound = "WFC0008";

    private static Compilation Compilation(string? codeBehind = null)
    {
        string stubs = File.ReadAllText(Path.Combine(FixturePaths.AspxProjectDir, "SystemWebStubs.cs"));
        string runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

        var trees = new List<Microsoft.CodeAnalysis.SyntaxTree> { CSharpSyntaxTree.ParseText(stubs) };
        if (codeBehind is not null)
            trees.Add(CSharpSyntaxTree.ParseText(codeBehind));

        return CSharpCompilation.Create("Events", trees,
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static string[] Report(string markup, string? codeBehind = null) =>
        AspxSourceMappingService.Parse(@"C:\site\Controls\Events.ascx", markup, Compilation(codeBehind))
            .RawDiagnostics.Select(d => d.Descriptor.Id).ToArray();

    private const string Markup = """
        <%@ Control Language="C#" Inherits="Missing.Namespace.NoSuchClass" %>
        <asp:Repeater runat="server" ID="Controls" OnItemDataBound="Controls_ItemDataBound">
            <ItemTemplate><span>x</span></ItemTemplate>
        </asp:Repeater>
        """;

    [Fact]
    public void AnEventIsNotAMissingPropertyWhenTheCodeBehindCannotBeResolved()
    {
        // The Repeater declares ItemDataBound, so OnItemDataBound is an event no matter what the
        // Inherits names. Nothing here can say whether the handler exists, so nothing is claimed.
        var reported = Report(Markup);

        Assert.DoesNotContain(PropertyNotFound, reported);
        Assert.DoesNotContain(EventHandlerNotFound, reported);

        // Said once, on the Inherits value, rather than as a symptom on every handler below it.
        Assert.Contains("WFC0010", reported);
    }

    [Fact]
    public void AMissingHandlerIsStillReportedWhenTheCodeBehindIsKnown()
    {
        // The other half: with a code-behind that resolves and no such method, the handler name is
        // reported — which is what arms the quick fix that generates it.
        var reported = Report(
            Markup.Replace("Missing.Namespace.NoSuchClass", "Site.Settings"),
            """
            namespace Site
            {
                public partial class Settings : System.Web.UI.UserControl
                {
                }
            }
            """);

        Assert.Contains(EventHandlerNotFound, reported);
        Assert.DoesNotContain(PropertyNotFound, reported);
    }

    [Fact]
    public void AnAttributeThatIsNeitherEventNorPropertyIsStillReported()
    {
        // The fall-through still has to work for what it was for.
        var reported = Report("""
            <%@ Control Language="C#" Inherits="Missing.Namespace.NoSuchClass" %>
            <asp:Label runat="server" NoSuchThing="x" />
            """);

        Assert.Contains(PropertyNotFound, reported);
    }
}
