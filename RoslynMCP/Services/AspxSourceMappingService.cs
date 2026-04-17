using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using WebFormsCore;
using WebFormsCore.Models;
using WebFormsCore.Nodes;
using WebFormsCore.SourceGenerator.Models;

namespace RoslynMCP.Services;

/// <summary>
/// Parses ASPX/ASCX files using WebFormsCore.Parser and provides source-mapped
/// information about directives, controls, expressions, and code blocks.
/// </summary>
internal static class AspxSourceMappingService
{
    private static readonly string[] s_aspxExtensions = [".aspx", ".ascx", ".asmx", ".asax", ".ashx", ".master"];

    /// <summary>
    /// Returns <c>true</c> when the file has an ASPX-family extension.
    /// </summary>
    public static bool IsAspxFile(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return s_aspxExtensions.Any(e => string.Equals(ext, e, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Parses an ASPX file and returns a structured result with all extracted elements.
    /// </summary>
    /// <param name="filePath">Absolute path to the ASPX file.</param>
    /// <param name="text">File content.</param>
    /// <param name="compilation">Roslyn compilation for type resolution.</param>
    /// <param name="namespaces">
    /// Optional tag-prefix → namespace mappings, typically from web.config
    /// <c>&lt;pages&gt;&lt;controls&gt;&lt;add tagPrefix="..." namespace="..."/&gt;</c>.
    /// Use <see cref="LoadWebConfigNamespaces"/> to obtain these.
    /// </param>
    /// <param name="rootDirectory">
    /// Optional project root directory used to resolve <c>@Register src="~/..."</c> paths.
    /// </param>
    public static AspxParseResult Parse(
        string filePath,
        string text,
        Compilation compilation,
        IEnumerable<KeyValuePair<string, string>>? namespaces = null,
        string? rootDirectory = null)
    {
        // Auto-inject default ASP.NET namespace mappings when the compilation
        // references System.Web. In traditional ASP.NET, the 'asp' prefix is
        // implicitly available mapping to System.Web.UI.WebControls etc.
        namespaces = EnsureDefaultAspNetNamespaces(compilation, namespaces);

        var rootNode = RootNode.Parse(
            out var diagnostics,
            compilation,
            fullPath: filePath,
            text: text,
            namespaces: namespaces,
            rootDirectory: rootDirectory,
            generateHash: false);

        if (rootNode is null)
            return new AspxParseResult(filePath, [], [], [], [], [], diagnostics, null);

        var directives = new List<AspxDirectiveInfo>();
        var controls = new List<AspxControlInfo>();
        var expressions = new List<AspxExpressionInfo>();
        var codeBlocks = new List<AspxCodeBlockInfo>();
        var errors = new List<string>();

        CollectDirectives(rootNode, directives);
        CollectNodes(rootNode, controls, expressions, codeBlocks);

        foreach (var diag in diagnostics)
        {
            Diagnostic d = diag;
            errors.Add(d.GetMessage());
        }

        return new AspxParseResult(filePath, directives, controls, expressions, codeBlocks, errors, diagnostics, rootNode);
    }

    /// <summary>
    /// Returns a human-readable outline of the ASPX file suitable for LLM consumption.
    /// </summary>
    public static string FormatOutline(AspxParseResult result)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# ASPX File: {Path.GetFileName(result.FilePath)}");
        sb.AppendLine();

        if (result.Directives.Count > 0)
        {
            sb.AppendLine("## Directives");
            foreach (var directive in result.Directives)
            {
                sb.AppendLine($"- **{directive.Type}** at line {directive.Line}");
                foreach (var (key, value) in directive.Attributes)
                {
                    sb.AppendLine($"  - {key}=\"{value}\"");
                }
            }
            sb.AppendLine();
        }

        if (result.Controls.Count > 0)
        {
            sb.AppendLine("## Server Controls");
            foreach (var control in result.Controls)
            {
                var idPart = control.Id is not null ? $" ID=\"{control.Id}\"" : "";
                sb.AppendLine($"- **{control.TagPrefix}:{control.TagName}**{idPart} ({control.TypeName}) at line {control.Line}");
            }
            sb.AppendLine();
        }

        if (result.Expressions.Count > 0)
        {
            sb.AppendLine("## Inline Expressions");
            foreach (var expr in result.Expressions)
            {
                var kind = expr.Kind switch
                {
                    AspxExpressionKind.Output => "<%=",
                    AspxExpressionKind.Encoded => "<%:",
                    AspxExpressionKind.DataBinding => "<%#",
                    _ => "<%"
                };
                var truncated = expr.Code.Length > 60
                    ? expr.Code[..57] + "..."
                    : expr.Code;
                sb.AppendLine($"- `{kind} {truncated} %>` at line {expr.Line}");
            }
            sb.AppendLine();
        }

        if (result.CodeBlocks.Count > 0)
        {
            sb.AppendLine("## Code Blocks");
            foreach (var block in result.CodeBlocks)
            {
                var preview = block.Code.Split('\n')[0].Trim();
                if (preview.Length > 60)
                    preview = preview[..57] + "...";
                sb.AppendLine($"- `<% {preview} %>` at line {block.Line}");
            }
            sb.AppendLine();
        }

        if (result.Errors.Count > 0)
        {
            sb.AppendLine("## Parse Errors");
            foreach (var error in result.Errors)
            {
                sb.AppendLine($"- {error}");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Maps a position (line, column) in an ASPX file to the corresponding code element,
    /// if the position falls within an inline expression or code block.
    /// Line and column are 0-indexed (matching TokenRange convention).
    /// </summary>
    public static AspxCodeLocation? MapPosition(AspxParseResult result, int line, int column)
    {
        foreach (var expr in result.Expressions)
        {
            if (expr.Range.Includes(line, column))
            {
                return new AspxCodeLocation(
                    expr.Code,
                    expr.Line,
                    expr.Column,
                    AspxCodeLocationType.Expression);
            }
        }

        foreach (var block in result.CodeBlocks)
        {
            if (block.Range.Includes(line, column))
            {
                return new AspxCodeLocation(
                    block.Code,
                    block.Line,
                    block.Column,
                    AspxCodeLocationType.CodeBlock);
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves a symbol from an ASPX file given a markup snippet with [| |] delimiters.
    /// Supports navigating to:
    /// <list type="bullet">
    ///   <item>Control types — e.g. <c>&lt;[|asp:LinkButton|]&gt;</c> → the control class</item>
    ///   <item>Event handlers — e.g. <c>OnClick="[|MyHandler|]"</c> → the code-behind method</item>
    ///   <item>Event names — e.g. <c>[|OnClick|]="MyHandler"</c> → the event on the control</item>
    ///   <item>Properties — e.g. <c>[|Text|]="Hello"</c> → the property on the control</item>
    /// </list>
    /// </summary>
    public static ISymbol? ResolveAspxSymbol(
        AspxParseResult parseResult,
        string fileText,
        MarkupString markup)
    {
        if (parseResult.ParseTree is null)
            return null;

        // Find the marked text position in the ASPX source
        var matches = MarkupSymbolResolver.FindAllOccurrences(fileText, markup.PlainText);
        if (matches.Count != 1)
            return null;

        var match = matches[0];
        int markedStart = MarkupSymbolResolver.MapSnippetOffsetToFile(
            fileText, match, markup.PlainText, markup.SpanStart);
        int markedEnd = MarkupSymbolResolver.MapSnippetOffsetToFile(
            fileText, match, markup.PlainText, markup.SpanStart + markup.SpanLength);
        string markedText = markup.MarkedText;

        // Walk all control nodes looking for a match (including template-nested controls)
        foreach (var control in GetAllControlNodesDeep(parseResult))
        {

            // Check if the marked text falls within the control's tag name
            var tagRange = control.StartTag.ElementRange;
            if (RangeContainsOffset(tagRange, markedStart, markedEnd))
                return control.ControlType;

            // Skip controls that don't contain the marked position
            // Use StartTag.Range which covers the entire opening tag (not just the tag name)
            var fullTagRange = control.StartTag.Range;
            if (!RangeContainsOffset(fullTagRange, markedStart, markedEnd)
                && (control.EndTag is null || !RangeContainsOffset(control.EndTag.Range, markedStart, markedEnd)))
                continue;

            // Check event handler values (e.g., OnClick="[|MyHandler|]")
            foreach (var evt in control.Events)
            {
                if (RangeContainsOffset(evt.Range, markedStart, markedEnd))
                    return evt.Method;
            }

            // Check property values
            foreach (var prop in control.Properties)
            {
                if (RangeContainsOffset(prop.Range, markedStart, markedEnd))
                    return prop.Member.Symbol;
            }

            // Check raw attributes (unprocessed by the parser)
            foreach (var (key, value) in control.Attributes)
            {
                if (RangeContainsOffset(key.Range, markedStart, markedEnd))
                {
                    var keyStr = key.Value;
                    if (keyStr.StartsWith("On", StringComparison.OrdinalIgnoreCase))
                    {
                        var eventSymbol = control.ControlType?.GetDeep<IEventSymbol>(keyStr.Substring(2));
                        if (eventSymbol != null)
                            return eventSymbol;
                    }

                    var member = control.ControlType?.GetMemberDeep(keyStr);
                    if (member != null)
                        return member.Symbol;
                }
                else if (key.Value.Equals("ID", StringComparison.OrdinalIgnoreCase)
                         && RangeContainsOffset(value.Range, markedStart, markedEnd))
                {
                    // Cursor is on the ID attribute value — resolve to code-behind field
                    var member = parseResult.ParseTree.Inherits?.GetMemberDeep(value.Value);
                    if (member?.Symbol is { } sym) return sym;
                    return null; // no code-behind field (template-nested or field not declared)
                }
            }

            // Semantic fallback: match marked text against event/property names
            // when the attribute name offset isn't stored (events are consumed by the parser)
            if (markedText.StartsWith("On", StringComparison.OrdinalIgnoreCase))
            {
                var eventName = markedText.Substring(2);
                foreach (var evt in control.Events)
                {
                    if (string.Equals(evt.EventName, eventName, StringComparison.OrdinalIgnoreCase))
                        return evt.Event;
                }

                // Also check on the control type directly
                var eventSymbol = control.ControlType?.GetDeep<IEventSymbol>(eventName);
                if (eventSymbol != null)
                    return eventSymbol;
            }

            // Match by event handler method name
            foreach (var evt in control.Events)
            {
                if (string.Equals(evt.MethodName, markedText, StringComparison.OrdinalIgnoreCase))
                    return evt.Method;
            }

            // Match by property name
            var memberResult = control.ControlType?.GetMemberDeep(markedText);
            if (memberResult != null)
                return memberResult.Symbol;

            // Fallback: match as control ID value (when ID attr is not in Attributes dict)
            if (control.Id is not null
                && string.Equals(markedText, control.Id, StringComparison.OrdinalIgnoreCase))
            {
                var member = parseResult.ParseTree.Inherits?.GetMemberDeep(control.Id);
                if (member?.Symbol is { } sym) return sym;
                return null; // no code-behind field (template-nested or field not declared)
            }
        }

        // Check if marked text matches the page base type (Inherits directive)
        if (parseResult.ParseTree.Inherits is { } inherits)
        {
            var inheritsName = inherits.ToDisplayString();
            if (inheritsName.Contains(markedText, StringComparison.OrdinalIgnoreCase))
                return inherits;
        }

        return null;
    }

    /// <summary>
    /// Enumerates all <see cref="ControlNode"/> instances in the parse tree, including those
    /// nested inside <see cref="TemplateNode"/>s (which are not reachable via <see cref="ContainerNode.AllChildren"/>
    /// because templates are stored in <see cref="ControlNode.Templates"/>, not in <see cref="ContainerNode.Children"/>).
    /// </summary>
    private static IEnumerable<ControlNode> GetAllControlNodesDeep(AspxParseResult parseResult)
    {
        if (parseResult.ParseTree is null) yield break;

        foreach (var node in parseResult.ParseTree.AllChildren)
            if (node is ControlNode c) yield return c;

        // TemplateNode objects are stored in ControlNode.Templates (added to Root.Templates)
        // and are NOT part of the Children hierarchy, so AllChildren above misses them.
        foreach (var template in parseResult.ParseTree.Templates)
            foreach (var node in template.AllChildren)
                if (node is ControlNode c) yield return c;
    }

    private static bool RangeContainsOffset(TokenRange range, int startOffset, int endOffset)
    {
        return range.Start.Offset <= startOffset && range.End.Offset >= endOffset;
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="control"/> is nested inside a <see cref="TemplateNode"/>,
    /// meaning it has no direct code-behind field and must be accessed via <c>FindControl</c>.
    /// </summary>
    internal static bool IsControlInTemplate(ControlNode control)
    {
        var parent = control.Parent;
        while (parent != null)
        {
            if (parent is TemplateNode) return true;
            parent = parent.Parent;
        }
        return false;
    }

    /// <summary>
    /// Finds the <see cref="ControlNode"/> whose <c>ID</c> attribute value is at the cursor position
    /// indicated by the markup snippet. Returns <c>null</c> if no such control is found.
    /// </summary>
    internal static ControlNode? FindControlNodeAtCursor(
        AspxParseResult parseResult, string fileText, MarkupString markup)
    {
        if (parseResult.ParseTree is null) return null;

        var matches = MarkupSymbolResolver.FindAllOccurrences(fileText, markup.PlainText);
        if (matches.Count != 1) return null;

        var match = matches[0];
        int markedStart = MarkupSymbolResolver.MapSnippetOffsetToFile(
            fileText, match, markup.PlainText, markup.SpanStart);
        int markedEnd = MarkupSymbolResolver.MapSnippetOffsetToFile(
            fileText, match, markup.PlainText, markup.SpanStart + markup.SpanLength);
        string markedText = markup.MarkedText;

        foreach (var control in GetAllControlNodesDeep(parseResult))
        {
            if (control.Id is null) continue;
            if (!string.Equals(markedText, control.Id, StringComparison.OrdinalIgnoreCase)) continue;

            var fullTagRange = control.StartTag.Range;
            if (RangeContainsOffset(fullTagRange, markedStart, markedEnd))
                return control;
        }

        return null;
    }

    /// <summary>
    /// Scans all C# documents in the project for methods that pass one of their string parameters
    /// directly to <c>FindControl</c>. Returns a list of <c>(MethodName, ParameterIndex)</c> pairs
    /// that can be used as wrapper methods when searching for control ID references.
    /// </summary>
    internal static async Task<List<(string MethodName, int ParamIndex, bool IsExtension)>> FindControlAccessorMethodsAsync(
        Project project, CancellationToken ct)
    {
        var wrappers = new List<(string MethodName, int ParamIndex, bool IsExtension)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var document in project.Documents)
        {
            ct.ThrowIfCancellationRequested();

            // Fast path: skip documents that don't contain "FindControl" at all.
            var docText = await document.GetTextAsync(ct);
            if (!docText.ToString().Contains("FindControl", StringComparison.Ordinal))
                continue;

            var root = await document.GetSyntaxRootAsync(ct);
            if (root is null) continue;

            foreach (var inv in root.DescendantNodes()
                         .OfType<InvocationExpressionSyntax>())
            {
                if (!IsInvocationNamed(inv, "FindControl")) continue;

                var args = inv.ArgumentList.Arguments;
                if (args.Count == 0) continue;

                // The argument must be a plain identifier (i.e. a forwarded parameter, not a literal).
                var argExpr = args[0].Expression;
                if (argExpr is not IdentifierNameSyntax ident) continue;

                var methodDecl = inv.AncestorsAndSelf()
                    .OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault();
                if (methodDecl is null) continue;

                // Find which parameter index corresponds to the identifier — syntax-only, no semantic model needed.
                var paramList = methodDecl.ParameterList.Parameters;
                int paramIndex = -1;
                for (int i = 0; i < paramList.Count; i++)
                {
                    if (paramList[i].Identifier.Text == ident.Identifier.Text)
                    {
                        paramIndex = i;
                        break;
                    }
                }
                if (paramIndex < 0) continue;

                bool isExtension = paramList.Count > 0
                    && paramList[0].Modifiers.Any(m => m.IsKind(SyntaxKind.ThisKeyword));

                string methodName = methodDecl.Identifier.Text;
                var key = $"{methodName}:{paramIndex}";
                if (seen.Add(key))
                    wrappers.Add((methodName, paramIndex, isExtension));
            }
        }

        return wrappers;
    }

    /// <summary>
    /// Searches all C# documents in the project for <c>FindControl("id")</c> calls and calls to
    /// discovered wrapper methods (e.g. <c>item.SetText("id", ...)</c>) that pass the control ID
    /// as a string literal. Extension methods called in member-access style (receiver.Method(...))
    /// are handled by adjusting the argument index.
    /// </summary>
    internal static async Task<List<AspxSymbolReference>> FindControlByIdAsync(
        Project project,
        string controlId,
        IReadOnlyList<(string MethodName, int ParamIndex, bool IsExtension)>? wrappers,
        CancellationToken ct)
    {
        var references = new List<AspxSymbolReference>();

        foreach (var document in project.Documents)
        {
            ct.ThrowIfCancellationRequested();

            var filePath = document.FilePath;
            if (string.IsNullOrEmpty(filePath)) continue;

            // Fast path: skip documents that contain neither "FindControl" nor any wrapper name.
            var docText = await document.GetTextAsync(ct);
            var docStr = docText.ToString();
            bool hasDirectCall = docStr.Contains("FindControl", StringComparison.Ordinal);
            bool hasWrapperCall = wrappers is not null
                && wrappers.Any(w => docStr.Contains(w.MethodName, StringComparison.Ordinal));
            if (!hasDirectCall && !hasWrapperCall)
                continue;

            var root = await document.GetSyntaxRootAsync(ct);
            if (root is null) continue;

            foreach (var inv in root.DescendantNodes()
                         .OfType<InvocationExpressionSyntax>())
            {
                var memberName = GetInvocationMemberName(inv);
                var args = inv.ArgumentList.Arguments;

                // Direct: FindControl("id")
                if (string.Equals(memberName, "FindControl", StringComparison.Ordinal)
                    && args.Count >= 1
                    && IsStringLiteralWithValue(args[0].Expression, controlId))
                {
                    AddFindControlRef(references, filePath, inv);
                    continue;
                }

                // Wrappers: e.g. item.SetText("id", value)
                if (wrappers is null) continue;
                foreach (var (wrapperName, paramIdx, isExtension) in wrappers)
                {
                    if (!string.Equals(memberName, wrapperName, StringComparison.Ordinal)) continue;

                    // Extension methods called as receiver.Method(...) don't include 'this' in args,
                    // so adjust the index down by 1 for member-access call style.
                    int effectiveIdx = (isExtension && inv.Expression is MemberAccessExpressionSyntax)
                        ? paramIdx - 1
                        : paramIdx;

                    if (effectiveIdx < 0 || args.Count <= effectiveIdx) continue;
                    if (!IsStringLiteralWithValue(args[effectiveIdx].Expression, controlId)) continue;

                    AddFindControlRef(references, filePath, inv);
                    break;
                }
            }
        }

        return references;
    }

    private static bool IsInvocationNamed(
        InvocationExpressionSyntax inv, string name)
        => GetInvocationMemberName(inv) is { } n && string.Equals(n, name, StringComparison.Ordinal);

    private static string? GetInvocationMemberName(
        InvocationExpressionSyntax inv)
        => inv.Expression switch
        {
            MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
            IdentifierNameSyntax i => i.Identifier.Text,
            _ => null
        };

    private static bool IsStringLiteralWithValue(ExpressionSyntax expr, string expected)
    {
        if (expr is LiteralExpressionSyntax lit
            && lit.Token.IsKind(SyntaxKind.StringLiteralToken))
            return string.Equals(lit.Token.ValueText, expected, StringComparison.Ordinal);
        return false;
    }

    private static void AddFindControlRef(
        List<AspxSymbolReference> list, string filePath,
        InvocationExpressionSyntax inv)
    {
        var loc = inv.GetLocation().GetLineSpan();
        list.Add(new AspxSymbolReference(
            filePath,
            loc.StartLinePosition.Line + 1,
            loc.StartLinePosition.Character + 1,
            inv.ToString(),
            AspxCodeLocationType.FindControlCall));
    }

    private static void CollectDirectives(RootNode root, List<AspxDirectiveInfo> directives)
    {
        foreach (var directive in root.Directives)
        {
            var attrs = new Dictionary<string, string>();
            foreach (var (key, value) in directive.Attributes)
            {
                attrs[key.Value] = value.Value;
            }

            directives.Add(new AspxDirectiveInfo(
                Type: directive.DirectiveType.ToString(),
                Line: directive.Range.Start.Line + 1,
                Attributes: attrs));
        }
    }

    private static void CollectNodes(
        RootNode root,
        List<AspxControlInfo> controls,
        List<AspxExpressionInfo> expressions,
        List<AspxCodeBlockInfo> codeBlocks)
    {
        foreach (var node in root.AllChildren)
        {
            switch (node)
            {
                case ControlNode control:
                    controls.Add(new AspxControlInfo(
                        TagPrefix: control.Namespace?.Value ?? "asp",
                        TagName: control.Name.Value,
                        TypeName: control.DisplayControlType,
                        Id: control.Id,
                        Line: control.Range.Start.Line + 1));
                    break;

                case ExpressionNode expr:
                    var kind = expr.IsEncode ? AspxExpressionKind.Encoded
                        : expr.IsEval ? AspxExpressionKind.DataBinding
                        : AspxExpressionKind.Output;
                    expressions.Add(new AspxExpressionInfo(
                        Code: expr.Text.Value,
                        Kind: kind,
                        Line: expr.Range.Start.Line + 1,
                        Column: expr.Range.Start.Column + 1,
                        Range: expr.Range));
                    break;

                case StatementNode stmt:
                    codeBlocks.Add(new AspxCodeBlockInfo(
                        Code: stmt.Text.Value,
                        Line: stmt.Range.Start.Line + 1,
                        Column: stmt.Range.Start.Column + 1,
                        EndLine: stmt.Range.End.Line + 1,
                        Range: stmt.Range));
                    break;
            }
        }
    }

    /// <summary>
    /// Default tag-prefix → namespace mappings that ASP.NET implicitly registers.
    /// These are always available in traditional ASP.NET WebForms projects.
    /// </summary>
    private static readonly KeyValuePair<string, string>[] s_defaultAspNetNamespaces =
    [
        new("asp", "System.Web.UI.WebControls"),
        new("asp", "System.Web.UI"),
        new("asp", "System.Web.UI.WebControls.WebParts"),
    ];

    /// <summary>
    /// Ensures the default ASP.NET tag-prefix namespace mappings (e.g. <c>asp → System.Web.UI.WebControls</c>)
    /// are included when the compilation references <c>System.Web</c>.
    /// </summary>
    private static IEnumerable<KeyValuePair<string, string>>? EnsureDefaultAspNetNamespaces(
        Compilation compilation,
        IEnumerable<KeyValuePair<string, string>>? namespaces)
    {
        // Check for System.Web as a referenced assembly (real .NET Framework projects)
        var hasSystemWeb = compilation.ReferencedAssemblyNames
            .Any(a => string.Equals(a.Name, "System.Web", StringComparison.OrdinalIgnoreCase));

        // Also check for the namespace via type lookup (covers source-defined stubs and WebFormsCore)
        if (!hasSystemWeb)
            hasSystemWeb = compilation.GetTypeByMetadataName("System.Web.UI.Control") is not null;

        if (!hasSystemWeb)
            return namespaces;

        if (namespaces is null)
            return s_defaultAspNetNamespaces;

        return s_defaultAspNetNamespaces.Concat(namespaces);
    }

    /// <summary>
    /// Loads tag-prefix → namespace mappings from a web.config file.
    /// Reads <c>&lt;system.web&gt;&lt;pages&gt;&lt;controls&gt;&lt;add tagPrefix="..." namespace="..."/&gt;</c>.
    /// </summary>
    /// <param name="projectDirectory">Project root directory to search for web.config.</param>
    /// <returns>
    /// Tag-prefix/namespace pairs, or an empty array if no web.config is found or it
    /// contains no control registrations.
    /// </returns>
    public static ImmutableArray<KeyValuePair<string, string>> LoadWebConfigNamespaces(string projectDirectory)
    {
        var webConfigPath = Path.Combine(projectDirectory, "web.config");
        if (!File.Exists(webConfigPath))
        {
            // Try Web.config (case-sensitive file systems)
            webConfigPath = Path.Combine(projectDirectory, "Web.config");
            if (!File.Exists(webConfigPath))
                return [];
        }

        try
        {
            var webConfigText = File.ReadAllText(webConfigPath);
            var namespaces = RootNode.GetNamespaces(webConfigText);
            if (!namespaces.IsDefaultOrEmpty)
            {
                Console.Error.WriteLine(
                    $"[AspxSourceMapping] Loaded {namespaces.Length} control registration(s) from '{webConfigPath}'.");
            }
            return namespaces;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AspxSourceMapping] Error reading web.config: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Discovers and parses all ASPX-family files in a project's directory tree.
    /// Reads web.config for globally registered tag prefixes and passes them to the parser.
    /// Skips obj/bin directories.
    /// </summary>
    public static async Task<AspxProjectIndex> BuildProjectIndexAsync(
        Project project, CancellationToken cancellationToken = default,
        Compilation? compilation = null)
    {
        var projectDir = Path.GetDirectoryName(project.FilePath);
        if (projectDir is null || !Directory.Exists(projectDir))
            return new AspxProjectIndex([]);

        compilation ??= await project.GetCompilationAsync(cancellationToken);
        if (compilation is null)
            return new AspxProjectIndex([]);

        // Load globally registered tag prefixes from web.config
        var webConfigNamespaces = LoadWebConfigNamespaces(projectDir);

        // Collect all ASPX-family files up front (excluding obj/bin)
        var allFiles = new List<string>();
        foreach (var ext in s_aspxExtensions)
        {
            foreach (var file in Directory.EnumerateFiles(projectDir, $"*{ext}", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(projectDir, file);
                var firstSegment = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                if (firstSegment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                    firstSegment.Equals("bin", StringComparison.OrdinalIgnoreCase))
                    continue;
                allFiles.Add(file);
            }
        }

        // Parse all files in parallel — RootNode.Parse and Compilation are both thread-safe.
        var results = new System.Collections.Concurrent.ConcurrentBag<AspxParseResult>();
        await Parallel.ForEachAsync(
            allFiles,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = cancellationToken
            },
            async (file, ct) =>
            {
                try
                {
                    var text = await File.ReadAllTextAsync(file, ct);
                    var result = Parse(file, text, compilation,
                        namespaces: webConfigNamespaces.IsDefaultOrEmpty ? null : webConfigNamespaces,
                        rootDirectory: projectDir);
                    results.Add(result);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AspxIndex] Error parsing '{file}': {ex.Message}");
                }
            });

        return new AspxProjectIndex([.. results]);
    }

    /// <summary>
    /// Searches indexed ASPX files for references to a symbol by name.
    /// Returns locations in expressions and code blocks that contain the symbol name.
    /// </summary>
    public static List<AspxSymbolReference> FindSymbolReferences(
        AspxProjectIndex index, string symbolName)
    {
        var references = new List<AspxSymbolReference>();

        foreach (var parseResult in index.Files)
        {
            foreach (var expr in parseResult.Expressions)
            {
                if (expr.Code.Contains(symbolName, StringComparison.Ordinal))
                {
                    references.Add(new AspxSymbolReference(
                        parseResult.FilePath, expr.Line, expr.Column,
                        expr.Code.Trim(), AspxCodeLocationType.Expression));
                }
            }

            foreach (var block in parseResult.CodeBlocks)
            {
                if (block.Code.Contains(symbolName, StringComparison.Ordinal))
                {
                    references.Add(new AspxSymbolReference(
                        parseResult.FilePath, block.Line, block.Column,
                        block.Code.Trim(), AspxCodeLocationType.CodeBlock));
                }
            }
        }

        return references;
    }
}

/// <summary>Result of parsing an ASPX file.</summary>
internal record AspxParseResult(
    string FilePath,
    List<AspxDirectiveInfo> Directives,
    List<AspxControlInfo> Controls,
    List<AspxExpressionInfo> Expressions,
    List<AspxCodeBlockInfo> CodeBlocks,
    List<string> Errors,
    ImmutableArray<ReportedDiagnostic> RawDiagnostics,
    RootNode? ParseTree);

/// <summary>A parsed <%@ ... %> directive.</summary>
internal record AspxDirectiveInfo(string Type, int Line, Dictionary<string, string> Attributes);

/// <summary>A parsed server control (e.g., asp:Button).</summary>
internal record AspxControlInfo(string TagPrefix, string TagName, string TypeName, string? Id, int Line);

/// <summary>A parsed inline expression (<%= %>, <%: %>, <%# %>).</summary>
internal record AspxExpressionInfo(string Code, AspxExpressionKind Kind, int Line, int Column, TokenRange Range);

/// <summary>A parsed code block (<% ... %>).</summary>
internal record AspxCodeBlockInfo(string Code, int Line, int Column, int EndLine, TokenRange Range);

/// <summary>A mapped code location within an ASPX file.</summary>
internal record AspxCodeLocation(string Code, int Line, int Column, AspxCodeLocationType Type);

internal enum AspxExpressionKind { Output, Encoded, DataBinding }
internal enum AspxCodeLocationType { Expression, CodeBlock, FindControlCall }

/// <summary>All parsed ASPX files in a project.</summary>
internal record AspxProjectIndex(List<AspxParseResult> Files);

/// <summary>A reference to a symbol found in an ASPX file.</summary>
internal record AspxSymbolReference(
    string FilePath, int Line, int Column,
    string CodeSnippet, AspxCodeLocationType LocationType);
