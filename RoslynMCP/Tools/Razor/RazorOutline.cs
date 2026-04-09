using System.Text;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.Language.Intermediate;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMCP.Services;

namespace RoslynMCP.Tools.Razor;

/// <summary>
/// Produces a structured outline for Razor files (.razor/.cshtml) by parsing
/// directives from source text, walking the Razor IR tree, and mapping generated
/// C# members back to their Razor source lines.
/// </summary>
internal class RazorOutline : IOutlineHandler
{
    public bool CanHandle(string filePath) => RazorSourceMappingService.IsRazorFile(filePath);

    public async Task<string> GetOutlineAsync(string filePath, CancellationToken cancellationToken)
    {
        string? projectPath = await NonCSharpProjectFinder.FindProjectAsync(filePath, cancellationToken);

        if (string.IsNullOrEmpty(projectPath))
            return "Error: Couldn't find a project containing this Razor file.";

        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
            projectPath, cancellationToken: cancellationToken);

        var text = await File.ReadAllTextAsync(filePath, cancellationToken);

        return await FormatOutlineAsync(filePath, text, project, cancellationToken);
    }

    /// <summary>
    /// Formats a structured outline for a Razor file by parsing directives from source
    /// and extracting @code block member signatures from the generated C# document.
    /// </summary>
    internal static async Task<string> FormatOutlineAsync(
        string filePath, string text, Microsoft.CodeAnalysis.Project project, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Razor File: {Path.GetFileName(filePath)}");
        sb.AppendLine();

        // Parse Razor file using the official Razor Language parser
        var razorDoc = ParseRazorDocument(filePath, text);
        var irTree = razorDoc.GetDocumentIntermediateNode();

        // Extract directives from source text (IR transforms them away in component mode)
        var directives = ExtractDirectivesFromSource(text);
        if (directives.Count > 0)
        {
            sb.AppendLine("## Directives");
            foreach (var (line, directive, value) in directives)
            {
                if (string.IsNullOrEmpty(value))
                    sb.AppendLine($"- **@{directive}** at line {line}");
                else
                    sb.AppendLine($"- **@{directive}** `{value}` at line {line}");
            }
            sb.AppendLine();
        }

        // Check for @code / @functions block via IR tree
        var codeNode = FindCodeBlockInIR(irTree);
        if (codeNode is not null)
        {
            int codeLine = (codeNode.Source?.LineIndex ?? 0) + 1;
            sb.AppendLine($"## @code Block (line {codeLine})");
            sb.AppendLine();

            // Extract member outlines from generated C#
            var sourceMap = await ProjectIndexCacheService.GetRazorSourceMapAsync(project, cancellationToken);
            if (sourceMap.RazorToGeneratedDocuments.TryGetValue(filePath, out var genDocs) && genDocs.Count > 0)
            {
                var genDoc = genDocs[0];
                var syntaxTree = await genDoc.GetSyntaxTreeAsync(cancellationToken);
                if (syntaxTree is not null)
                {
                    var root = await syntaxTree.GetRootAsync(cancellationToken);
                    string genFilePath = genDoc.FilePath ?? genDoc.Name;
                    sb.AppendLine("```");
                    AppendCodeMembers(sb, root, sourceMap, filePath, genFilePath);
                    sb.AppendLine("```");
                }
            }
            else
            {
                sb.AppendLine("_(No generated C# document found for member extraction)_");
            }

            sb.AppendLine();
        }

        // Count inline C# expressions from the IR tree
        int expressions = CountExpressionsInIR(irTree);
        if (expressions > 0)
        {
            sb.AppendLine($"## Inline Expressions: {expressions}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Parses a Razor file using the Microsoft.AspNetCore.Razor.Language engine.
    /// Returns a <see cref="RazorCodeDocument"/> with a full syntax tree and IR.
    /// </summary>
    internal static RazorCodeDocument ParseRazorDocument(string filePath, string text)
    {
        var source = RazorSourceDocument.Create(text, filePath);
        string fileKind = filePath.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
            ? "component"
            : null!;
        var engine = RazorProjectEngine.Create(
            RazorConfiguration.Default,
            RazorProjectFileSystem.Create(Path.GetDirectoryName(filePath)!));
        return engine.Process(source, fileKind, importSources: null, tagHelpers: null);
    }

    private static readonly HashSet<string> KnownDirectives = new(StringComparer.OrdinalIgnoreCase)
    {
        "page", "using", "inject", "implements", "inherits", "layout",
        "attribute", "namespace", "typeparam", "model", "rendermode",
        "preservewhitespace", "section"
    };

    /// <summary>
    /// Extracts directives by scanning Razor source text line-by-line.
    /// The Razor IR tree in component mode transforms directives away (e.g. @page → RouteAttributeExtensionNode),
    /// so we parse them from source text for reliable outline display.
    /// </summary>
    internal static List<(int Line, string DirectiveName, string Value)> ExtractDirectivesFromSource(string text)
    {
        var result = new List<(int, string, string)>();
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimStart();
            if (line.Length < 2 || line[0] != '@') continue;

            // Skip @code/@functions (handled separately), @{, @(, @*, control flow
            if (line.StartsWith("@code") || line.StartsWith("@functions") ||
                line[1] == '{' || line[1] == '(' || line[1] == '*')
                continue;

            foreach (var directive in KnownDirectives)
            {
                if (!line.AsSpan(1).StartsWith(directive, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Must end the directive name (not just a prefix of a longer word)
                int afterDirective = 1 + directive.Length;
                if (afterDirective < line.Length && char.IsLetterOrDigit(line[afterDirective]))
                    continue;

                string value = afterDirective < line.Length
                    ? line[afterDirective..].Trim().TrimEnd('\r')
                    : "";
                result.Add((i + 1, directive, value));
                break;
            }
        }
        return result;
    }

    /// <summary>
    /// Finds the @code/@functions block in the Razor IR tree.
    /// In component mode, this is a CSharpCodeIntermediateNode that is a direct child
    /// of ClassDeclarationIntermediateNode.
    /// </summary>
    internal static IntermediateNode? FindCodeBlockInIR(IntermediateNode node)
    {
        if (node is ClassDeclarationIntermediateNode classNode)
        {
            foreach (var child in classNode.Children)
            {
                if (child is CSharpCodeIntermediateNode codeNode && codeNode.Source.HasValue)
                    return codeNode;
            }
        }

        foreach (var child in node.Children)
        {
            var found = FindCodeBlockInIR(child);
            if (found is not null) return found;
        }

        return null;
    }

    /// <summary>
    /// Counts inline C# expression nodes in the Razor IR tree.
    /// </summary>
    internal static int CountExpressionsInIR(IntermediateNode node)
    {
        int count = 0;
        if (node is CSharpExpressionIntermediateNode)
            count++;

        foreach (var child in node.Children)
            count += CountExpressionsInIR(child);

        return count;
    }

    private static void AppendCodeMembers(
        StringBuilder sb, Microsoft.CodeAnalysis.SyntaxNode root,
        RazorSourceMap sourceMap, string razorFilePath, string generatedFilePath)
    {
        foreach (var child in root.DescendantNodes())
        {
            string? outline = child switch
            {
                MethodDeclarationSyntax m => GetFileOutlineTool.FormatMethod(m),
                PropertyDeclarationSyntax p => GetFileOutlineTool.FormatProperty(p),
                FieldDeclarationSyntax f => FormatFieldSimple(f),
                EventFieldDeclarationSyntax e => FormatEventFieldSimple(e),
                _ => null
            };

            if (outline is null) continue;

            var lineSpan = child.GetLocation().GetLineSpan();
            int genLine = lineSpan.StartLinePosition.Line + 1;

            var razorLoc = RazorSourceMappingService.MapGeneratedToRazor(
                sourceMap, generatedFilePath, genLine);

            if (razorLoc is not null &&
                string.Equals(razorLoc.RazorFilePath, razorFilePath, StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine($"{razorLoc.Line,4}: {outline}");
            }
        }
    }

    private static string FormatFieldSimple(FieldDeclarationSyntax field)
    {
        string modifiers = GetFileOutlineTool.FormatModifiers(field.Modifiers);
        string type = field.Declaration.Type.ToString();
        var names = string.Join(", ", field.Declaration.Variables.Select(v => v.Identifier.Text));
        return $"{modifiers}{type} {names}";
    }

    private static string FormatEventFieldSimple(EventFieldDeclarationSyntax eventField)
    {
        string modifiers = GetFileOutlineTool.FormatModifiers(eventField.Modifiers);
        string type = eventField.Declaration.Type.ToString();
        var names = string.Join(", ", eventField.Declaration.Variables.Select(v => v.Identifier.Text));
        return $"{modifiers}event {type} {names}";
    }
}
