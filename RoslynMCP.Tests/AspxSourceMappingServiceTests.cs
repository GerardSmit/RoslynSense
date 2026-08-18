using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMCP.Services;
using Xunit;
using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Languages.WebForms.Tools;

namespace RoslynMCP.Tests;

/// <summary>
/// Covers the parse half of the WebForms engine, and the caret-to-symbol resolution the MCP
/// tools now share with the editor.
/// </summary>
/// <remarks>
/// In the shared-state collection because resolving a caret loads the fixture project through
/// <see cref="WorkspaceService"/>, which is a process-wide cache.
/// </remarks>
[Collection(SharedState.Name)]
public class AspxSourceMappingServiceTests
{
    private static Compilation CreateMinimalCompilation()
    {
        var tree = CSharpSyntaxTree.ParseText("class Dummy {}");
        return CSharpCompilation.Create("TestAssembly",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    [Fact]
    public void IsAspxFile_DetectsAspxExtensions()
    {
        Assert.True(AspxSourceMappingService.IsAspxFile("page.aspx"));
        Assert.True(AspxSourceMappingService.IsAspxFile("control.ascx"));
        Assert.True(AspxSourceMappingService.IsAspxFile("service.asmx"));
        Assert.True(AspxSourceMappingService.IsAspxFile("global.asax"));
        Assert.True(AspxSourceMappingService.IsAspxFile("handler.ashx"));
        Assert.True(AspxSourceMappingService.IsAspxFile("site.master"));
        Assert.False(AspxSourceMappingService.IsAspxFile("file.cs"));
        Assert.False(AspxSourceMappingService.IsAspxFile("page.razor"));
        Assert.False(AspxSourceMappingService.IsAspxFile("page.html"));
    }

    // --- Default.aspx tests ---

    [Fact]
    public void Parse_Aspx_ReturnsDirectives()
    {
        var text = File.ReadAllText(FixturePaths.DefaultAspxFile);
        var compilation = CreateMinimalCompilation();

        var result = AspxSourceMappingService.Parse(FixturePaths.DefaultAspxFile, text, compilation);

        Assert.NotNull(result);
        Assert.True(result.Directives.Count > 0, "Should find at least one directive");
        var pageDirective = result.Directives.FirstOrDefault(d =>
            d.Type.Equals("Page", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(pageDirective);
    }

    [Fact]
    public void Parse_Aspx_ReturnsExpressions()
    {
        var text = File.ReadAllText(FixturePaths.DefaultAspxFile);
        var compilation = CreateMinimalCompilation();

        var result = AspxSourceMappingService.Parse(FixturePaths.DefaultAspxFile, text, compilation);

        Assert.True(result.Expressions.Count > 0, "Should find inline expressions");
    }

    [Fact]
    public void Parse_Aspx_ReturnsCodeBlocks()
    {
        var text = File.ReadAllText(FixturePaths.DefaultAspxFile);
        var compilation = CreateMinimalCompilation();

        var result = AspxSourceMappingService.Parse(FixturePaths.DefaultAspxFile, text, compilation);

        Assert.True(result.CodeBlocks.Count > 0, "Should find code blocks");
    }

    [Fact]
    public async Task Outline_Aspx_ContainsExpectedSections()
    {
        var outline = await AspxOutline.FormatAsync(FixturePaths.DefaultAspxFile, default);

        Assert.Contains("**ASPX File:", outline);
        Assert.Contains("Directives", outline);
        Assert.Contains("Server Controls", outline);
    }

    [Fact]
    public void MapPosition_Aspx_ReturnsNullForNonCodeLocation()
    {
        var text = File.ReadAllText(FixturePaths.DefaultAspxFile);
        var compilation = CreateMinimalCompilation();

        var result = AspxSourceMappingService.Parse(FixturePaths.DefaultAspxFile, text, compilation);

        // Line 1, column 1 is in the directive, not an expression or code block
        var location = AspxSourceMappingService.MapPosition(result, 1, 1);
        Assert.Null(location);
    }

    [Fact]
    public void MapPosition_Aspx_ReturnsExpressionForInlineCode()
    {
        var text = File.ReadAllText(FixturePaths.DefaultAspxFile);
        var compilation = CreateMinimalCompilation();

        var result = AspxSourceMappingService.Parse(FixturePaths.DefaultAspxFile, text, compilation);

        // Line 12 (0-indexed 11) contains: <%= DateTime.Now.ToString() %>
        // Find the expression that contains DateTime.Now
        var expr = result.Expressions.FirstOrDefault(e => e.Code.Contains("DateTime"));
        Assert.NotNull(expr);

        // Use the expression's own range to query MapPosition
        var location = AspxSourceMappingService.MapPosition(result, expr.Range.Start.Line, expr.Range.Start.Column);
        Assert.NotNull(location);
        Assert.Equal(AspxCodeLocationType.Expression, location.Type);
        Assert.Contains("DateTime", location.Code);
    }

    [Fact]
    public void MapPosition_Aspx_ReturnsCodeBlockForStatementBlock()
    {
        var text = File.ReadAllText(FixturePaths.DefaultAspxFile);
        var compilation = CreateMinimalCompilation();

        var result = AspxSourceMappingService.Parse(FixturePaths.DefaultAspxFile, text, compilation);

        // Line 17 (0-indexed 16) contains: <% if (IsPostBack) { %>
        var block = result.CodeBlocks.FirstOrDefault(b => b.Code.Contains("IsPostBack"));
        Assert.NotNull(block);

        var location = AspxSourceMappingService.MapPosition(result, block.Range.Start.Line, block.Range.Start.Column);
        Assert.NotNull(location);
        Assert.Equal(AspxCodeLocationType.CodeBlock, location.Type);
        Assert.Contains("IsPostBack", location.Code);
    }

    // --- .ascx (User Control) tests ---

    [Fact]
    public void Parse_Ascx_ReturnsControlDirective()
    {
        var text = File.ReadAllText(FixturePaths.HeaderControlFile);
        var compilation = CreateMinimalCompilation();

        var result = AspxSourceMappingService.Parse(FixturePaths.HeaderControlFile, text, compilation);

        Assert.NotNull(result);
        var controlDirective = result.Directives.FirstOrDefault(d =>
            d.Type.Equals("Control", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(controlDirective);
    }

    [Fact]
    public void Parse_Ascx_ReturnsExpressionsAndCodeBlocks()
    {
        var text = File.ReadAllText(FixturePaths.HeaderControlFile);
        var compilation = CreateMinimalCompilation();

        var result = AspxSourceMappingService.Parse(FixturePaths.HeaderControlFile, text, compilation);

        Assert.True(result.Expressions.Count > 0, "Should find expression (<%= Title %>)");
        Assert.True(result.CodeBlocks.Count > 0, "Should find server script block");
    }

    // --- .master (Master Page) tests ---

    [Fact]
    public void Parse_Master_ReturnsMasterDirective()
    {
        var text = File.ReadAllText(FixturePaths.SiteMasterFile);
        var compilation = CreateMinimalCompilation();

        var result = AspxSourceMappingService.Parse(FixturePaths.SiteMasterFile, text, compilation);

        Assert.NotNull(result);
        var masterDirective = result.Directives.FirstOrDefault(d =>
            d.Type.Equals("Master", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(masterDirective);
    }

    [Fact]
    public void Parse_Master_ReturnsExpressionsAndCodeBlocks()
    {
        var text = File.ReadAllText(FixturePaths.SiteMasterFile);
        var compilation = CreateMinimalCompilation();

        var result = AspxSourceMappingService.Parse(FixturePaths.SiteMasterFile, text, compilation);

        Assert.True(result.Expressions.Count > 0, "Should find year expression");
        Assert.True(result.CodeBlocks.Count > 0, "Should find authentication code block");
    }

    [Fact]
    public async Task Outline_Master_ContainsDirectives()
    {
        var outline = await AspxOutline.FormatAsync(FixturePaths.SiteMasterFile, default);

        Assert.Contains("**ASPX File:", outline);
        Assert.Contains("Directives", outline);
    }

    [Fact]
    public async Task Outline_NestsControlsInsideTheirParent()
    {
        // The outline is the editor's documentSymbol walk, so a control inside another control's
        // body is indented under it. The flat listing this replaced could not express that.
        var outline = await AspxOutline.FormatAsync(FixturePaths.RepeaterAspxFile, default);

        var lines = outline.Split('\n');
        int parent = Array.FindIndex(lines, l => l.Contains("**rptItems**", StringComparison.Ordinal));
        Assert.True(parent >= 0, $"Expected rptItems in the outline:\n{outline}");

        int parentIndent = Indent(lines[parent]);
        var nested = lines
            .Skip(parent + 1)
            .TakeWhile(l => l.TrimStart().StartsWith("- ", StringComparison.Ordinal) && Indent(l) > parentIndent);

        Assert.Contains(nested, l => l.Contains("btnAction", StringComparison.Ordinal));

        static int Indent(string line) => line.Length - line.TrimStart().Length;
    }

    // --- .asmx (Web Service) tests ---

    [Fact]
    public void Parse_Asmx_ReturnsDirective()
    {
        var text = File.ReadAllText(FixturePaths.DataServiceFile);
        var compilation = CreateMinimalCompilation();

        var result = AspxSourceMappingService.Parse(FixturePaths.DataServiceFile, text, compilation);

        Assert.NotNull(result);
        Assert.True(result.Directives.Count > 0, "Should find at least one directive");
        // WebService directive isn't in the parser's DirectiveType enum, so it may be "Unknown"
        var firstDirective = result.Directives[0];
        Assert.True(firstDirective.Attributes.ContainsKey("Language") || firstDirective.Attributes.ContainsKey("Class"),
            "WebService directive should have Language or Class attribute");
    }

    [Fact]
    public void Parse_Asmx_ReturnsCodeBlocks()
    {
        var text = File.ReadAllText(FixturePaths.DataServiceFile);
        var compilation = CreateMinimalCompilation();

        var result = AspxSourceMappingService.Parse(FixturePaths.DataServiceFile, text, compilation);

        Assert.True(result.CodeBlocks.Count > 0, "Should find code blocks");
    }

    // --- .ashx (Handler) tests ---

    [Fact]
    public void Parse_Ashx_ReturnsDirective()
    {
        var text = File.ReadAllText(FixturePaths.ImageHandlerFile);
        var compilation = CreateMinimalCompilation();

        var result = AspxSourceMappingService.Parse(FixturePaths.ImageHandlerFile, text, compilation);

        Assert.NotNull(result);
        Assert.True(result.Directives.Count > 0, "Should find at least one directive");
        var firstDirective = result.Directives[0];
        Assert.True(firstDirective.Attributes.ContainsKey("Language") || firstDirective.Attributes.ContainsKey("Class"),
            "WebHandler directive should have Language or Class attribute");
    }

    [Fact]
    public void Parse_Ashx_ReturnsCodeBlocks()
    {
        var text = File.ReadAllText(FixturePaths.ImageHandlerFile);
        var compilation = CreateMinimalCompilation();

        var result = AspxSourceMappingService.Parse(FixturePaths.ImageHandlerFile, text, compilation);

        Assert.True(result.CodeBlocks.Count > 0, "Should find code blocks");
    }

    // --- web.config control registration tests ---

    [Fact]
    public void LoadWebConfigNamespaces_WhenWebConfigExistsThenReturnsRegistrations()
    {
        var namespaces = AspxSourceMappingService.LoadWebConfigNamespaces(FixturePaths.AspxProjectDir);

        Assert.False(namespaces.IsDefaultOrEmpty, "Should find control registrations in web.config");
        Assert.Contains(namespaces, kvp => kvp.Key == "app" && kvp.Value == "AspxProject");
        Assert.Contains(namespaces, kvp => kvp.Key == "uc" && kvp.Value == "AspxProject.Controls");
    }

    [Fact]
    public void LoadWebConfigNamespaces_WhenNoWebConfigThenReturnsEmpty()
    {
        // SampleProject has no web.config
        var namespaces = AspxSourceMappingService.LoadWebConfigNamespaces(FixturePaths.SampleProjectDir);

        Assert.True(namespaces.IsDefaultOrEmpty, "Should return empty when no web.config exists");
    }

    [Fact]
    public void LoadWebConfigNamespaces_WhenNonExistentDirectoryThenReturnsEmpty()
    {
        var namespaces = AspxSourceMappingService.LoadWebConfigNamespaces(@"C:\nonexistent\directory");

        Assert.True(namespaces.IsDefaultOrEmpty);
    }

    [Fact]
    public void LoadWebConfigImports_WhenWebConfigHasPagesNamespacesThenReturnsThem()
    {
        var imports = AspxSourceMappingService.LoadWebConfigImports(FixturePaths.AspxProjectDir);

        Assert.Contains("AspxProject", imports);
        Assert.Contains("System.Collections.Generic", imports);
    }

    [Fact]
    public void LoadWebConfigImports_WhenNoWebConfigThenReturnsEmpty()
    {
        var imports = AspxSourceMappingService.LoadWebConfigImports(FixturePaths.SampleProjectDir);

        Assert.True(imports.IsDefaultOrEmpty);
    }

    [Fact]
    public void GetPageNamespaces_HonorsRemoveAndClear()
    {
        var imports = WebFormsCore.Nodes.RootNode.GetPageNamespaces("""
            <configuration>
              <system.web>
                <pages>
                  <namespaces>
                    <add namespace="First" />
                    <clear />
                    <add namespace="Kept" />
                    <add namespace="Dropped" />
                    <remove namespace="Dropped" />
                  </namespaces>
                </pages>
              </system.web>
            </configuration>
            """);

        Assert.Equal(["Kept"], imports.ToArray());
    }

    [Fact]
    public void Parse_WithImports_AddsThemToTheParseTree()
    {
        var text = File.ReadAllText(FixturePaths.DefaultAspxFile);
        var compilation = CreateMinimalCompilation();

        var result = AspxSourceMappingService.Parse(
            FixturePaths.DefaultAspxFile, text, compilation,
            rootDirectory: FixturePaths.AspxProjectDir,
            imports: ["Fixture.Helpers"]);

        Assert.NotNull(result.ParseTree);
        Assert.Contains("Fixture.Helpers", result.ParseTree!.Namespaces);
    }

    [Fact]
    public void Parse_WithWebConfigNamespaces_AcceptsNamespaces()
    {
        var text = File.ReadAllText(FixturePaths.DefaultAspxFile);
        var compilation = CreateMinimalCompilation();
        var namespaces = AspxSourceMappingService.LoadWebConfigNamespaces(FixturePaths.AspxProjectDir);

        // Should not throw when namespaces are provided
        var result = AspxSourceMappingService.Parse(
            FixturePaths.DefaultAspxFile, text, compilation,
            namespaces: namespaces,
            rootDirectory: FixturePaths.AspxProjectDir);

        Assert.NotNull(result);
        Assert.NotEmpty(result.Directives);
    }

    [Fact]
    public void Parse_WithRootDirectory_ResolvesRelativePaths()
    {
        var text = File.ReadAllText(FixturePaths.DefaultAspxFile);
        var compilation = CreateMinimalCompilation();

        // Passing rootDirectory enables @Register src="~/..." resolution
        var result = AspxSourceMappingService.Parse(
            FixturePaths.DefaultAspxFile, text, compilation,
            rootDirectory: FixturePaths.AspxProjectDir);

        Assert.NotNull(result);
    }

    // --- Caret resolution ---
    //
    // These used to run against AspxSourceMappingService.ResolveAspxSymbol, a second resolver
    // that matched the marked text against control names. There is now one: the snippet is
    // mapped to a file offset and handed to the same AspxSymbolResolver the editor uses.

    private static async Task<AspxHit?> ResolveAsync(
        string filePath, string markupSnippet, int? hintLine = null)
    {
        var document = await AspxDocumentService.GetAsync(filePath, default);
        Assert.NotNull(document);

        var markup = MarkupString.Parse(markupSnippet);
        var marked = AspxSourceMappingService.FindMarkedSpan(document!.Text, markup, hintLine);
        Assert.NotNull(marked);

        return AspxSymbolResolver.ResolveAt(document, marked!.Value.Start);
    }

    [Fact]
    public async Task ResolveAt_ControlTagName_ReturnsControlType()
    {
        var hit = await ResolveAsync(FixturePaths.DefaultAspxFile, "<[|asp:Label|] ID=\"lblTitle\"");

        Assert.Equal(AspxHitKind.ControlType, hit!.Kind);
        var symbol = Assert.IsAssignableFrom<INamedTypeSymbol>(hit.Symbol);
        Assert.Equal("Label", symbol.Name);
        Assert.Contains("System.Web.UI.WebControls", symbol.ContainingNamespace.ToDisplayString());
    }

    [Fact]
    public async Task ResolveAt_EventHandlerValue_ReturnsCodeBehindMethod()
    {
        var hit = await ResolveAsync(FixturePaths.DefaultAspxFile, "OnClick=\"[|BtnSubmit_Click|]\"");

        Assert.Equal(AspxHitKind.EventHandler, hit!.Kind);
        var symbol = Assert.IsAssignableFrom<IMethodSymbol>(hit.Symbol);
        Assert.Equal("BtnSubmit_Click", symbol.Name);
    }

    [Fact]
    public async Task ResolveAt_EventName_ReturnsEventSymbol()
    {
        var hit = await ResolveAsync(FixturePaths.DefaultAspxFile, "[|OnClick|]=\"BtnSubmit_Click\"");

        Assert.Equal(AspxHitKind.EventName, hit!.Kind);
        var symbol = Assert.IsAssignableFrom<IEventSymbol>(hit.Symbol);
        Assert.Equal("Click", symbol.Name);
    }

    [Fact]
    public async Task ResolveAt_PropertyName_ReturnsPropertySymbol()
    {
        var hit = await ResolveAsync(FixturePaths.DefaultAspxFile, "[|Text|]=\"Submit\"");

        Assert.Equal(AspxHitKind.PropertyName, hit!.Kind);
        Assert.Equal("Text", hit.Symbol!.Name);
    }

    [Fact]
    public async Task ResolveAt_InheritsDirective_ReturnsPageType()
    {
        var hit = await ResolveAsync(
            FixturePaths.DefaultAspxFile, "Inherits=\"[|AspxProject.DefaultPage|]\"");

        Assert.Equal(AspxHitKind.Inherits, hit!.Kind);
        var symbol = Assert.IsAssignableFrom<INamedTypeSymbol>(hit.Symbol);
        Assert.Equal("DefaultPage", symbol.Name);
    }

    [Fact]
    public async Task ResolveAt_NoMatch_ReturnsNull()
    {
        var document = await AspxDocumentService.GetAsync(FixturePaths.DefaultAspxFile, default);
        var markup = MarkupString.Parse("[|NonExistentThing|]");

        // Text that is not in the file has no offset to resolve at, which is the earlier of the
        // two ways this returns nothing.
        Assert.Null(AspxSourceMappingService.FindMarkedSpan(document!.Text, markup));
    }

    [Fact]
    public async Task ResolveAt_ControlIdValue_ReturnsCodeBehindField()
    {
        var hit = await ResolveAsync(FixturePaths.DefaultAspxFile, "ID=\"[|btnSubmit|]\"");

        Assert.Equal(AspxHitKind.ControlId, hit!.Kind);
        var symbol = Assert.IsAssignableFrom<IFieldSymbol>(hit.Symbol);
        Assert.Equal("btnSubmit", symbol.Name);
    }

    [Fact]
    public async Task ResolveAt_ControlIdValue_LabelField_ReturnsCodeBehindField()
    {
        var hit = await ResolveAsync(FixturePaths.DefaultAspxFile, "ID=\"[|lblTitle|]\"");

        Assert.Equal(AspxHitKind.ControlId, hit!.Kind);
        var symbol = Assert.IsAssignableFrom<IFieldSymbol>(hit.Symbol);
        Assert.Equal("lblTitle", symbol.Name);
    }

    [Fact]
    public async Task ResolveAt_TemplateNestedControlId_ReturnsNoSymbol()
    {
        // Controls inside a Repeater template have no code-behind field, so the ID resolves to
        // the attribute but to no symbol.
        var hit = await ResolveAsync(FixturePaths.RepeaterAspxFile, "ID=\"[|btnAction|]\"");

        Assert.Equal(AspxHitKind.ControlId, hit!.Kind);
        Assert.Null(hit.Symbol);
    }

    [Fact]
    public void FindControlNodeAtCursor_TemplateControl_ReturnsControlNode()
    {
        var (result, text) = ParseRepeaterAspxWithSystemWeb();
        var markup = MarkupString.Parse("ID=\"[|btnAction|]\"");

        var controlNode = AspxSourceMappingService.FindControlNodeAtCursor(result, text, markup);

        Assert.NotNull(controlNode);
        Assert.Equal("btnAction", controlNode.Id);
    }

    [Fact]
    public void IsControlInTemplate_TopLevelControl_ReturnsFalse()
    {
        var (result, _) = ParseRepeaterAspxWithSystemWeb();

        // rptItems is top-level (directly in the page, not inside any template)
        var rptItems = result.ParseTree!.AllChildren
            .OfType<WebFormsCore.Nodes.ControlNode>()
            .FirstOrDefault(c => string.Equals(c.Id, "rptItems", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(rptItems);
        Assert.False(AspxSourceMappingService.IsControlInTemplate(rptItems));
    }

    [Fact]
    public void IsControlInTemplate_InsideItemTemplate_ReturnsTrue()
    {
        var (result, _) = ParseRepeaterAspxWithSystemWeb();

        // btnAction is inside <ItemTemplate> — stored in TemplateNode.Children, not AllChildren
        var btnAction = result.ParseTree!.Templates
            .SelectMany(t => t.AllChildren)
            .OfType<WebFormsCore.Nodes.ControlNode>()
            .FirstOrDefault(c => string.Equals(c.Id, "btnAction", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(btnAction);
        Assert.True(AspxSourceMappingService.IsControlInTemplate(btnAction));
    }

    private static Compilation CreateRepeaterCompilation()
    {
        var stubsText = File.ReadAllText(Path.Combine(FixturePaths.AspxProjectDir, "SystemWebStubs.cs"));
        var codeBehindText = File.ReadAllText(FixturePaths.RepeaterCodeBehindFile);
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        return CSharpCompilation.Create("TestWithRepeater",
            [CSharpSyntaxTree.ParseText(stubsText), CSharpSyntaxTree.ParseText(codeBehindText)],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                assemblyIdentityComparer: DesktopAssemblyIdentityComparer.Default));
    }

    private static (AspxParseResult result, string text) ParseRepeaterAspxWithSystemWeb()
    {
        var text = File.ReadAllText(FixturePaths.RepeaterAspxFile);
        var compilation = CreateRepeaterCompilation();
        var result = AspxSourceMappingService.Parse(
            FixturePaths.RepeaterAspxFile, text, compilation,
            rootDirectory: FixturePaths.AspxProjectDir);
        return (result, text);
    }
}
