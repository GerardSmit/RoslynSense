using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services.Refactoring;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Change Signature and Move Type to File, driven against a real workspace.
/// </summary>
/// <remarks>
/// Both reach past the file in front of the user — a reordered parameter has to land at every call
/// site, an override and an XML doc tag — so the assertions are about the <em>other</em> files.
/// A refactoring that only fixed the declaration would pass a test that looked at the declaration.
/// </remarks>
public class RefactoringTests
{
    private const string Library = """
        namespace Sample
        {
            public class Calculator
            {
                /// <summary>Adds.</summary>
                /// <param name="left">Left.</param>
                /// <param name="right">Right.</param>
                public virtual int Add(int left, int right)
                {
                    return left + right;
                }
            }

            public class Doubler : Calculator
            {
                public override int Add(int left, int right)
                {
                    return (left + right) * 2;
                }
            }
        }
        """;

    private const string Caller = """
        namespace Sample
        {
            public class Consumer
            {
                public int Use()
                {
                    var calculator = new Calculator();
                    return calculator.Add(1, 2);
                }
            }
        }
        """;

    [Fact]
    public async Task ReorderingParametersRewritesOverridesAndCallSites()
    {
        using var workspace = CreateWorkspace(("Calculator.cs", Library), ("Consumer.cs", Caller));
        var document = DocumentNamed(workspace, "Calculator.cs");

        var result = await RefactoringService.ChangeSignatureAsync(
            document, await PositionOfAsync(document, "Add(int left"), [1, 0]);

        Assert.True(result.Ok, result.Message);

        string library = await TextOfAsync(workspace, "Calculator.cs");
        string caller = await TextOfAsync(workspace, "Consumer.cs");

        Assert.Contains("Add(int right, int left)", library);
        // The override, which nothing in the edited declaration would have told us about.
        Assert.Equal(2, library.Split("Add(int right, int left)").Length - 1);
        // The call site, in another file entirely.
        Assert.Contains("Add(2, 1)", caller);
    }

    [Fact]
    public async Task RemovingAParameterTakesItOutOfTheCallSitesAndTheDocComment()
    {
        using var workspace = CreateWorkspace(("Calculator.cs", Library), ("Consumer.cs", Caller));
        var document = DocumentNamed(workspace, "Calculator.cs");

        var result = await RefactoringService.ChangeSignatureAsync(
            document, await PositionOfAsync(document, "Add(int left"), [0]);

        Assert.True(result.Ok, result.Message);

        string library = await TextOfAsync(workspace, "Calculator.cs");

        Assert.Contains("Add(int left)", library);
        // The <param> tag for the removed parameter goes too, or the doc comment now lies.
        Assert.DoesNotContain("""<param name="right">""", library);
        Assert.Contains("Add(1)", await TextOfAsync(workspace, "Consumer.cs"));
    }

    [Fact]
    public async Task AnOutOfRangeParameterIsRefusedBeforeAnythingIsWritten()
    {
        using var workspace = CreateWorkspace(("Calculator.cs", Library));
        var document = DocumentNamed(workspace, "Calculator.cs");

        var result = await RefactoringService.ChangeSignatureAsync(
            document, await PositionOfAsync(document, "Add(int left"), [0, 5]);

        Assert.False(result.Ok);
        Assert.Contains("out of range", result.Message);
        Assert.Contains("int left, int right", await TextOfAsync(workspace, "Calculator.cs"));
    }

    [Fact]
    public async Task APositionOnNothingCallableSaysSoRatherThanGuessing()
    {
        using var workspace = CreateWorkspace(("Calculator.cs", Library));
        var document = DocumentNamed(workspace, "Calculator.cs");

        var result = await RefactoringService.ChangeSignatureAsync(
            document, await PositionOfAsync(document, "namespace Sample"), [0]);

        Assert.False(result.Ok);
    }

    [Fact]
    public async Task MovingATypeGivesItItsOwnFileAndLeavesTheRest()
    {
        using var workspace = CreateWorkspace(("Calculator.cs", Library));
        var document = DocumentNamed(workspace, "Calculator.cs");

        var result = await RefactoringService.MoveTypeToFileAsync(
            document, await PositionOfAsync(document, "class Doubler"));

        Assert.True(result.Ok, result.Message);
        Assert.Contains(result.ChangedFiles, f => f.Contains("Doubler") && f.Contains("(new)"));

        var solution = workspace.CurrentSolution;
        string moved = await TextOfAsync(workspace, "Doubler.cs");
        string remaining = await TextOfAsync(workspace, "Calculator.cs");

        Assert.Contains("class Doubler", moved);
        // Same namespace, or the move silently changes what the type is called.
        Assert.Contains("namespace Sample", moved);
        Assert.DoesNotContain("class Doubler", remaining);
        Assert.Contains("class Calculator", remaining);
    }

    // === helpers ===

    private static AdhocWorkspace CreateWorkspace(params (string Name, string Text)[] files)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();

        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId, VersionStamp.Create(), "Sample", "Sample", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]));

        foreach (var (name, text) in files)
        {
            solution = solution.AddDocument(
                DocumentId.CreateNewId(projectId), name,
                SourceText.From(text, System.Text.Encoding.UTF8),
                filePath: Path.Combine(Path.GetTempPath(), "refactor-tests", name));
        }

        Assert.True(workspace.TryApplyChanges(solution));
        return workspace;
    }

    private static Document DocumentNamed(AdhocWorkspace workspace, string name) =>
        workspace.CurrentSolution.Projects.Single().Documents.Single(d => d.Name == name);

    private static async Task<int> PositionOfAsync(Document document, string marker)
    {
        string text = (await document.GetTextAsync()).ToString();
        int index = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{marker}' is not in {document.Name}.");

        // Land on the identifier rather than the keyword before it, which is where a caller
        // pointing at a member's name would be.
        int nameOffset = marker.LastIndexOf(' ') + 1;
        return index + nameOffset;
    }

    private static async Task<string> TextOfAsync(AdhocWorkspace workspace, string name)
    {
        var document = workspace.CurrentSolution.Projects.Single().Documents
            .SingleOrDefault(d => d.Name == name);

        Assert.True(document is not null, $"'{name}' is not in the solution.");
        return (await document!.GetTextAsync()).ToString();
    }
}
