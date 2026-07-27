using System.Text;
using Microsoft.CodeAnalysis;
using WebFormsCore.Nodes;

namespace RoslynMCP.Services.Designers;

/// <summary>
/// Regenerates the <c>.designer.cs</c> companion for a WebForms markup file, the job Visual
/// Studio's WebForms designer does on save.
/// </summary>
/// <remarks>
/// The parser already resolves every server control's CLR type against the project's compilation
/// (honouring <c>web.config</c> tag registrations and <c>&lt;%@ Register %&gt;</c> directives), so
/// this is mostly a formatting job on top of <see cref="AspxSourceMappingService"/>.
/// </remarks>
internal sealed class AspxDesignerGenerator : IDesignerGenerator
{
    public bool CanHandle(string filePath) => AspxSourceMappingService.IsAspxFile(filePath)
        && DesignerExtensions.Contains(Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Only markup that has a code-behind class gets a designer file. Handlers and services
    /// (<c>.ashx</c>, <c>.asmx</c>) are parsed by the same service but have no control tree.
    /// </summary>
    private static readonly string[] DesignerExtensions = [".aspx", ".ascx", ".master"];

    /// <summary>Visual Studio appends <c>.designer.cs</c> to the full markup file name.</summary>
    public string GetDesignerPath(string filePath) => filePath + ".designer.cs";

    public async Task<DesignerResult> GenerateAsync(
        string filePath, Project project, CancellationToken cancellationToken)
    {
        var designerPath = GetDesignerPath(filePath);

        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation is null)
            return DesignerResult.Failed(designerPath, "Unable to produce a compilation for the project.");

        var projectDir = Path.GetDirectoryName(project.FilePath);
        var parseResult = await ParseAsync(filePath, compilation, projectDir, cancellationToken);

        if (parseResult.ParseTree is null)
            return DesignerResult.Failed(designerPath, "Markup could not be parsed.");

        // A markup file whose Inherits attribute does not resolve has no partial class to extend.
        // Emitting anyway would produce a file that cannot compile, so refuse instead.
        var codeBehind = parseResult.ParseTree.Inherits;
        if (codeBehind is null)
        {
            return DesignerResult.Failed(designerPath,
                "The Inherits type could not be resolved. Check the @Page/@Control directive and " +
                "that the code-behind class exists in this project.");
        }

        var fields = CollectFields(parseResult.ParseTree, codeBehind, designerPath);
        var masterType = await ResolveMasterTypeAsync(
            parseResult, filePath, compilation, projectDir, cancellationToken);

        return new DesignerResult(designerPath, Render(codeBehind, fields, masterType), []);
    }

    private static async Task<AspxParseResult> ParseAsync(
        string filePath, Compilation compilation, string? projectDir, CancellationToken cancellationToken)
    {
        var namespaces = projectDir is not null
            ? AspxSourceMappingService.LoadWebConfigNamespaces(projectDir)
            : default;

        var text = await File.ReadAllTextAsync(filePath, cancellationToken);
        return AspxSourceMappingService.Parse(
            filePath, text, compilation,
            namespaces: namespaces.IsDefaultOrEmpty ? null : namespaces,
            rootDirectory: projectDir);
    }

    private readonly record struct DesignerField(string Name, string TypeName);

    /// <summary>
    /// Collects the controls that need a generated field.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Walks <see cref="ContainerNode.AllChildren"/>, which deliberately excludes template contents:
    /// the parser stores templates on <c>ControlNode.Templates</c> rather than in the child
    /// hierarchy, and a template-nested control genuinely gets no designer field (it is reached
    /// through <c>FindControl</c> instead). <c>FieldName</c> is set by the parser only for
    /// non-template controls carrying an <c>ID</c>, so it is exactly the right signal.
    /// </para>
    /// <para>
    /// A control whose field is already declared by hand in the code-behind is skipped: emitting it
    /// here too would be a duplicate member. Declarations coming from the designer file being
    /// regenerated do not count, since that file is about to be replaced.
    /// </para>
    /// </remarks>
    private static List<DesignerField> CollectFields(
        RootNode root, INamedTypeSymbol codeBehind, string designerPath)
    {
        var fields = new List<DesignerField>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var control in root.AllChildren.OfType<ControlNode>())
        {
            if (control.FieldName is not { Length: > 0 } name)
                continue;
            if (!seen.Add(name))
                continue;
            if (IsDeclaredOutsideDesigner(codeBehind, name, designerPath))
                continue;

            fields.Add(new DesignerField(name, control.DisplayControlType));
        }

        return fields;
    }

    private static bool IsDeclaredOutsideDesigner(
        INamedTypeSymbol codeBehind, string memberName, string designerPath)
    {
        foreach (var member in codeBehind.GetMembers(memberName))
        {
            foreach (var reference in member.DeclaringSyntaxReferences)
            {
                var path = reference.SyntaxTree.FilePath;
                if (!string.IsNullOrEmpty(path) &&
                    !string.Equals(path, designerPath, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves the code-behind type of a page's master, so the designer can expose the
    /// strongly-typed <c>Master</c> property Visual Studio generates. Returns <c>null</c> when the
    /// page has no master or the master cannot be resolved — the property is then simply omitted.
    /// </summary>
    private static async Task<INamedTypeSymbol?> ResolveMasterTypeAsync(
        AspxParseResult parseResult,
        string filePath,
        Compilation compilation,
        string? projectDir,
        CancellationToken cancellationToken)
    {
        var masterPageFile = parseResult.Directives
            .Where(d => d.Type.Equals("Page", StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Attributes.TryGetValue("MasterPageFile", out var value) ? value : null)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        if (masterPageFile is null)
            return null;

        var masterPath = ResolveMarkupPath(masterPageFile, filePath, projectDir);
        if (masterPath is null || !File.Exists(masterPath))
            return null;

        try
        {
            var masterResult = await ParseAsync(masterPath, compilation, projectDir, cancellationToken);
            return masterResult.ParseTree?.Inherits;
        }
        catch (Exception)
        {
            // An unparseable master must not block the page's own designer.
            return null;
        }
    }

    /// <summary>
    /// Resolves a markup reference, which is either application-rooted (<c>~/Site.master</c>) or
    /// relative to the referencing file.
    /// </summary>
    private static string? ResolveMarkupPath(string reference, string filePath, string? projectDir)
    {
        var trimmed = reference.Trim().Replace('/', Path.DirectorySeparatorChar);

        try
        {
            if (trimmed.StartsWith("~" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return projectDir is null
                    ? null
                    : Path.GetFullPath(Path.Combine(projectDir, trimmed[2..]));
            }

            var dir = Path.GetDirectoryName(filePath);
            return dir is null ? null : Path.GetFullPath(Path.Combine(dir, trimmed));
        }
        catch
        {
            return null;
        }
    }

    // Visual Studio's designer files come out of CodeDOM, which indents otherwise-blank separator
    // lines to the current nesting level. Reproducing that exactly keeps regeneration from
    // rewriting every line of a file Visual Studio wrote.
    private const string Header =
        """
        //------------------------------------------------------------------------------
        // <auto-generated>
        //     This code was generated by a tool.
        //
        //     Changes to this file may cause incorrect behavior and will be lost if
        //     the code is regenerated.
        // </auto-generated>
        //------------------------------------------------------------------------------


        """;

    private static string Render(
        INamedTypeSymbol codeBehind, List<DesignerField> fields, INamedTypeSymbol? masterType)
    {
        var ns = codeBehind.ContainingNamespace is { IsGlobalNamespace: false } containing
            ? containing.ToDisplayString()
            : null;

        var sb = new StringBuilder();
        sb.Append(Header);

        var indent = ns is null ? "" : "    ";
        if (ns is not null)
        {
            sb.Append("namespace ").Append(ns).AppendLine(" {");
            sb.AppendLine(indent);
            sb.AppendLine(indent);
        }

        sb.Append(indent).Append("public partial class ").Append(codeBehind.Name).AppendLine(" {");

        var memberIndent = indent + "    ";
        foreach (var field in fields)
        {
            sb.AppendLine(memberIndent);
            AppendField(sb, memberIndent, field);
        }

        if (masterType is not null)
        {
            sb.AppendLine(memberIndent);
            AppendMasterProperty(sb, memberIndent, masterType);
        }

        sb.Append(indent).AppendLine("}");
        if (ns is not null)
            sb.AppendLine("}");

        return sb.ToString();
    }

    private static void AppendField(StringBuilder sb, string indent, DesignerField field)
    {
        sb.Append(indent).AppendLine("/// <summary>");
        sb.Append(indent).Append("/// ").Append(field.Name).AppendLine(" control.");
        sb.Append(indent).AppendLine("/// </summary>");
        sb.Append(indent).AppendLine("/// <remarks>");
        sb.Append(indent).AppendLine("/// Auto-generated field.");
        sb.Append(indent).AppendLine("/// To modify move field declaration from designer file to code-behind file.");
        sb.Append(indent).AppendLine("/// </remarks>");
        sb.Append(indent).Append("protected ").Append(field.TypeName).Append(' ').Append(field.Name).AppendLine(";");
    }

    private static void AppendMasterProperty(StringBuilder sb, string indent, INamedTypeSymbol masterType)
    {
        var name = masterType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", "", StringComparison.Ordinal);

        sb.Append(indent).AppendLine("/// <summary>");
        sb.Append(indent).AppendLine("/// Master property.");
        sb.Append(indent).AppendLine("/// </summary>");
        sb.Append(indent).AppendLine("/// <remarks>");
        sb.Append(indent).AppendLine("/// Auto-generated property.");
        sb.Append(indent).AppendLine("/// </remarks>");
        sb.Append(indent).Append("public new ").Append(name).AppendLine(" Master {");
        sb.Append(indent).AppendLine("    get {");
        sb.Append(indent).Append("        return ((").Append(name).AppendLine(")(base.Master));");
        sb.Append(indent).AppendLine("    }");
        sb.Append(indent).AppendLine("}");
    }
}
