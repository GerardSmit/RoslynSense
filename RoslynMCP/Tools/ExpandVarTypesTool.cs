using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

/// <summary>
/// Returns a method's source with every <c>var</c> declaration replaced by its resolved explicit type,
/// so the LLM can read return types without navigating to each method definition.
/// </summary>
[McpServerToolType]
public static class ExpandVarTypesTool
{
    [McpServerTool, Description(
        "Return the source of a specific method with all 'var' declarations replaced by their " +
        "resolved explicit types (e.g. 'var x = Method()' → 'Customer x = Method()'). " +
        "Use this as your first step when you need to understand what types a method works with: " +
        "it reveals the return type of every local call, the element type of every collection, " +
        "and the type of every destructured result — all in one read. " +
        "This avoids chasing each method individually with GoToDefinition or SemanticSymbolSearch. " +
        "Especially useful before editing a method, fixing a type error, or explaining logic that " +
        "relies on inferred types.")]
    public static async Task<string> ExpandVarTypes(
        [Description("Path to the C# source file.")]
        string filePath,
        [Description("Name of the method to expand (e.g. 'ProcessOrder'). " +
                     "If there are multiple overloads or members with the same name, " +
                     "add hintLine to pick the right one.")]
        string methodName,
        [Description("Approximate line number of the method (1-based). " +
                     "Used to disambiguate when the same name appears multiple times.")]
        int? hintLine = null,
        CancellationToken cancellationToken = default)
    {
        var errors = new StringBuilder();
        var ctx = await ToolHelper.ResolveFileAsync(filePath, errors, cancellationToken);
        if (ctx is null)
            return errors.ToString();

        if (ctx.Document is null)
            return "Error: File not found in project.";

        var root = await ctx.Document.GetSyntaxRootAsync(cancellationToken);
        if (root is null)
            return "Error: Could not parse syntax tree.";

        var model = await ctx.Document.GetSemanticModelAsync(cancellationToken);
        if (model is null)
            return "Error: Could not obtain semantic model.";

        var members = FindNamedMembers(root, methodName);
        if (members.Count == 0)
            return $"Error: No method or member named '{methodName}' found in {Path.GetFileName(ctx.SystemPath)}.";

        SyntaxNode target;
        if (members.Count == 1)
        {
            target = members[0];
        }
        else if (hintLine.HasValue)
        {
            var hint = hintLine.Value;
            target = members
                .OrderBy(m => Math.Abs(m.GetLocation().GetLineSpan().StartLinePosition.Line + 1 - hint))
                .First();
        }
        else
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Multiple members named '{methodName}' found. Provide hintLine to pick one:");
            foreach (var m in members)
            {
                var line = m.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                sb.AppendLine($"  Line {line}: {GetMemberSignature(m)}");
            }
            return sb.ToString().TrimEnd();
        }

        var rewriter = new VarRewriter(model);
        var rewritten = rewriter.Visit(target);

        var sb2 = new StringBuilder();
        var startLine = target.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        sb2.AppendLine($"// {Path.GetFileName(ctx.SystemPath)}, line {startLine}");
        if (rewriter.UnresolvedCount > 0)
            sb2.AppendLine($"// Note: {rewriter.UnresolvedCount} 'var' declaration(s) could not be resolved and were left as-is.");
        sb2.AppendLine("```csharp");
        sb2.Append(rewritten!.ToFullString().TrimEnd());
        sb2.AppendLine();
        sb2.AppendLine("```");
        return sb2.ToString();
    }

    private static List<SyntaxNode> FindNamedMembers(SyntaxNode root, string name)
    {
        var results = new List<SyntaxNode>();
        foreach (var node in root.DescendantNodes())
        {
            string? memberName = node switch
            {
                MethodDeclarationSyntax m => m.Identifier.Text,
                ConstructorDeclarationSyntax c => c.Identifier.Text,
                LocalFunctionStatementSyntax lf => lf.Identifier.Text,
                PropertyDeclarationSyntax p => p.Identifier.Text,
                _ => null
            };
            if (string.Equals(memberName, name, StringComparison.Ordinal))
                results.Add(node);
        }
        return results;
    }

    private static string GetMemberSignature(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m =>
            $"{m.Modifiers} {m.ReturnType} {m.Identifier}{m.ParameterList}".Trim(),
        ConstructorDeclarationSyntax c =>
            $"{c.Modifiers} {c.Identifier}{c.ParameterList}".Trim(),
        LocalFunctionStatementSyntax lf =>
            $"{lf.Modifiers} {lf.ReturnType} {lf.Identifier}{lf.ParameterList}".Trim(),
        PropertyDeclarationSyntax p =>
            $"{p.Modifiers} {p.Type} {p.Identifier}".Trim(),
        _ => node.GetType().Name
    };

    /// <summary>
    /// Rewrites every <c>var</c> type placeholder to its resolved explicit type name.
    /// Leaves <c>var</c> unchanged when the type cannot be resolved (error symbol).
    /// </summary>
    private sealed class VarRewriter(SemanticModel model) : CSharpSyntaxRewriter
    {
        public int UnresolvedCount { get; private set; }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (node.Identifier.Text != "var")
                return node;

            // Only replace 'var' when it is the type placeholder in a declaration
            bool isVarPosition = node.Parent switch
            {
                VariableDeclarationSyntax d => d.Type == node,
                ForEachStatementSyntax f => f.Type == node,
                _ => false
            };

            if (!isVarPosition)
                return node;

            var typeInfo = model.GetTypeInfo(node);
            var type = typeInfo.Type;

            if (type is null or IErrorTypeSymbol || type.TypeKind == TypeKind.Error)
            {
                UnresolvedCount++;
                return node;
            }

            var typeName = type.ToMinimalDisplayString(model, node.SpanStart);
            return SyntaxFactory.ParseTypeName(typeName)
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia());
        }
    }
}
