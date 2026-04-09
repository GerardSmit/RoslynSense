using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

/// <summary>
/// Shows callers of a method/property (who calls this?) and callees (what does this call?).
/// </summary>
[McpServerToolType]
public static class CallHierarchyTool
{
    private const int MaxCallees = 50;
    private const int MaxCallers = 50;

    [McpServerTool, Description(
        "Show the call hierarchy for a method or property. " +
        "Finds all callers (who calls this symbol?) and callees (what does this symbol call?). " +
        "Provide a code snippet with [| |] delimiters around the target symbol.")]
    public static async Task<string> GetCallHierarchy(
        [Description("Path to the file containing the symbol.")] string filePath,
        [Description(
            "Code snippet with [| |] markers around the target, " +
            "e.g. 'void [|ProcessData|](int x)'.")]
        string markupSnippet,
        [Description("Direction: 'callers' (who calls this?), 'callees' (what does this call?), or 'both'. Default: 'both'.")]
        string direction = "both",
        [Description("Approximate line number near the target snippet. Used to pick the closest match when the snippet appears multiple times.")]
        int? hintLine = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var errors = new StringBuilder();
            var ctx = await ToolHelper.ResolveSymbolAsync(filePath, markupSnippet, errors, cancellationToken, hintLine);
            if (ctx is null)
                return errors.ToString();

            if (!ctx.IsResolved)
                return ToolHelper.FormatResolutionError(ctx.Resolution);

            var symbol = ctx.Symbol!;
            var solution = ctx.Workspace.CurrentSolution;

            var sb = new StringBuilder();
            sb.AppendLine($"# Call Hierarchy: {symbol.Name}");
            sb.AppendLine();
            SymbolFormatter.AppendSymbolInfo(sb, symbol);
            sb.AppendLine();

            if (direction is not "callers" and not "callees" and not "both")
                return $"Error: direction must be 'callers', 'callees', or 'both' (got '{direction}').";

            bool showCallers = direction is "callers" or "both";
            bool showCallees = direction is "callees" or "both";

            if (showCallers)
            {
                await AppendCallersAsync(sb, symbol, solution, ctx.ProjectDir, cancellationToken);
            }

            if (showCallees)
            {
                await AppendCalleesAsync(sb, symbol, ctx.Document, ctx.ProjectDir, cancellationToken);
            }

            return sb.ToString();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CallHierarchy] Unhandled error: {ex}");
            return $"Error: {ex.Message}";
        }
    }

    private static async Task AppendCallersAsync(
        StringBuilder sb,
        ISymbol symbol,
        Solution solution,
        string? projectDir,
        CancellationToken cancellationToken)
    {
        sb.AppendLine("## Callers (↑ Who calls this?)");
        sb.AppendLine();

        var callers = await SymbolFinder.FindCallersAsync(
            symbol, solution, cancellationToken);

        var callerList = callers
            .Where(c => c.IsDirect)
            .OrderBy(c => c.CallingSymbol.ContainingType?.Name)
            .ThenBy(c => c.CallingSymbol.Name)
            .Take(MaxCallers)
            .ToList();

        if (callerList.Count == 0)
        {
            sb.AppendLine("No callers found in the current solution.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine($"Found {callerList.Count} caller(s):");
        sb.AppendLine();
        sb.AppendLine("| Caller | Containing Type | File | Line |");
        sb.AppendLine("|--------|-----------------|------|------|");

        foreach (var caller in callerList)
        {
            var callingSymbol = caller.CallingSymbol;
            string callerName = callingSymbol.ToDisplayString();
            string containingType = callingSymbol.ContainingType?.Name ?? "-";

            var loc = caller.Locations.FirstOrDefault();
            if (loc is not null && loc.IsInSource)
            {
                var lineSpan = loc.GetLineSpan();
                string displayPath = projectDir is not null
                    ? Path.GetRelativePath(projectDir, lineSpan.Path)
                    : lineSpan.Path;
                int line = lineSpan.StartLinePosition.Line + 1;
                sb.AppendLine($"| {MarkdownHelper.EscapeTableCell(callerName)} | {containingType} | {MarkdownHelper.EscapeTableCell(displayPath)} | {line} |");
            }
            else
            {
                sb.AppendLine($"| {MarkdownHelper.EscapeTableCell(callerName)} | {containingType} | (external) | - |");
            }
        }

        sb.AppendLine();
    }

    private static async Task AppendCalleesAsync(
        StringBuilder sb,
        ISymbol symbol,
        Document document,
        string? projectDir,
        CancellationToken cancellationToken)
    {
        sb.AppendLine("## Callees (↓ What does this call?)");
        sb.AppendLine();

        // Get the syntax node for the symbol's declaration
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (location is null)
        {
            sb.AppendLine("Symbol has no source location — cannot analyze callees.");
            sb.AppendLine();
            return;
        }

        var syntaxTree = location.SourceTree;
        if (syntaxTree is null)
        {
            sb.AppendLine("Cannot access syntax tree for callee analysis.");
            sb.AppendLine();
            return;
        }

        // Find the document containing this symbol
        var docId = document.Project.Solution.GetDocumentId(syntaxTree);
        var targetDoc = docId is not null ? document.Project.Solution.GetDocument(docId) : null;
        if (targetDoc is null)
        {
            sb.AppendLine("Cannot locate document for callee analysis.");
            sb.AppendLine();
            return;
        }

        var semanticModel = await targetDoc.GetSemanticModelAsync(cancellationToken);
        if (semanticModel is null)
        {
            sb.AppendLine("Cannot obtain semantic model for callee analysis.");
            sb.AppendLine();
            return;
        }

        var root = await syntaxTree.GetRootAsync(cancellationToken);
        var declNode = root.FindNode(location.SourceSpan);

        // Find all invocations within this method/property body
        // Filter to syntax nodes that represent actual calls to avoid false positives
        var invocations = declNode.DescendantNodes()
            .Where(n => n is Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax
                      or Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax
                      or Microsoft.CodeAnalysis.CSharp.Syntax.ObjectCreationExpressionSyntax
                      or Microsoft.CodeAnalysis.CSharp.Syntax.ImplicitObjectCreationExpressionSyntax
                      or Microsoft.CodeAnalysis.CSharp.Syntax.ElementAccessExpressionSyntax)
            .Select(n => semanticModel.GetSymbolInfo(n, cancellationToken))
            .Where(si => si.Symbol is not null)
            .Select(si => si.Symbol!)
            .Where(s => s.Kind is SymbolKind.Method or SymbolKind.Property)
            .Distinct(SymbolEqualityComparer.Default)
            .Take(MaxCallees)
            .ToList();

        if (invocations.Count == 0)
        {
            sb.AppendLine("No outgoing calls found in this symbol's body.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine($"Found {invocations.Count} callee(s):");
        sb.AppendLine();
        sb.AppendLine("| Callee | Kind | Containing Type |");
        sb.AppendLine("|--------|------|-----------------|");

        foreach (var callee in invocations.OrderBy(s => s.ContainingType?.Name).ThenBy(s => s.Name))
        {
            string calleeName = callee.ToDisplayString();
            string kind = callee.Kind.ToString();
            string containingType = callee.ContainingType?.ToDisplayString() ?? "-";
            sb.AppendLine($"| {MarkdownHelper.EscapeTableCell(calleeName)} | {kind} | {MarkdownHelper.EscapeTableCell(containingType)} |");
        }

        sb.AppendLine();
    }
}
