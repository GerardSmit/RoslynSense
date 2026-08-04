using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynMCP.Services;
using RoslynMCP.Languages;
using RoslynMCP.Lsp.Handlers;

namespace RoslynMCP.Tools;

/// <summary>
/// Navigates to the definition of a symbol identified by a markup snippet,
/// returning either source context or an external metadata preview.
/// </summary>
[McpServerToolType]
public static class GoToDefinitionSnippetTool
{
    /// <summary>
    /// Resolves a symbol via markup targeting and returns its definition location
    /// with a code snippet for context.
    /// </summary>
    [McpServerTool, Description(
        "Go to the definition of a symbol using a code snippet. Provide a code snippet from the file with " +
        "[| |] delimiters around the target symbol, e.g. 'var x = [|Foo|].Bar();'. " +
        "Returns source file context when available, or auto-decompiled source " +
        "for referenced assembly symbols. Works with C# files. " +
        "Prefer GoToDefinition (by fully-qualified name) when you already know the type/member name.")]
    public static async Task<string> GoToDefinitionSnippet(
        [Description("Path to the file containing the symbol reference.")] string filePath,
        [Description(
            "Code snippet with [| |] markers around the target symbol, " +
            "e.g. 'var x = [|Foo|].Bar();'.")]
        string markupSnippet,
        IOutputFormatter fmt,
        [Description("Number of lines of context to show around the definition. Default: 5.")]
        int contextLines = 5,
        [Description("Approximate line number near the target snippet. Used to pick the closest match when the snippet appears multiple times.")]
        int? hintLine = null,
        IEnumerable<IGoToDefinitionHandler>? handlers = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string systemPath = PathHelper.NormalizePath(filePath);

            // Delegate to registered handlers for non-C# file types
            if (handlers is not null)
            {
                foreach (var handler in handlers)
                {
                    if (handler.CanHandle(systemPath))
                        return await handler.ResolveAsync(systemPath, markupSnippet, contextLines, cancellationToken);
                }
            }

            var errors = new StringBuilder();
            var ctx = await ToolHelper.ResolveSymbolAsync(filePath, markupSnippet, errors, cancellationToken, hintLine);
            if (ctx is null)
                return errors.ToString();

            if (!ctx.IsResolved)
                return ToolHelper.FormatResolutionError(ctx.Resolution);

            // The seam the editor's F12 goes through, called with the document and offset this
            // tool's own markup resolution produced. One helper for both front-ends is what stops
            // an AI session and the editor resolving the same dispatch differently.
            var redirected = await NavigationHandlers.RedirectedSymbolsAsync(
                ctx.Document, ctx.Resolution.Position ?? 0, ctx.Symbol!,
                NavigationKind.Definition, languages: null, cancellationToken);

            if (redirected.Count == 1)
            {
                return await FormatDefinitionAsync(
                    redirected[0], ctx.Project, contextLines, fmt, cancellationToken);
            }

            if (redirected.Count > 1)
            {
                // A notification has as many handlers as subscribe to it, and choosing one to show
                // would be a guess. Every one, each rendered the way a single definition is.
                var all = new StringBuilder();
                all.AppendLine($"# {redirected.Count} handlers");
                all.AppendLine();

                foreach (var target in redirected)
                {
                    all.AppendLine(await FormatDefinitionAsync(
                        target, ctx.Project, contextLines, fmt, cancellationToken));
                }

                return all.ToString();
            }

            return await FormatDefinitionAsync(ctx.Symbol!, ctx.Project, contextLines, fmt, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[GoToDefinition] Unhandled error: {ex}");
            return $"Error: {ex.Message}";
        }
    }

    internal static async Task<string> FormatDefinitionAsync(
        ISymbol symbol, Project project, int contextLines, IOutputFormatter fmt, CancellationToken cancellationToken = default)
    {
        // For constructors, use the containing type name as the display header
        string displayName = symbol is IMethodSymbol { MethodKind: Microsoft.CodeAnalysis.MethodKind.Constructor }
            ? symbol.ContainingType?.Name ?? symbol.Name
            : symbol.Name;

        var sb = new StringBuilder();
        sb.AppendLine($"# Definition: {displayName}");
        sb.AppendLine();

        sb.AppendLine($"- **Symbol**: {symbol.ToDisplayString()}");
        sb.AppendLine($"- **Kind**: {symbol.Kind}");
        sb.AppendLine($"- **Accessibility**: {symbol.DeclaredAccessibility}");

        if (symbol.ContainingType is not null)
            sb.AppendLine($"- **Containing Type**: {symbol.ContainingType.ToDisplayString()}");

        if (symbol.ContainingNamespace is { IsGlobalNamespace: false })
            sb.AppendLine($"- **Namespace**: {symbol.ContainingNamespace.ToDisplayString()}");

        SymbolFormatter.AppendXmlDocs(sb, symbol);

        sb.AppendLine();

        var sourceLocations = symbol.Locations.Where(l => l.IsInSource).ToList();
        var metadataLocations = symbol.Locations.Where(l => l.IsInMetadata).ToList();
        bool isNamedType = symbol is INamedTypeSymbol;

        if (sourceLocations.Count > 0)
        {
            // For named types, skip the code context — the members table is more useful
            await AppendLocationsAsync(
                sb,
                sourceLocations,
                project.Solution,
                "Source Location",
                provenance: null,
                assemblyPath: null,
                includeCodeContext: !isNamedType,
                contextLines,
                cancellationToken);
        }
        else if (metadataLocations.Count > 0)
        {
            var decompiled = await DecompiledSourceService.TryDecompileSymbolAsync(
                symbol,
                project,
                cancellationToken);

            if (decompiled is not null)
            {
                var decompiledLocations = decompiled.Locations.ToList();
                if (decompiledLocations.Count > 0)
                {
                    await AppendLocationsAsync(
                        sb,
                        decompiledLocations,
                        decompiled.Project.Solution,
                        "Decompiled Source",
                        provenance: "auto-decompiled",
                        assemblyPath: decompiled.AssemblyPath,
                        includeCodeContext: true,
                        contextLines,
                        cancellationToken);
                }
                else
                {
                    sb.Append(MetadataSourceFormatter.FormatExternalDefinition(symbol));
                }
            }
            else
            {
                sb.Append(MetadataSourceFormatter.FormatExternalDefinition(symbol));
            }
        }
        else
        {
            sb.AppendLine("No definition location available.");
        }

        // For source-defined named types, append a compact member outline.
        // Metadata types already show members via MetadataSourceFormatter or decompiled source.
        if (isNamedType && sourceLocations.Count > 0)
            AppendMemberOutline(sb, (INamedTypeSymbol)symbol, fmt);

        fmt.AppendHints(sb,
            "Use FindUsages to find all references to this symbol",
            "Use GetCallHierarchy to see callers and callees");

        return sb.ToString();
    }

    private static async Task AppendLocationsAsync(
        StringBuilder sb,
        IReadOnlyList<Location> locations,
        Solution solution,
        string sectionTitle,
        string? provenance,
        string? assemblyPath,
        bool includeCodeContext,
        int contextLines,
        CancellationToken cancellationToken)
    {
        for (int i = 0; i < locations.Count; i++)
        {
            var location = locations[i];
            string header = locations.Count > 1
                ? $"## {sectionTitle} {i + 1}"
                : $"## {sectionTitle}";
            sb.AppendLine(header);
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(provenance) && !string.IsNullOrWhiteSpace(assemblyPath))
            {
                sb.AppendLine($"**Provenance**: {provenance} from `{Path.GetFileNameWithoutExtension(assemblyPath)}`");
                sb.AppendLine($"**Assembly Path**: {assemblyPath}");
            }

            var lineSpan = location.GetLineSpan();
            int startLine = lineSpan.StartLinePosition.Line + 1;
            int endLine = ToolHelper.GetDeclarationEndLine(location);
            string defFile = lineSpan.Path;

            sb.AppendLine($"**File**: {defFile}");
            if (endLine > startLine)
                sb.AppendLine($"**Lines**: {startLine}–{endLine}");
            else
                sb.AppendLine($"**Line**: {startLine}");
            sb.AppendLine();

            if (includeCodeContext)
                await AppendCodeContextAsync(sb, location, solution, contextLines, cancellationToken);
        }
    }

    private static async Task AppendCodeContextAsync(
        StringBuilder sb, Location location, Solution solution, int contextLines, CancellationToken cancellationToken)
    {
        var tree = location.SourceTree;
        if (tree is null) return;

        var lineSpan = location.GetLineSpan();
        int targetLine = lineSpan.StartLinePosition.Line; // 0-based

        var documentId = solution.GetDocumentIdsWithFilePath(tree.FilePath).FirstOrDefault();
        if (documentId is null) return;

        var doc = solution.GetDocument(documentId);
        if (doc is null) return;

        var text = await doc.GetTextAsync(cancellationToken);
        int contextStart = Math.Max(0, targetLine - contextLines);
        int contextEnd = Math.Min(text.Lines.Count - 1, targetLine + contextLines);

        sb.AppendLine("```csharp");
        for (int i = contextStart; i <= contextEnd; i++)
        {
            string lineText = text.Lines[i].ToString();
            int lineNum = i + 1;
            sb.AppendLine(lineNum == targetLine + 1
                ? $"{lineNum}: > {lineText}"
                : $"{lineNum}:   {lineText}");
        }
        sb.AppendLine("```");
        sb.AppendLine();
    }

    private const int MaxMembers = 30;

    private static void AppendMemberOutline(StringBuilder sb, INamedTypeSymbol type, IOutputFormatter fmt)
    {
        var members = type.GetMembers()
            .Where(SymbolFormatter.ShouldDisplayMember)
            .OrderBy(SymbolFormatter.MemberSortOrder)
            .ThenBy(m => m.Locations.FirstOrDefault()?.GetLineSpan().StartLinePosition.Line ?? int.MaxValue)
            .ToList();

        if (members.Count == 0)
            return;

        sb.AppendLine();
        sb.AppendLine("## Members");
        sb.AppendLine();

        bool truncated = members.Count > MaxMembers;
        var displayed = truncated ? members.Take(MaxMembers).ToList() : members;

        var columns = new[] { "Kind", "Signature", "Line" };
        var rows = new List<string[]>();

        foreach (var member in displayed)
        {
            string kind = member switch
            {
                IMethodSymbol { MethodKind: MethodKind.Constructor } => "ctor",
                IMethodSymbol => "method",
                IPropertySymbol => "property",
                IFieldSymbol { IsConst: true } => "const",
                IFieldSymbol => "field",
                IEventSymbol => "event",
                _ => member.Kind.ToString().ToLowerInvariant()
            };

            string signature = member.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

            var loc = member.Locations.FirstOrDefault(l => l.IsInSource);
            string line = loc is not null
                ? (loc.GetLineSpan().StartLinePosition.Line + 1).ToString()
                : "-";

            rows.Add([kind, signature, line]);
        }

        fmt.AppendTable(sb, "Members", columns, rows, members.Count);

        if (truncated)
            fmt.AppendTruncation(sb, MaxMembers, members.Count);
    }
}
