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
        "and code blocks. Useful for understanding file structure without reading the full source.")]
    public static async Task<string> GetFileOutline(
        [Description("Path to the C# or ASPX file.")] string filePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return "Error: File path cannot be empty.";

            string systemPath = PathHelper.NormalizePath(filePath);

            if (!File.Exists(systemPath))
                return $"Error: File {systemPath} does not exist.";

            // ASPX files: parse and show directive/control/expression outline
            if (AspxSourceMappingService.IsAspxFile(systemPath))
                return await GetAspxOutlineAsync(systemPath, cancellationToken);

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
        int line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        string prefix = string.Concat(Enumerable.Repeat(Indent, depth));
        sb.AppendLine($"{line,4}: {prefix}{text}");
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
        sb.Append(del.ParameterList);
        return sb.ToString();
    }

    private static string FormatMethod(MethodDeclarationSyntax method)
    {
        var sb = new StringBuilder();
        sb.Append(FormatModifiers(method.Modifiers));
        sb.Append(method.ReturnType);
        sb.Append(' ');
        sb.Append(method.Identifier.Text);
        sb.Append(method.TypeParameterList?.ToString() ?? "");
        sb.Append(method.ParameterList);
        return sb.ToString();
    }

    private static string FormatConstructor(ConstructorDeclarationSyntax ctor)
    {
        var sb = new StringBuilder();
        sb.Append(FormatModifiers(ctor.Modifiers));
        sb.Append(ctor.Identifier.Text);
        sb.Append(ctor.ParameterList);
        return sb.ToString();
    }

    private static string FormatProperty(PropertyDeclarationSyntax prop)
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
        sb.Append(indexer.ParameterList);
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

    private static string FormatModifiers(SyntaxTokenList modifiers)
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

    private static async Task<string> GetAspxOutlineAsync(string filePath, CancellationToken cancellationToken)
    {
        string? projectPath = await WorkspaceService.FindContainingProjectAsync(filePath, cancellationToken);

        // ASPX files aren't Roslyn Documents; walk up to find the nearest .csproj
        if (string.IsNullOrEmpty(projectPath))
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(filePath)!);
            while (dir is not null)
            {
                var csproj = dir.GetFiles("*.csproj").FirstOrDefault();
                if (csproj is not null)
                {
                    projectPath = csproj.FullName;
                    break;
                }
                dir = dir.Parent;
            }
        }

        if (string.IsNullOrEmpty(projectPath))
            return "Error: Couldn't find a project containing this ASPX file.";

        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
            projectPath, cancellationToken: cancellationToken);
        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation is null)
            return "Error: Unable to produce compilation for project.";

        var projectDir = Path.GetDirectoryName(projectPath);
        var webConfigNamespaces = projectDir is not null
            ? AspxSourceMappingService.LoadWebConfigNamespaces(projectDir)
            : default;

        var text = await File.ReadAllTextAsync(filePath, cancellationToken);
        var result = AspxSourceMappingService.Parse(filePath, text, compilation,
            namespaces: webConfigNamespaces.IsDefaultOrEmpty ? null : webConfigNamespaces,
            rootDirectory: projectDir);
        return AspxSourceMappingService.FormatOutline(result);
    }
}
