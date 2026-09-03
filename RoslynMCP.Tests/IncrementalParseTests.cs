using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// What a keystroke costs to parse. The buffer overlay hands Roslyn the edited
/// <see cref="SourceText"/>, and only a text whose change lineage reaches the one the workspace
/// already holds lets Roslyn reuse green nodes — apply the edit as a whole new text and every
/// keystroke re-parses the entire file.
/// </summary>
[Collection(SharedState.Name)]
public class IncrementalParseTests
{
    private const int MethodCount = 40;

    /// <summary>The method the edit lands in, and the substring inside its body that moves.</summary>
    private const int EditedMethod = 20;

    /// <summary>
    /// An edit inside one method body re-parses that method and nothing else.
    /// </summary>
    /// <remarks>
    /// Asserted with <see cref="SyntaxNode.IsIncrementallyIdenticalTo"/>, which is green-node
    /// identity — not structural equality — so a rebuilt-but-identical method fails it. The edited
    /// method is asserted <em>not</em> identical for the same reason: a test that only checked the
    /// other 39 would pass against a parser that reused everything including the edit.
    /// </remarks>
    [Fact]
    public async Task AnEditInsideOneMethodLeavesTheOtherMethodsGreenNodesAlone()
    {
        string path = FixturePaths.CalculatorFile;
        string session = $"incremental-{Guid.NewGuid():N}";
        string source = BuildSource();

        // Loaded first, and the buffer reconciled into the workspace before the "before" root is
        // taken. didOpen does both — the store raises its bridge event and the server reconciles —
        // but it does it off the notification thread, so a test that skipped this would take its
        // first root from the overlay fork and its second from the reconciled workspace: two
        // independent parses of the same text, and no green node shared between them regardless of
        // how incremental the parser was.
        await WorkspaceService.GetOrOpenProjectAsync(FixturePaths.SampleProjectFile);
        OpenDocumentStore.Open(session, path, SourceText.From(source), version: 1);
        try
        {
            await WorkspaceService.ReconcileOpenBufferAsync(path);
            var before = await MethodsAsync(path);

            // One statement's operand, inside a single body: the smallest edit that has to
            // produce a different tree at all.
            string target = $"accumulator += {EditedMethod} * 2;";
            int at = source.IndexOf(target, StringComparison.Ordinal);
            Assert.True(at >= 0);

            var edited = OpenDocumentStore.Change(path, version: 2,
                original => original.WithChanges(
                    new TextChange(new TextSpan(at, target.Length), $"accumulator += {EditedMethod} * 3;")));
            Assert.NotNull(edited);

            await WorkspaceService.ReconcileOpenBufferAsync(path);
            var after = await MethodsAsync(path);

            Assert.Equal(before.Length, after.Length);
            Assert.Equal(MethodCount, before.Length);

            for (int i = 0; i < before.Length; i++)
            {
                Assert.Equal(before[i].Identifier.Text, after[i].Identifier.Text);

                if (i == EditedMethod)
                {
                    Assert.False(before[i].IsIncrementallyIdenticalTo(after[i]),
                        $"{after[i].Identifier.Text} holds the edit and cannot be the same green node");
                }
                else
                {
                    Assert.True(before[i].IsIncrementallyIdenticalTo(after[i]),
                        $"{after[i].Identifier.Text} was re-parsed by an edit in "
                        + $"Step{EditedMethod} — the whole file is being re-parsed per keystroke");
                }
            }
        }
        finally
        {
            OpenDocumentStore.Close(session, path);
        }
    }

    private static async Task<MethodDeclarationSyntax[]> MethodsAsync(string path)
    {
        var document = await LspDocumentResolver.ResolveAsync(path, default);
        Assert.NotNull(document);
        var root = await document!.GetSyntaxRootAsync();
        Assert.NotNull(root);
        return [.. root!.DescendantNodes().OfType<MethodDeclarationSyntax>()];
    }

    /// <summary>A few hundred lines of unremarkable method bodies — enough that re-parsing all of
    /// them is a measurable cost and a visible failure.</summary>
    private static string BuildSource()
    {
        var builder = new StringBuilder()
            .Append("namespace SampleProject;\r\n\r\npublic class Calculator\r\n{\r\n");

        for (int i = 0; i < MethodCount; i++)
        {
            builder.Append($"    public int Step{i}(int seed)\r\n")
                .Append("    {\r\n")
                .Append("        int accumulator = seed;\r\n")
                .Append($"        accumulator += {i};\r\n")
                .Append($"        accumulator += {i} * 2;\r\n")
                .Append($"        if (accumulator > {i})\r\n")
                .Append("        {\r\n")
                .Append("            accumulator -= 1;\r\n")
                .Append("        }\r\n")
                .Append("        return accumulator;\r\n")
                .Append("    }\r\n\r\n");
        }

        return builder.Append("}\r\n").ToString();
    }
}
