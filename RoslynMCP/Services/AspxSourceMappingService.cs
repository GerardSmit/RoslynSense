using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
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
        var rootNode = RootNode.Parse(
            out var diagnostics,
            compilation,
            fullPath: filePath,
            text: text,
            namespaces: namespaces,
            rootDirectory: rootDirectory,
            generateHash: false);

        if (rootNode is null)
            return new AspxParseResult(filePath, [], [], [], [], [], diagnostics);

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

        return new AspxParseResult(filePath, directives, controls, expressions, codeBlocks, errors, diagnostics);
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
        Project project, CancellationToken cancellationToken = default)
    {
        var projectDir = Path.GetDirectoryName(project.FilePath);
        if (projectDir is null || !Directory.Exists(projectDir))
            return new AspxProjectIndex([]);

        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation is null)
            return new AspxProjectIndex([]);

        // Load globally registered tag prefixes from web.config
        var webConfigNamespaces = LoadWebConfigNamespaces(projectDir);

        var results = new List<AspxParseResult>();

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

                try
                {
                    var text = await File.ReadAllTextAsync(file, cancellationToken);
                    var result = Parse(file, text, compilation,
                        namespaces: webConfigNamespaces.IsDefaultOrEmpty ? null : webConfigNamespaces,
                        rootDirectory: projectDir);
                    results.Add(result);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AspxIndex] Error parsing '{file}': {ex.Message}");
                }
            }
        }

        return new AspxProjectIndex(results);
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
    ImmutableArray<ReportedDiagnostic> RawDiagnostics);

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
internal enum AspxCodeLocationType { Expression, CodeBlock }

/// <summary>All parsed ASPX files in a project.</summary>
internal record AspxProjectIndex(List<AspxParseResult> Files);

/// <summary>A reference to a symbol found in an ASPX file.</summary>
internal record AspxSymbolReference(
    string FilePath, int Line, int Column,
    string CodeSnippet, AspxCodeLocationType LocationType);
