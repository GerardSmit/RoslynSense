using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Services;
using RoslynMCP.Tools;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>The editor-context bridge: what the user is looking at, mirrored for the AI.</summary>
public class EditorContextTests
{
    [Fact]
    public void ContextRoundTripsThroughTheStore()
    {
        string solution = Path.Combine(
            Path.GetTempPath(), $"editor-context-{Guid.NewGuid():N}", "App.sln");

        EditorContextStore.Write(solution, new EditorContextStore.Context(
            ActiveFile: @"C:\src\Orders.cs",
            Line: 41,
            Character: 8,
            EnclosingSymbol: "OrderCalculator.Total",
            SelectionText: "total += lineTotal;",
            OpenFiles: [@"C:\src\Orders.cs"],
            DirtyFiles: [@"C:\src\Orders.cs"],
            Diagnostics: [new EditorContextStore.VisibleDiagnostic("Error", "CS0103", "The name 'x' does not exist", 41)],
            UpdatedAtUtc: DateTime.UtcNow));

        try
        {
            var context = EditorContextStore.Read(solution);

            Assert.NotNull(context);
            Assert.Equal("OrderCalculator.Total", context!.EnclosingSymbol);
            Assert.Equal(41, context.Line);
            Assert.Single(context.Diagnostics);
            Assert.Equal("CS0103", context.Diagnostics[0].Code);
        }
        finally
        {
            EditorContextStore.Clear(solution);
        }

        Assert.Null(EditorContextStore.Read(solution));
    }

    [Fact]
    public void ReportResolvesAWorkspaceFolderToItsSolution()
    {
        // The extension often knows only its workspace folder; the key has to match what MCP
        // tools derive from their working directory or the two never meet.
        EditorContextHandler.Report(new EditorContextParams(
            SolutionPath: FixturePaths.MultiSolutionDir,
            ActiveFile: @"C:\x\Program.cs",
            Line: 3,
            Character: 1,
            EnclosingSymbol: "Program.Main",
            SelectionText: null,
            OpenFiles: [@"C:\x\Program.cs"],
            DirtyFiles: [],
            Diagnostics: []));

        var context = EditorContextStore.ReadNearest(FixturePaths.MultiSolutionDir);

        Assert.NotNull(context);
        Assert.Equal("Program.Main", context!.EnclosingSymbol);
    }

    [Fact]
    public void ToolReportsPlainlyWhenNoEditorHasReported() =>
        Assert.Contains("No editor is connected",
            EditorContextTool.Format(null, new MarkdownFormatter()));

    [Fact]
    public void StaleContextIsReportedAsStaleRatherThanUsed()
    {
        var stale = new EditorContextStore.Context(
            @"C:\x\Old.cs", 1, 1, "Old.Method", null, [], [], [],
            UpdatedAtUtc: DateTime.UtcNow.AddHours(-9));

        string result = EditorContextTool.Format(stale, new MarkdownFormatter());

        // Answering from an hours-old cursor position produces confidently wrong answers.
        Assert.Contains("stale", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Old.Method", result);
    }

    [Fact]
    public void FreshContextRendersTheCursorAndEnclosingSymbol()
    {
        var context = new EditorContextStore.Context(
            @"C:\src\Orders.cs", 41, 8, "OrderCalculator.Total", "total += lineTotal;",
            OpenFiles: [@"C:\src\Orders.cs"],
            DirtyFiles: [@"C:\src\Orders.cs"],
            Diagnostics: [new EditorContextStore.VisibleDiagnostic("Error", "CS0103", "missing", 41)],
            UpdatedAtUtc: DateTime.UtcNow);

        string result = EditorContextTool.Format(context, new MarkdownFormatter());

        Assert.Contains("OrderCalculator.Total", result);
        Assert.Contains("line 42", result); // 0-based internally, 1-based for humans
        Assert.Contains("total += lineTotal;", result);
        Assert.Contains("CS0103", result);
    }
}
