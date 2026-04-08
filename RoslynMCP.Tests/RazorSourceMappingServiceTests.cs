using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

public class RazorSourceMappingServiceTests
{
    [Fact]
    public void IsRazorFile_DetectsRazorExtensions()
    {
        Assert.True(RazorSourceMappingService.IsRazorFile("Component.razor"));
        Assert.True(RazorSourceMappingService.IsRazorFile("Page.cshtml"));
        Assert.True(RazorSourceMappingService.IsRazorFile("VIEW.RAZOR"));
        Assert.True(RazorSourceMappingService.IsRazorFile("_Layout.CSHTML"));
        Assert.False(RazorSourceMappingService.IsRazorFile("file.cs"));
        Assert.False(RazorSourceMappingService.IsRazorFile("page.aspx"));
        Assert.False(RazorSourceMappingService.IsRazorFile("style.css"));
    }

    [Fact]
    public void MapGeneratedToRazor_ReturnsNullForUnmappedLine()
    {
        var sourceMap = new RazorSourceMap([], []);
        var result = RazorSourceMappingService.MapGeneratedToRazor(sourceMap, "Generated.razor.g.cs", 10);
        Assert.Null(result);
    }

    [Fact]
    public void MapGeneratedToRazor_ExcludesEndLine()
    {
        // GeneratedEndLine is exclusive — line 20 itself should NOT be mapped
        var mappings = new List<RazorLineMapping>
        {
            new("gen.g.cs", GeneratedStartLine: 10, GeneratedEndLine: 20,
                RazorFilePath: "Page.razor", RazorLine: 5)
        };
        var sourceMap = new RazorSourceMap(mappings, []);

        // Line 19 (last included) should map
        var included = RazorSourceMappingService.MapGeneratedToRazor(sourceMap, "gen.g.cs", 19);
        Assert.NotNull(included);
        Assert.Equal(14, included.Line); // 5 + (19 - 10) = 14

        // Line 20 (exclusive end) should NOT map
        var excluded = RazorSourceMappingService.MapGeneratedToRazor(sourceMap, "gen.g.cs", 20);
        Assert.Null(excluded);
    }

    [Fact]
    public void MapGeneratedToRazor_ReturnsMappedLocationForKnownLine()
    {
        var mappings = new List<RazorLineMapping>
        {
            new(
                GeneratedFilePath: "Generated.razor.g.cs",
                GeneratedStartLine: 10,
                GeneratedEndLine: 20,
                RazorFilePath: "Component.razor",
                RazorLine: 5)
        };
        var sourceMap = new RazorSourceMap(mappings, []);

        var result = RazorSourceMappingService.MapGeneratedToRazor(sourceMap, "Generated.razor.g.cs", 15);

        Assert.NotNull(result);
        Assert.Equal("Component.razor", result.RazorFilePath);
        Assert.Equal(10, result.Line); // 5 + (15 - 10) = 10
    }

    [Fact]
    public void MapGeneratedToRazor_UseMostSpecificMapping()
    {
        var mappings = new List<RazorLineMapping>
        {
            new("gen.g.cs", GeneratedStartLine: 5, GeneratedEndLine: 30, RazorFilePath: "A.razor", RazorLine: 1),
            new("gen.g.cs", GeneratedStartLine: 15, GeneratedEndLine: 25, RazorFilePath: "B.razor", RazorLine: 10),
        };
        var sourceMap = new RazorSourceMap(mappings, []);

        // Line 18 falls in both mappings, but the more specific (later start) wins
        var result = RazorSourceMappingService.MapGeneratedToRazor(sourceMap, "gen.g.cs", 18);

        Assert.NotNull(result);
        Assert.Equal("B.razor", result.RazorFilePath);
        Assert.Equal(13, result.Line); // 10 + (18 - 15) = 13
    }

    [Fact]
    public void MapDiagnostic_ReturnsUnmappedWhenNotInSource()
    {
        var sourceMap = new RazorSourceMap([], []);
        var diag = Diagnostic.Create(
            new DiagnosticDescriptor("TEST01", "Test", "Test message", "Test", DiagnosticSeverity.Error, true),
            Location.None);

        var result = RazorSourceMappingService.MapDiagnostic(sourceMap, diag);

        Assert.NotNull(result);
        Assert.Null(result.MappedLocation);
    }

    [Fact]
    public void MapDiagnostic_MapsSourceFileDiagnostic()
    {
        var mappings = new List<RazorLineMapping>
        {
            new("Generated.razor.g.cs", GeneratedStartLine: 10, GeneratedEndLine: 20,
                RazorFilePath: "Page.razor", RazorLine: 3)
        };
        var sourceMap = new RazorSourceMap(mappings, []);

        // Create a diagnostic at line 14 (0-indexed 13) in the generated file
        var sourceText = SourceText.From(string.Join("\n", Enumerable.Repeat("// line", 30)));
        var tree = CSharpSyntaxTree.ParseText(sourceText, path: "Generated.razor.g.cs");
        var lineSpan = tree.GetText().Lines[13].Span;
        var location = Location.Create(tree, lineSpan);

        var diag = Diagnostic.Create(
            new DiagnosticDescriptor("CS0001", "Error", "Test error", "Compiler", DiagnosticSeverity.Error, true),
            location);

        var result = RazorSourceMappingService.MapDiagnostic(sourceMap, diag);

        Assert.NotNull(result);
        Assert.NotNull(result.MappedLocation);
        Assert.Equal("Page.razor", result.MappedLocation.RazorFilePath);
        // Line 14 (1-indexed) maps: RazorLine(3) + (14 - 10) = 7
        Assert.Equal(7, result.MappedLocation.Line);
    }

    [Fact]
    public async Task BuildSourceMapAsync_EmptyProject_ReturnsEmptyMap()
    {
        // Create a minimal project with no source-generated documents
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("Test", LanguageNames.CSharp);

        var sourceMap = await RazorSourceMappingService.BuildSourceMapAsync(project);

        Assert.NotNull(sourceMap);
        Assert.Empty(sourceMap.Mappings);
    }

    [Fact]
    public void ParseLineDirectives_StandardSyntax()
    {
        var code = string.Join("\n", new[]
        {
            "// generated preamble",          // line 0 (0-indexed)
            "#line 5 \"Component.razor\"",     // line 1
            "var x = 1;",                      // line 2 → razor line 5
            "var y = 2;",                      // line 3 → razor line 6
            "#line hidden",                    // line 4
            "// hidden code",                  // line 5
        });
        var text = SourceText.From(code);

        var mappings = RazorSourceMappingService.ParseLineDirectives("gen.g.cs", text);

        Assert.Single(mappings);
        var m = mappings[0];
        Assert.Equal("Component.razor", m.RazorFilePath);
        Assert.Equal(5, m.RazorLine);
        Assert.Equal(3, m.GeneratedStartLine); // 1-indexed line after the directive (0-indexed 1 → 1-indexed 2 + 1 = 3)
        Assert.Equal(5, m.GeneratedEndLine);    // exclusive: 1-indexed line of #line hidden (0-indexed 4 → 5)
    }

    [Fact]
    public void ParseLineDirectives_EnhancedSyntax_CapturesStartLine()
    {
        // C# 10+ enhanced #line directive:
        // #line (startLine, startCol) - (endLine, endCol) charOffset "file"
        var code = string.Join("\n", new[]
        {
            "#line (5, 20) - (5, 32) 84 \"Counter.razor\"",  // line 0
            "var x = 1;",                                      // line 1 → razor line 5
            "#line hidden",                                    // line 2
        });
        var text = SourceText.From(code);

        var mappings = RazorSourceMappingService.ParseLineDirectives("gen.g.cs", text);

        Assert.Single(mappings);
        var m = mappings[0];
        Assert.Equal("Counter.razor", m.RazorFilePath);
        Assert.Equal(5, m.RazorLine);   // Should be 5 (startLine), NOT 84 (charOffset)
        Assert.Equal(2, m.GeneratedStartLine); // 1-indexed next line
        Assert.Equal(3, m.GeneratedEndLine);   // exclusive: #line hidden at 1-indexed line 3
    }

    [Fact]
    public void ParseLineDirectives_EnhancedSyntax_WithoutCharOffset()
    {
        // Enhanced directive without the optional character offset
        var code = string.Join("\n", new[]
        {
            "#line (10, 1) - (10, 15) \"Page.razor\"",  // line 0
            "var z = 3;",                                 // line 1 → razor line 10
        });
        var text = SourceText.From(code);

        var mappings = RazorSourceMappingService.ParseLineDirectives("gen.g.cs", text);

        Assert.Single(mappings);
        var m = mappings[0];
        Assert.Equal("Page.razor", m.RazorFilePath);
        Assert.Equal(10, m.RazorLine);
    }

    [Fact]
    public void ParseLineDirectives_FinalMapping_IncludesLastLine()
    {
        // When file ends without #line hidden/default, the final mapping should include the last line
        var code = string.Join("\n", new[]
        {
            "#line 1 \"App.razor\"",  // line 0
            "var a = 1;",             // line 1 → razor line 1
            "var b = 2;",             // line 2 → razor line 2 (LAST LINE)
        });
        var text = SourceText.From(code);

        var mappings = RazorSourceMappingService.ParseLineDirectives("gen.g.cs", text);

        Assert.Single(mappings);
        var m = mappings[0];

        // Last line is 1-indexed 3. With exclusive end = Lines.Count + 1 = 4,
        // querying line 3 should work: 3 >= 4 → false → included
        var sourceMap = new RazorSourceMap(mappings, []);
        var result = RazorSourceMappingService.MapGeneratedToRazor(sourceMap, "gen.g.cs", 3);
        Assert.NotNull(result);
        Assert.Equal("App.razor", result.RazorFilePath);
        Assert.Equal(2, result.Line); // 1 + (3 - 2) = 2
    }
}
