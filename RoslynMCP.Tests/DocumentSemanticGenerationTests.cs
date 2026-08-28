using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The key every memo over a solution-wide query is versioned by. What is pinned here is the
/// direction the key must look in: a reference count for a symbol in project B is changed by an
/// edit in a project that <em>depends on</em> B — a new call site lives in the caller, not the
/// callee — so the key has to move for edits in B's dependents, and has to hold still for edits
/// in projects that cannot see B at all, or the memo either serves stale counts or serves nothing.
/// </summary>
public class DocumentSemanticGenerationTests
{
    [Fact]
    public async Task AnEditInAProjectThatDependsOnThisOneMovesTheGeneration()
    {
        var (solution, calleeDoc, callerDoc) = TwoProjectSolution();

        object before = await DocumentSemanticGeneration.ForAsync(
            solution.GetDocument(calleeDoc)!, default);

        // Unchanged solution: the same question gets the same key, or the memo never holds.
        object again = await DocumentSemanticGeneration.ForAsync(
            solution.GetDocument(calleeDoc)!, default);
        Assert.Equal(before, again);

        // The user's gesture: a new call site typed into a method body in the dependent project.
        // A body edit on purpose — Roslyn's semantic versions ignore those, and a call site is
        // exactly a body edit.
        var edited = solution.WithDocumentText(callerDoc, SourceText.From(
            """
            public static class Caller
            {
                public static void Run()
                {
                    Callee.Used();
                    Callee.Used();
                }
            }
            """));

        object after = await DocumentSemanticGeneration.ForAsync(
            edited.GetDocument(calleeDoc)!, default);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task AnEditInAProjectThatCannotSeeThisOneHoldsTheGenerationStill()
    {
        var (solution, calleeDoc, _) = TwoProjectSolution();

        // A third project with no reference in either direction: nothing in it can mention the
        // callee, so its edits must not cost the callee's file its kept answers.
        var strangerId = ProjectId.CreateNewId("Stranger");
        var strangerDoc = DocumentId.CreateNewId(strangerId);
        solution = solution
            .AddProject(strangerId, "Stranger", "Stranger", LanguageNames.CSharp)
            .AddMetadataReference(
                strangerId, MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddDocument(strangerDoc, "Stranger.cs", SourceText.From(
                "public static class Stranger { public static void Run() { } }"));

        object before = await DocumentSemanticGeneration.ForAsync(
            solution.GetDocument(calleeDoc)!, default);

        var edited = solution.WithDocumentText(strangerDoc, SourceText.From(
            "public static class Stranger { public static void Run() { Run(); } }"));

        object after = await DocumentSemanticGeneration.ForAsync(
            edited.GetDocument(calleeDoc)!, default);

        Assert.Equal(before, after);
    }

    /// <summary>Caller references Callee; the lens under test sits on Callee.Used.</summary>
    private static (Solution Solution, DocumentId CalleeDoc, DocumentId CallerDoc) TwoProjectSolution()
    {
        var workspace = new AdhocWorkspace();
        var calleeId = ProjectId.CreateNewId("Callee");
        var callerId = ProjectId.CreateNewId("Caller");
        var calleeDoc = DocumentId.CreateNewId(calleeId);
        var callerDoc = DocumentId.CreateNewId(callerId);

        var mscorlib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);

        var solution = workspace.CurrentSolution
            .AddProject(calleeId, "Callee", "Callee", LanguageNames.CSharp)
            .AddMetadataReference(calleeId, mscorlib)
            .AddDocument(calleeDoc, "Callee.cs", SourceText.From(
                "public static class Callee { public static void Used() { } }"))
            .AddProject(callerId, "Caller", "Caller", LanguageNames.CSharp)
            .AddMetadataReference(callerId, mscorlib)
            .AddProjectReference(callerId, new ProjectReference(calleeId))
            .AddDocument(callerDoc, "Caller.cs", SourceText.From(
                """
                public static class Caller
                {
                    public static void Run()
                    {
                        Callee.Used();
                    }
                }
                """));

        return (solution, calleeDoc, callerDoc);
    }
}
