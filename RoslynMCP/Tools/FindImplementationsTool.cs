using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

/// <summary>
/// Finds all implementations and derived types of an interface, abstract class,
/// virtual/abstract member, or overridable method.
/// </summary>
[McpServerToolType]
public static class FindImplementationsTool
{
    [McpServerTool, Description(
        "Find all implementations of an interface, abstract class, or virtual/abstract member. " +
        "Provide a code snippet with [| |] delimiters around the target symbol. " +
        "For interfaces: finds all implementing types. For classes: finds all derived types. " +
        "For virtual/abstract methods: finds all overriding methods.")]
    public static async Task<string> FindImplementations(
        [Description("Path to the file containing the symbol.")] string filePath,
        [Description(
            "Code snippet with [| |] markers around the target, " +
            "e.g. 'public interface [|IService|]' or 'virtual void [|Execute|]()'.")]
        string markupSnippet,
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

            return symbol switch
            {
                INamedTypeSymbol typeSymbol => await FindTypeImplementationsAsync(
                    typeSymbol, solution, ctx.Project, cancellationToken),
                IMethodSymbol methodSymbol => await FindMemberImplementationsAsync(
                    methodSymbol, solution, ctx.Project, cancellationToken),
                IPropertySymbol propertySymbol => await FindMemberImplementationsAsync(
                    propertySymbol, solution, ctx.Project, cancellationToken),
                IEventSymbol eventSymbol => await FindMemberImplementationsAsync(
                    eventSymbol, solution, ctx.Project, cancellationToken),
                _ => $"Cannot find implementations for {symbol.Kind} '{symbol.Name}'. " +
                     "This tool works with interfaces, classes, and virtual/abstract members.",
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FindImplementations] Unhandled error: {ex}");
            return $"Error: {ex.Message}";
        }
    }

    private static async Task<string> FindTypeImplementationsAsync(
        INamedTypeSymbol typeSymbol,
        Solution solution,
        Project project,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();

        bool isInterface = typeSymbol.TypeKind == TypeKind.Interface;
        string symbolLabel = isInterface ? "Interface" : "Class";

        sb.AppendLine($"# Implementations: {typeSymbol.Name}");
        sb.AppendLine();
        sb.AppendLine($"- **{symbolLabel}**: {typeSymbol.ToDisplayString()}");
        sb.AppendLine($"- **Kind**: {typeSymbol.TypeKind}");
        sb.AppendLine();

        var implementations = new List<(INamedTypeSymbol Type, Location? Location)>();

        if (isInterface)
        {
            var implementors = await SymbolFinder.FindImplementationsAsync(
                typeSymbol, solution, cancellationToken: cancellationToken);

            foreach (var impl in implementors.OfType<INamedTypeSymbol>())
            {
                var loc = impl.Locations.FirstOrDefault(l => l.IsInSource);
                implementations.Add((impl, loc));
            }
        }
        else
        {
            var derived = await SymbolFinder.FindDerivedClassesAsync(
                typeSymbol, solution, cancellationToken: cancellationToken);

            foreach (var d in derived)
            {
                var loc = d.Locations.FirstOrDefault(l => l.IsInSource);
                implementations.Add((d, loc));
            }
        }

        if (implementations.Count == 0)
        {
            sb.AppendLine(isInterface
                ? "No implementations found in the current solution."
                : "No derived classes found in the current solution.");
            return sb.ToString();
        }

        sb.AppendLine($"## {(isInterface ? "Implementing Types" : "Derived Types")} ({implementations.Count})");
        sb.AppendLine();
        sb.AppendLine("| Type | File | Line |");
        sb.AppendLine("|------|------|------|");

        string? projectDir = Path.GetDirectoryName(project.FilePath);

        foreach (var (type, loc) in implementations.OrderBy(i => i.Type.Name))
        {
            string typeName = type.ToDisplayString();
            if (loc is not null)
            {
                var lineSpan = loc.GetLineSpan();
                string filePath = lineSpan.Path;
                string displayPath = projectDir is not null
                    ? Path.GetRelativePath(projectDir, filePath)
                    : filePath;
                int line = lineSpan.StartLinePosition.Line + 1;
                sb.AppendLine($"| {MarkdownFormatter.EscapeTableCell(typeName)} | {MarkdownFormatter.EscapeTableCell(displayPath)} | {line} |");
            }
            else
            {
                sb.AppendLine($"| {MarkdownFormatter.EscapeTableCell(typeName)} | (external) | - |");
            }
        }

        // Show code context for first few implementations
        sb.AppendLine();
        int shown = 0;
        foreach (var (type, loc) in implementations.OrderBy(i => i.Type.Name))
        {
            if (shown >= 5 || loc is null) continue;

            var lineSpan = loc.GetLineSpan();
            string filePath = lineSpan.Path;
            int startLine = lineSpan.StartLinePosition.Line;

            if (!File.Exists(filePath)) continue;

            var lines = await File.ReadAllLinesAsync(filePath, cancellationToken);
            int contextStart = Math.Max(0, startLine - 1);
            int contextEnd = Math.Min(lines.Length - 1, startLine + 10);

            string displayPath = projectDir is not null
                ? Path.GetRelativePath(projectDir, filePath)
                : filePath;

            sb.AppendLine($"### {type.Name} ({displayPath}:{startLine + 1})");
            sb.AppendLine();
            sb.AppendLine("```csharp");
            for (int i = contextStart; i <= contextEnd; i++)
                sb.AppendLine(lines[i]);
            sb.AppendLine("```");
            sb.AppendLine();
            shown++;
        }

        return sb.ToString();
    }

    private static async Task<string> FindMemberImplementationsAsync(
        ISymbol memberSymbol,
        Solution solution,
        Project project,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Implementations: {memberSymbol.Name}");
        sb.AppendLine();
        sb.AppendLine($"- **Symbol**: {memberSymbol.ToDisplayString()}");
        sb.AppendLine($"- **Kind**: {memberSymbol.Kind}");
        sb.AppendLine($"- **Containing Type**: {memberSymbol.ContainingType?.ToDisplayString()}");
        sb.AppendLine();

        var implementations = new List<(ISymbol Symbol, Location? Location)>();

        // FindImplementationsAsync works for interface members and virtual/abstract members
        var impls = await SymbolFinder.FindImplementationsAsync(
            memberSymbol, solution, cancellationToken: cancellationToken);

        foreach (var impl in impls)
        {
            var loc = impl.Locations.FirstOrDefault(l => l.IsInSource);
            implementations.Add((impl, loc));
        }

        // Also find overrides for virtual/abstract methods
        if (memberSymbol is IMethodSymbol { IsVirtual: true } or IMethodSymbol { IsAbstract: true } or
            IPropertySymbol { IsVirtual: true } or IPropertySymbol { IsAbstract: true } or
            IEventSymbol { IsVirtual: true } or IEventSymbol { IsAbstract: true })
        {
            var overrides = await SymbolFinder.FindOverridesAsync(
                memberSymbol, solution, cancellationToken: cancellationToken);

            foreach (var ov in overrides)
            {
                if (!implementations.Any(i => SymbolEqualityComparer.Default.Equals(i.Symbol, ov)))
                {
                    var loc = ov.Locations.FirstOrDefault(l => l.IsInSource);
                    implementations.Add((ov, loc));
                }
            }
        }

        if (implementations.Count == 0)
        {
            sb.AppendLine("No implementations or overrides found in the current solution.");
            return sb.ToString();
        }

        sb.AppendLine($"## Implementations ({implementations.Count})");
        sb.AppendLine();
        sb.AppendLine("| Implementation | Containing Type | File | Line |");
        sb.AppendLine("|----------------|-----------------|------|------|");

        string? projectDir = Path.GetDirectoryName(project.FilePath);

        foreach (var (impl, loc) in implementations.OrderBy(i => i.Symbol.ContainingType?.Name))
        {
            string implDisplay = impl.ToDisplayString();
            string containingType = impl.ContainingType?.ToDisplayString() ?? "?";
            if (loc is not null)
            {
                var lineSpan = loc.GetLineSpan();
                string filePath = lineSpan.Path;
                string displayPath = projectDir is not null
                    ? Path.GetRelativePath(projectDir, filePath)
                    : filePath;
                int line = lineSpan.StartLinePosition.Line + 1;
                sb.AppendLine($"| {MarkdownFormatter.EscapeTableCell(implDisplay)} | {MarkdownFormatter.EscapeTableCell(containingType)} | {MarkdownFormatter.EscapeTableCell(displayPath)} | {line} |");
            }
            else
            {
                sb.AppendLine($"| {MarkdownFormatter.EscapeTableCell(implDisplay)} | {MarkdownFormatter.EscapeTableCell(containingType)} | (external) | - |");
            }
        }

        return sb.ToString();
    }
}
