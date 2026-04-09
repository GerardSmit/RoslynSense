using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Tests for the RenameSymbol tool.
/// Uses temporary copies of fixture files so renames don't break other tests.
/// </summary>
public class RenameSymbolToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _projectFile;
    private readonly string _calculatorFile;

    public RenameSymbolToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"RenameTest_{Guid.NewGuid():N}");
        CopyDirectory(FixturePaths.SampleProjectDir, _tempDir);
        _projectFile = Path.Combine(_tempDir, "SampleProject.csproj");
        _calculatorFile = Path.Combine(_tempDir, "Calculator.cs");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            string dirName = Path.GetFileName(dir);
            if (dirName is "obj" or "bin") continue;
            CopyDirectory(dir, Path.Combine(destDir, dirName));
        }
    }

    [Fact]
    public async Task WhenEmptyFilePathThenReturnsError()
    {
        var result = await RoslynMCP.Tools.RenameSymbolTool.RenameSymbol(
            "", "var x = [|Add|](1, 2);", "Sum");
        Assert.StartsWith("Error: File path cannot be empty", result);
    }

    [Fact]
    public async Task WhenEmptyMarkupThenReturnsError()
    {
        var result = await RoslynMCP.Tools.RenameSymbolTool.RenameSymbol(
            _calculatorFile, "", "Sum");
        Assert.StartsWith("Error: markupSnippet cannot be empty", result);
    }

    [Fact]
    public async Task WhenEmptyNewNameThenReturnsError()
    {
        var result = await RoslynMCP.Tools.RenameSymbolTool.RenameSymbol(
            _calculatorFile, "var x = [|Add|](1, 2);", "");
        Assert.StartsWith("Error: newName cannot be empty", result);
    }

    [Fact]
    public async Task WhenInvalidIdentifierThenReturnsError()
    {
        var result = await RoslynMCP.Tools.RenameSymbolTool.RenameSymbol(
            _calculatorFile, "var x = [|Add|](1, 2);", "123invalid");
        Assert.Contains("not a valid C# identifier", result);
    }

    [Fact]
    public async Task WhenFileNotFoundThenReturnsError()
    {
        var result = await RoslynMCP.Tools.RenameSymbolTool.RenameSymbol(
            @"C:\nonexistent\file.cs", "var x = [|Add|](1, 2);", "Sum");
        Assert.Contains("does not exist", result);
    }

    [Fact]
    public async Task WhenInvalidMarkupThenReturnsError()
    {
        var result = await RoslynMCP.Tools.RenameSymbolTool.RenameSymbol(
            _calculatorFile, "no markers here", "Sum");
        Assert.Contains("Invalid markup", result);
    }

    [Fact]
    public async Task WhenDryRunThenDoesNotModifyFiles()
    {
        string originalContent = await File.ReadAllTextAsync(_calculatorFile);

        var result = await RoslynMCP.Tools.RenameSymbolTool.RenameSymbol(
            _calculatorFile, "public int [|Add|](int a, int b)", "Sum", dryRun: true);

        Assert.Contains("Rename:", result);
        Assert.Contains("Preview", result);

        string afterContent = await File.ReadAllTextAsync(_calculatorFile);
        Assert.Equal(originalContent, afterContent);
    }

    [Fact]
    public async Task WhenRenamingMethodThenUpdatesFileOnDisk()
    {
        var result = await RoslynMCP.Tools.RenameSymbolTool.RenameSymbol(
            _calculatorFile, "public int [|Add|](int a, int b)", "Sum");

        Assert.Contains("Rename:", result);
        Assert.Contains("Sum", result);
        Assert.Contains("Applied", result);

        string content = await File.ReadAllTextAsync(_calculatorFile);
        Assert.Contains("Sum", content);
        Assert.DoesNotContain("public int Add(", content);
    }

    [Fact]
    public async Task WhenSameNameThenReturnsAlreadyHasName()
    {
        var result = await RoslynMCP.Tools.RenameSymbolTool.RenameSymbol(
            _calculatorFile, "public int [|Add|](int a, int b)", "Add");

        Assert.Contains("already has the requested name", result);
    }

    [Fact]
    public async Task WhenVerbatimIdentifierThenAccepted()
    {
        var result = await RoslynMCP.Tools.RenameSymbolTool.RenameSymbol(
            _calculatorFile, "public int [|Add|](int a, int b)", "@Sum", dryRun: true);

        Assert.Contains("Rename:", result);
    }

    // --- ASPX directive replacement tests (unit tests on internal helpers) ---

    [Fact]
    public void ReplaceDirectiveAttribute_ReplacesInheritsValue()
    {
        var input = """<%@ Page Language="C#" Inherits="MyNamespace.OldPage" %>""";
        var result = RoslynMCP.Tools.WebForms.AspxRename.ReplaceDirectiveAttribute(
            input, "Inherits", "MyNamespace.OldPage", "MyNamespace.NewPage");
        Assert.Contains("MyNamespace.NewPage", result);
        Assert.DoesNotContain("OldPage", result);
    }

    [Fact]
    public void ReplaceDirectiveAttribute_WhenNoMatchThenUnchanged()
    {
        var input = """<%@ Page Language="C#" Inherits="Other.Class" %>""";
        var result = RoslynMCP.Tools.WebForms.AspxRename.ReplaceDirectiveAttribute(
            input, "Inherits", "MyNamespace.OldPage", "MyNamespace.NewPage");
        Assert.Equal(input, result);
    }

    [Fact]
    public void ReplaceCodeBehindFileName_ReplacesFileName()
    {
        var input = """<%@ Page CodeBehind="Default.aspx.cs" Inherits="Default" %>""";
        var result = RoslynMCP.Tools.WebForms.AspxRename.ReplaceCodeBehindFileName(
            input, "Default", "HomePage");
        Assert.Contains("HomePage.aspx.cs", result);
    }

    [Fact]
    public void ReplaceCodeBehindFileName_WhenCodeFileThenAlsoReplaces()
    {
        var input = """<%@ Page CodeFile="Default.aspx.cs" %>""";
        var result = RoslynMCP.Tools.WebForms.AspxRename.ReplaceCodeBehindFileName(
            input, "Default", "HomePage");
        Assert.Contains("HomePage.aspx.cs", result);
    }

    [Fact]
    public void ReplaceInCodeBlocks_ReplacesWholeWordInExpressions()
    {
        var input = """<%= OldClass.GetValue() %> and <% OldClass.DoWork(); %>""";
        var result = RoslynMCP.Tools.WebForms.AspxRename.ReplaceInCodeBlocks(
            input, "OldClass", "NewClass");
        Assert.Contains("NewClass.GetValue()", result);
        Assert.Contains("NewClass.DoWork()", result);
        Assert.DoesNotContain("OldClass", result);
    }

    [Fact]
    public void ReplaceInCodeBlocks_DoesNotReplacePartialMatches()
    {
        var input = """<%= OldClassName.GetValue() %>""";
        var result = RoslynMCP.Tools.WebForms.AspxRename.ReplaceInCodeBlocks(
            input, "OldClass", "NewClass");
        // "OldClassName" should NOT be replaced because "OldClass" is not a whole word here
        Assert.Contains("OldClassName", result);
    }

    [Fact]
    public void ReplaceInCodeBlocks_LeavesHtmlUnchanged()
    {
        var input = """<div class="OldClass">Hello</div>""";
        var result = RoslynMCP.Tools.WebForms.AspxRename.ReplaceInCodeBlocks(
            input, "OldClass", "NewClass");
        // Not inside <% %> blocks, so should be unchanged
        Assert.Equal(input, result);
    }

    // --- Integration tests: ASPX file reference updates ---

    [Fact]
    public async Task WhenRenamingTypeThenAspxInheritsUpdated()
    {
        // Create a temp project with an ASPX file that references a class via Inherits
        var tempDir = Path.Combine(Path.GetTempPath(), $"RenameAspx_{Guid.NewGuid():N}");
        try
        {
            CopyDirectory(FixturePaths.AspxProjectDir, tempDir);

            // Add ASPX that uses PageHelper in code blocks
            var aspxFile = Path.Combine(tempDir, "TestPage.aspx");
            await File.WriteAllTextAsync(aspxFile,
                """<%@ Page Language="C#" Inherits="AspxProject.PageHelper" %><%= PageHelper.FormatDate(DateTime.Now) %>""");

            var csFile = Path.Combine(tempDir, "PageHelper.cs");

            var result = await RoslynMCP.Tools.RenameSymbolTool.RenameSymbol(
                csFile, "public class [|PageHelper|]", "PageUtility", dryRun: true,
                handlers: TestHandlers.Rename);

            Assert.Contains("Rename:", result);
            Assert.Contains("PageUtility", result);
            Assert.Contains("ASPX", result);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task WhenRenamingTypeThenFileIsRenamed()
    {
        // Create a temp project — rename Calculator class which matches Calculator.cs filename
        var tempDir = Path.Combine(Path.GetTempPath(), $"RenameFile_{Guid.NewGuid():N}");
        try
        {
            CopyDirectory(FixturePaths.SampleProjectDir, tempDir);
            var calcFile = Path.Combine(tempDir, "Calculator.cs");

            var result = await RoslynMCP.Tools.RenameSymbolTool.RenameSymbol(
                calcFile, "public class [|Calculator|]", "MathHelper");

            Assert.Contains("Rename:", result);
            Assert.Contains("MathHelper", result);
            Assert.Contains("Renamed Files", result);

            // Verify old file is gone and new file exists
            Assert.False(File.Exists(calcFile), "Old file should be renamed");
            Assert.True(File.Exists(Path.Combine(tempDir, "MathHelper.cs")), "New file should exist");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task WhenRenamingMethodThenNoFileRename()
    {
        // Renaming a method should not trigger file rename
        var result = await RoslynMCP.Tools.RenameSymbolTool.RenameSymbol(
            _calculatorFile, "public int [|Add|](int a, int b)", "Sum", dryRun: true);

        Assert.Contains("Rename:", result);
        Assert.DoesNotContain("Renamed Files", result);
    }

    [Fact]
    public void ReplaceDirectiveAttribute_HandlesCaseInsensitiveMatch()
    {
        var input = """<%@ Page INHERITS="MyApp.OldClass" %>""";
        var result = RoslynMCP.Tools.WebForms.AspxRename.ReplaceDirectiveAttribute(
            input, "INHERITS", "MyApp.OldClass", "MyApp.NewClass");
        Assert.Contains("MyApp.NewClass", result);
    }

    [Fact]
    public void ReplaceInCodeBlocks_HandlesBindingExpressions()
    {
        var input = """<%# OldClass.Eval("Name") %>""";
        var result = RoslynMCP.Tools.WebForms.AspxRename.ReplaceInCodeBlocks(
            input, "OldClass", "NewClass");
        Assert.Contains("NewClass.Eval", result);
    }
}
