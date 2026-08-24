using System.Text;
using Microsoft.CodeAnalysis;
using WebFormsCore.Nodes;
using RoslynMCP.Languages.WebForms.Core;

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

        var style = DesignerStyle.Detect(designerPath);

        var group = await FindInheritsGroupAsync(
            project, filePath, parseResult.ParseTree, codeBehind, compilation, projectDir, cancellationToken);

        if (group.Count > 1)
            return await GenerateForGroupAsync(
                filePath, designerPath, parseResult, codeBehind, group, style, compilation, projectDir,
                cancellationToken);

        var fields = CollectFields(
            parseResult.ParseTree, codeBehind,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { designerPath });
        var masterType = await ResolveMasterTypeAsync(
            parseResult, filePath, compilation, projectDir, cancellationToken);

        return new DesignerResult(
            designerPath, Render(codeBehind, KeepExistingOrder(fields, style), masterType, style), []);
    }

    /// <summary>
    /// The designer for a class several markup files share. Visual Studio regenerates each file's
    /// designer in isolation, which silently drops the fields the other variants need; here the
    /// canonical file — the one beside the class's own code-behind — gets the union of every
    /// variant's controls, and the variants get an empty partial so nothing is declared twice.
    /// A control missing from some variants is emitted nullable, because it genuinely is null when
    /// one of those variants is the one loaded.
    /// </summary>
    private async Task<DesignerResult> GenerateForGroupAsync(
        string filePath,
        string designerPath,
        AspxParseResult parseResult,
        INamedTypeSymbol codeBehind,
        List<MarkupFile> group,
        DesignerStyle style,
        Compilation compilation,
        string? projectDir,
        CancellationToken cancellationToken)
    {
        var canonicalPath = SelectCanonicalPath(group, codeBehind);

        if (!PathsEqual(canonicalPath, filePath))
        {
            return new DesignerResult(designerPath, Render(codeBehind, [], masterType: null, style), [])
            {
                RelatedSources = [canonicalPath],
            };
        }

        var designerPaths = group
            .Select(file => GetDesignerPath(file.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var fields = CollectUnionFields(group, canonicalPath, codeBehind, designerPaths);
        var masterType = await ResolveMasterTypeAsync(
            parseResult, filePath, compilation, projectDir, cancellationToken);

        return new DesignerResult(
            designerPath,
            Render(codeBehind, KeepExistingOrder(fields, style), masterType, style, nullableDirective: true),
            [])
        {
            RelatedSources = [.. group.Select(file => file.Path).Where(path => !PathsEqual(path, filePath))],
        };
    }

    private readonly record struct MarkupFile(string Path, RootNode Tree);

    /// <summary>
    /// Every markup file in the project whose <c>Inherits</c> resolves to the same code-behind
    /// class, the requested file included. Almost always a list of one.
    /// </summary>
    private static async Task<List<MarkupFile>> FindInheritsGroupAsync(
        Project project,
        string filePath,
        RootNode selfTree,
        INamedTypeSymbol codeBehind,
        Compilation compilation,
        string? projectDir,
        CancellationToken cancellationToken)
    {
        var group = new List<MarkupFile> { new(filePath, selfTree) };
        var className = codeBehind.Name;

        foreach (var candidate in AspxReferenceService.EnumerateFiles(project))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (PathsEqual(candidate, filePath))
                continue;
            if (!DesignerExtensions.Contains(Path.GetExtension(candidate), StringComparer.OrdinalIgnoreCase))
                continue;

            // Cheap textual pre-filter: a file that never mentions the class name cannot inherit it.
            string text;
            try
            {
                text = await File.ReadAllTextAsync(candidate, cancellationToken);
            }
            catch (IOException)
            {
                continue;
            }

            if (!text.Contains(className, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var parsed = await ParseAsync(candidate, compilation, projectDir, cancellationToken);
                if (parsed.ParseTree is { } tree
                    && SymbolEqualityComparer.Default.Equals(tree.Inherits, codeBehind))
                {
                    group.Add(new MarkupFile(candidate, tree));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A sibling that fails to parse simply is not part of the group.
            }
        }

        return group;
    }

    /// <summary>
    /// The group member whose designer carries the fields: the markup sitting beside the class's
    /// own code-behind file, by the <c>Foo.aspx</c> → <c>Foo.aspx.cs</c> convention. Falls back to
    /// the lexicographically first path so the choice is stable either way.
    /// </summary>
    private static string SelectCanonicalPath(List<MarkupFile> group, INamedTypeSymbol codeBehind)
    {
        var declaringFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in codeBehind.DeclaringSyntaxReferences)
        {
            if (reference.SyntaxTree.FilePath is { Length: > 0 } path)
                declaringFiles.Add(path);
        }

        var candidates = group.Select(file => file.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        string? first = null;

        foreach (var path in candidates)
        {
            first ??= path;
            if (declaringFiles.Contains(path + ".cs"))
                return path;
        }

        return first!;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

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

    private readonly record struct DesignerField(string Name, string TypeName, bool Nullable = false);

    /// <summary>
    /// Collects the controls that need a generated field.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Walks the non-template hierarchy plus the contents of single-instance templates.
    /// A control inside a multi-instance template genuinely gets no designer field (it is reached
    /// through <c>FindControl</c> instead), and <c>FieldName</c> is set by the parser only for
    /// controls that get one, so it is exactly the right signal.
    /// </para>
    /// <para>
    /// A control whose field is already declared by hand in the code-behind is skipped: emitting it
    /// here too would be a duplicate member. Declarations coming from a designer file being
    /// regenerated do not count, since that file is about to be replaced.
    /// </para>
    /// </remarks>
    private static List<DesignerField> CollectFields(
        RootNode root, INamedTypeSymbol codeBehind, IReadOnlySet<string> designerPaths)
    {
        var fields = new List<DesignerField>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (name, type) in EnumerateDesignerControls(root))
        {
            if (!seen.Add(name))
                continue;
            if (IsDeclaredOutsideDesigner(codeBehind, name, designerPaths))
                continue;

            fields.Add(new DesignerField(
                name, type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
        }

        return fields;
    }

    /// <summary>
    /// The union of every group member's controls, in canonical-file-first order. A control that
    /// is absent from at least one variant comes out nullable: when that variant is the one
    /// loaded, the field really is null.
    /// </summary>
    private static List<DesignerField> CollectUnionFields(
        List<MarkupFile> group,
        string canonicalPath,
        INamedTypeSymbol codeBehind,
        IReadOnlySet<string> designerPaths)
    {
        var ordered = group
            .OrderBy(file => PathsEqual(file.Path, canonicalPath) ? 0 : 1)
            .ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase);

        var names = new List<string>();
        var types = new Dictionary<string, INamedTypeSymbol>(StringComparer.Ordinal);
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var file in ordered)
        {
            var seenInFile = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (name, type) in EnumerateDesignerControls(file.Tree))
            {
                if (!seenInFile.Add(name))
                    continue;

                if (types.TryGetValue(name, out var existing))
                {
                    // The same ID declared as different types across variants still needs one
                    // field both can assign to, so it is typed as their nearest common base.
                    types[name] = CommonBaseType(existing, type);
                    occurrences[name]++;
                }
                else
                {
                    names.Add(name);
                    types[name] = type;
                    occurrences[name] = 1;
                }
            }
        }

        var fields = new List<DesignerField>();
        foreach (var name in names)
        {
            if (IsDeclaredOutsideDesigner(codeBehind, name, designerPaths))
                continue;

            fields.Add(new DesignerField(
                name,
                types[name].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                Nullable: occurrences[name] < group.Count));
        }

        return fields;
    }

    /// <summary>
    /// Puts the fields back in the order the designer file already declares them, with fields the
    /// markup has newly gained appended in markup order.
    /// </summary>
    /// <remarks>
    /// Markup order is the order Visual Studio would emit, but a designer file that has been around
    /// for a while no longer matches it: controls get moved around the page, and Visual Studio only
    /// rewrites the file when it happens to be open. Re-sorting on every regeneration produces a
    /// diff of hundreds of moved lines that says nothing, so the file's own order wins and a real
    /// change shows up as the addition it is.
    /// </remarks>
    private static List<DesignerField> KeepExistingOrder(List<DesignerField> fields, DesignerStyle style)
    {
        if (style.FieldOrder.Count == 0)
            return fields;

        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < style.FieldOrder.Count; i++)
            rank.TryAdd(style.FieldOrder[i], i);

        // OrderBy is stable, so the fields with no place yet stay in markup order behind the rest.
        return [.. fields.OrderBy(field => rank.TryGetValue(field.Name, out var index) ? index : int.MaxValue)];
    }

    private static IEnumerable<(string Name, INamedTypeSymbol Type)> EnumerateDesignerControls(RootNode root)
    {
        foreach (var control in root.AllChildren.OfType<ControlNode>())
        {
            if (control.FieldName is { Length: > 0 } name)
                yield return (name, control.ControlType);
        }

        // Template contents live on TemplateNode, outside the child hierarchy. Root.Templates is a
        // flat list of every template in the file; only single-instance ones can contribute fields,
        // and a control nested in any multi-instance template has no FieldName to contribute.
        foreach (var template in root.Templates)
        {
            if (!template.IsSingleInstance)
                continue;

            foreach (var control in template.AllChildren.OfType<ControlNode>())
            {
                if (control.FieldName is { Length: > 0 } name)
                    yield return (name, control.ControlType);
            }
        }
    }

    private static INamedTypeSymbol CommonBaseType(INamedTypeSymbol left, INamedTypeSymbol right)
    {
        for (var candidate = left; candidate is not null; candidate = candidate.BaseType)
        {
            for (var type = right; type is not null; type = type.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(type, candidate))
                    return candidate;
            }
        }

        return left;
    }

    private static bool IsDeclaredOutsideDesigner(
        INamedTypeSymbol codeBehind, string memberName, IReadOnlySet<string> designerPaths)
    {
        foreach (var member in codeBehind.GetMembers(memberName))
        {
            foreach (var reference in member.DeclaringSyntaxReferences)
            {
                var path = reference.SyntaxTree.FilePath;
                if (!string.IsNullOrEmpty(path) && !designerPaths.Contains(path))
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
    // rewriting every line of a file Visual Studio wrote. A file that has since been reformatted
    // keeps its own conventions instead — see DesignerStyle.
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
        INamedTypeSymbol codeBehind, List<DesignerField> fields, INamedTypeSymbol? masterType,
        DesignerStyle style, bool nullableDirective = false)
    {
        var ns = codeBehind.ContainingNamespace is { IsGlobalNamespace: false } containing
            ? containing.ToDisplayString()
            : null;

        var sb = new StringBuilder();
        sb.Append(style.Header ?? Header);

        if (nullableDirective)
        {
            sb.AppendLine("#nullable enable");
            sb.AppendLine();
        }

        var indent = ns is null ? "" : "    ";
        if (ns is not null)
        {
            AppendBlockStart(sb, style, "", "namespace " + ns);
            AppendBlank(sb, style, indent);
            AppendBlank(sb, style, indent);
        }

        AppendBlockStart(sb, style, indent, "public partial class " + codeBehind.Name);

        var memberIndent = indent + "    ";
        foreach (var field in fields)
        {
            AppendBlank(sb, style, memberIndent);
            AppendField(sb, memberIndent, field);
        }

        if (masterType is not null)
        {
            AppendBlank(sb, style, memberIndent);
            AppendMasterProperty(sb, memberIndent, masterType, style);
        }

        sb.Append(indent).AppendLine("}");
        if (ns is not null)
            sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>Writes a declaration and its opening brace, on one line or two.</summary>
    private static void AppendBlockStart(
        StringBuilder sb, DesignerStyle style, string indent, string declaration)
    {
        if (style.BraceOnNewLine)
            sb.Append(indent).AppendLine(declaration).Append(indent).AppendLine("{");
        else
            sb.Append(indent).Append(declaration).AppendLine(" {");
    }

    private static void AppendBlank(StringBuilder sb, DesignerStyle style, string indent) =>
        sb.AppendLine(style.IndentBlankLines ? indent : "");

    private static void AppendField(StringBuilder sb, string indent, DesignerField field)
    {
        sb.Append(indent).AppendLine("/// <summary>");
        sb.Append(indent).Append("/// ").Append(field.Name).AppendLine(" control.");
        sb.Append(indent).AppendLine("/// </summary>");
        sb.Append(indent).AppendLine("/// <remarks>");
        sb.Append(indent).AppendLine("/// Auto-generated field.");
        sb.Append(indent).AppendLine("/// To modify move field declaration from designer file to code-behind file.");
        sb.Append(indent).AppendLine("/// </remarks>");
        sb.Append(indent).Append("protected ").Append(field.TypeName);
        if (field.Nullable)
            sb.Append('?');
        sb.Append(' ').Append(field.Name).AppendLine(";");
    }

    private static void AppendMasterProperty(
        StringBuilder sb, string indent, INamedTypeSymbol masterType, DesignerStyle style)
    {
        var name = masterType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", "", StringComparison.Ordinal);

        sb.Append(indent).AppendLine("/// <summary>");
        sb.Append(indent).AppendLine("/// Master property.");
        sb.Append(indent).AppendLine("/// </summary>");
        sb.Append(indent).AppendLine("/// <remarks>");
        sb.Append(indent).AppendLine("/// Auto-generated property.");
        sb.Append(indent).AppendLine("/// </remarks>");
        AppendBlockStart(sb, style, indent, $"public new {name} Master");
        AppendBlockStart(sb, style, indent + "    ", "get");
        sb.Append(indent).Append("        ").Append("return ((").Append(name).AppendLine(")(base.Master));");
        sb.Append(indent).AppendLine("    }");
        sb.Append(indent).AppendLine("}");
    }
}
