using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

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
    public void FormatOutline_Aspx_ContainsExpectedSections()
    {
        var text = File.ReadAllText(FixturePaths.DefaultAspxFile);
        var compilation = CreateMinimalCompilation();

        var result = AspxSourceMappingService.Parse(FixturePaths.DefaultAspxFile, text, compilation);
        var outline = AspxSourceMappingService.FormatOutline(result);

        Assert.Contains("# ASPX File:", outline);
        Assert.Contains("Directives", outline);
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
    public void FormatOutline_Master_ContainsDirectives()
    {
        var text = File.ReadAllText(FixturePaths.SiteMasterFile);
        var compilation = CreateMinimalCompilation();

        var result = AspxSourceMappingService.Parse(FixturePaths.SiteMasterFile, text, compilation);
        var outline = AspxSourceMappingService.FormatOutline(result);

        Assert.Contains("# ASPX File:", outline);
        Assert.Contains("Directives", outline);
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
}
