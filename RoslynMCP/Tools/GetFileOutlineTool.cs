using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

/// <summary>
/// Produces a compact, token-efficient outline of a C# file's structure
/// (namespaces, types, members) with line numbers for navigation.
/// </summary>
[McpServerToolType]
public static class GetFileOutlineTool
{
    private const string Indent = "  ";

    /// <summary>
    /// Returns a compact tree-style outline of a C# file showing namespaces, types,
    /// and member signatures with line numbers.
    /// </summary>
    [McpServerTool, Description(
        "Get a compact outline of a C# or ASPX file showing namespaces, types, and member " +
        "signatures with line numbers. For ASPX files, shows directives, controls, expressions, " +
        "and code blocks. For Razor files, shows directives, @code block members, and component structure. " +
        "Useful for understanding file structure without reading the full source. " +
        "Supports multiple files separated by semicolons.")]
    public static async Task<string> GetFileOutline(
        [Description("Path to the C# or ASPX file. Separate multiple paths with semicolons.")] string filePath,
        IOutputFormatter fmt,
        IEnumerable<IOutlineHandler>? handlers = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return "Error: File path cannot be empty.";

            var paths = filePath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (paths.Length == 1)
            {
                var singleResult = await GetSingleFileOutline(paths[0], handlers, cancellationToken);
                var sbSingle = new StringBuilder(singleResult);
                fmt.AppendHints(sbSingle,
                    "Use GoToDefinition to navigate to a specific member",
                    "Use FindUsages to find references to a symbol");
                return sbSingle.ToString();
            }

            // Multi-file mode: concatenate outlines
            var sb = new StringBuilder();
            foreach (var path in paths)
            {
                if (sb.Length > 0)
                    sb.AppendLine();

                sb.Append(await GetSingleFileOutline(path, handlers, cancellationToken));
            }
            fmt.AppendHints(sb,
                "Use GoToDefinition to navigate to a specific member",
                "Use FindUsages to find references to a symbol");

            return sb.ToString();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[GetFileOutline] Unhandled error: {ex}");
            return $"Error: {ex.Message}";
        }
    }

    private static async Task<string> GetSingleFileOutline(
        string filePath, IEnumerable<IOutlineHandler>? handlers, CancellationToken cancellationToken)
    {
        string systemPath = PathHelper.NormalizePath(filePath);

        if (!File.Exists(systemPath))
            return $"Error: File {systemPath} does not exist.";

        // Delegate to registered handlers for non-C# file types
        if (handlers is not null)
        {
            foreach (var handler in handlers)
            {
                if (handler.CanHandle(systemPath))
                    return await handler.GetOutlineAsync(systemPath, cancellationToken);
            }
        }

        var errors = new StringBuilder();
        var fileCtx = await ToolHelper.ResolveFileAsync(filePath, errors, cancellationToken);
        if (fileCtx is null)
            return errors.ToString();

        if (fileCtx.Document is null)
            return "Error: File not found in project.";

        var syntaxTree = await fileCtx.Document.GetSyntaxTreeAsync(cancellationToken);
        if (syntaxTree is null)
            return "Error: Unable to obtain syntax tree.";

        var root = await syntaxTree.GetRootAsync(cancellationToken);
        var sb = new StringBuilder();
        sb.AppendLine($"# Outline: {Path.GetFileName(fileCtx.SystemPath)}");
        sb.AppendLine();
        sb.AppendLine("```");
        AppendOutline(sb, root, depth: 0);
        sb.AppendLine("```");

        return sb.ToString();
    }

    private static void AppendOutline(StringBuilder sb, SyntaxNode node, int depth)
    {
        foreach (var child in node.ChildNodes())
        {
            switch (child)
            {
                case BaseNamespaceDeclarationSyntax ns:
                    AppendEntry(sb, depth, $"namespace {ns.Name}", child);
                    AppendOutline(sb, child, depth + 1);
                    break;

                case TypeDeclarationSyntax type:
                    AppendEntry(sb, depth, FormatTypeDeclaration(type), child);
                    AppendOutline(sb, child, depth + 1);
                    break;

                case EnumDeclarationSyntax enumDecl:
                    AppendEntry(sb, depth, FormatEnumDeclaration(enumDecl), child);
                    foreach (var member in enumDecl.Members)
                        AppendEntry(sb, depth + 1, member.Identifier.Text, member);
                    break;

                case DelegateDeclarationSyntax del:
                    AppendEntry(sb, depth, FormatDelegate(del), child);
                    break;

                case MethodDeclarationSyntax method:
                    AppendEntry(sb, depth, FormatMethod(method), child);
                    break;

                case ConstructorDeclarationSyntax ctor:
                    AppendEntry(sb, depth, FormatConstructor(ctor), child);
                    break;

                case DestructorDeclarationSyntax dtor:
                    AppendEntry(sb, depth, $"~{dtor.Identifier.Text}()", child);
                    break;

                case PropertyDeclarationSyntax prop:
                    AppendEntry(sb, depth, FormatProperty(prop), child);
                    break;

                case IndexerDeclarationSyntax indexer:
                    AppendEntry(sb, depth, FormatIndexer(indexer), child);
                    break;

                case FieldDeclarationSyntax field:
                    AppendFieldEntries(sb, depth, field);
                    break;

                case EventFieldDeclarationSyntax eventField:
                    AppendEventFieldEntries(sb, depth, eventField);
                    break;

                case EventDeclarationSyntax eventDecl:
                    AppendEntry(sb, depth,
                        $"{FormatModifiers(eventDecl.Modifiers)}event {eventDecl.Type} {eventDecl.Identifier.Text}",
                        child);
                    break;

                case OperatorDeclarationSyntax op:
                    AppendEntry(sb, depth,
                        $"{FormatModifiers(op.Modifiers)}{op.ReturnType} operator {op.OperatorToken.Text}{op.ParameterList}",
                        child);
                    break;

                case ConversionOperatorDeclarationSyntax conv:
                    AppendEntry(sb, depth,
                        $"{FormatModifiers(conv.Modifiers)}{conv.ImplicitOrExplicitKeyword.Text} operator {conv.Type}{conv.ParameterList}",
                        child);
                    break;

                case GlobalStatementSyntax:
                    // Top-level statements — note their presence without expanding
                    AppendEntry(sb, depth, "<top-level statement>", child);
                    break;
            }
        }
    }

    private static void AppendEntry(StringBuilder sb, int depth, string text, SyntaxNode node)
    {
        var lineSpan = node.GetLocation().GetLineSpan();
        int startLine = lineSpan.StartLinePosition.Line + 1;
        int endLine = lineSpan.EndLinePosition.Line + 1;
        string prefix = string.Concat(Enumerable.Repeat(Indent, depth));

        if (endLine > startLine)
            sb.AppendLine($"{startLine,4}-{endLine,-4}: {prefix}{text}");
        else
            sb.AppendLine($"{startLine,4}: {prefix}{text}");
    }

    private static string FormatTypeDeclaration(TypeDeclarationSyntax type)
    {
        var sb = new StringBuilder();
        sb.Append(FormatModifiers(type.Modifiers));
        sb.Append(type.Keyword.Text);
        sb.Append(' ');
        sb.Append(type.Identifier.Text);
        sb.Append(type.TypeParameterList?.ToString() ?? "");

        // Include primary constructor parameters for records
        if (type is RecordDeclarationSyntax record && record.ParameterList is not null)
            sb.Append(record.ParameterList);

        if (type.BaseList is not null)
        {
            sb.Append(' ');
            sb.Append(type.BaseList);
        }

        return sb.ToString();
    }

    private static string FormatEnumDeclaration(EnumDeclarationSyntax enumDecl)
    {
        var sb = new StringBuilder();
        sb.Append(FormatModifiers(enumDecl.Modifiers));
        sb.Append("enum ");
        sb.Append(enumDecl.Identifier.Text);

        if (enumDecl.BaseList is not null)
        {
            sb.Append(' ');
            sb.Append(enumDecl.BaseList);
        }

        return sb.ToString();
    }

    private static string FormatDelegate(DelegateDeclarationSyntax del)
    {
        var sb = new StringBuilder();
        sb.Append(FormatModifiers(del.Modifiers));
        sb.Append("delegate ");
        sb.Append(del.ReturnType);
        sb.Append(' ');
        sb.Append(del.Identifier.Text);
        sb.Append(del.TypeParameterList?.ToString() ?? "");
        sb.Append(NormalizeParamList(del.ParameterList));
        return sb.ToString();
    }

    internal static string FormatMethod(MethodDeclarationSyntax method)
    {
        var sb = new StringBuilder();
        sb.Append(FormatModifiers(method.Modifiers));
        sb.Append(method.ReturnType);
        sb.Append(' ');
        sb.Append(method.Identifier.Text);
        sb.Append(method.TypeParameterList?.ToString() ?? "");
        sb.Append(NormalizeParamList(method.ParameterList));
        return sb.ToString();
    }

    private static string FormatConstructor(ConstructorDeclarationSyntax ctor)
    {
        var sb = new StringBuilder();
        sb.Append(FormatModifiers(ctor.Modifiers));
        sb.Append(ctor.Identifier.Text);
        sb.Append(NormalizeParamList(ctor.ParameterList));
        return sb.ToString();
    }

    internal static string FormatProperty(PropertyDeclarationSyntax prop)
    {
        var sb = new StringBuilder();
        sb.Append(FormatModifiers(prop.Modifiers));
        sb.Append(prop.Type);
        sb.Append(' ');
        sb.Append(prop.Identifier.Text);
        sb.Append(FormatAccessors(prop.AccessorList));
        return sb.ToString();
    }

    private static string FormatIndexer(IndexerDeclarationSyntax indexer)
    {
        var sb = new StringBuilder();
        sb.Append(FormatModifiers(indexer.Modifiers));
        sb.Append(indexer.Type);
        sb.Append(" this");
        sb.Append(NormalizeParamList(indexer.ParameterList));
        sb.Append(FormatAccessors(indexer.AccessorList));
        return sb.ToString();
    }

    private static void AppendFieldEntries(StringBuilder sb, int depth, FieldDeclarationSyntax field)
    {
        string modifiers = FormatModifiers(field.Modifiers);
        string type = field.Declaration.Type.ToString();
        foreach (var variable in field.Declaration.Variables)
            AppendEntry(sb, depth, $"{modifiers}{type} {variable.Identifier.Text}", field);
    }

    private static void AppendEventFieldEntries(StringBuilder sb, int depth, EventFieldDeclarationSyntax eventField)
    {
        string modifiers = FormatModifiers(eventField.Modifiers);
        string type = eventField.Declaration.Type.ToString();
        foreach (var variable in eventField.Declaration.Variables)
            AppendEntry(sb, depth, $"{modifiers}event {type} {variable.Identifier.Text}", eventField);
    }

    private static string NormalizeParamList(BaseParameterListSyntax paramList)
    {
        // Collapse any whitespace (including newlines from multi-line param lists) to single spaces
        string raw = paramList.ToString();
        return System.Text.RegularExpressions.Regex.Replace(raw, @"\s+", " ");
    }

    internal static string FormatModifiers(SyntaxTokenList modifiers)
    {
        string text = modifiers.ToString();
        return text.Length > 0 ? text + " " : "";
    }

    private static string FormatAccessors(AccessorListSyntax? accessorList)
    {
        if (accessorList is null)
            return "";

        var accessors = accessorList.Accessors
            .Select(a => a.Keyword.Text)
            .ToList();

        return accessors.Count > 0
            ? " { " + string.Join("; ", accessors) + "; }"
            : "";
    }

}
