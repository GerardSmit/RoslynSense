using System.Text;
using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Languages.WebForms.Lsp;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Tools;

namespace RoslynMCP.Languages.WebForms.Tools;

/// <summary>
/// Produces a structured outline for ASPX/ASCX files: the directives, the control tree, and the
/// inline code the page carries.
/// </summary>
internal class AspxOutline : IOutlineHandler
{
    public bool CanHandle(string filePath) => AspxDocumentService.IsAspxFile(filePath);

    public Task<string> GetOutlineAsync(string filePath, CancellationToken cancellationToken) =>
        FormatAsync(filePath, cancellationToken);

    /// <summary>
    /// The markdown outline of one markup file.
    /// </summary>
    /// <remarks>
    /// The control tree comes from the same <c>documentSymbol</c> walk the editor renders, rather
    /// than a second flat listing built for this tool: that is what makes nesting and
    /// <c>&lt;ItemTemplate&gt;</c> contents show up here at all. Directives and inline code are
    /// not part of that walk — an editor outline has no use for them — so they are read off the
    /// parse the walk itself resolved through, which costs nothing extra because it is memoized.
    /// </remarks>
    internal static async Task<string> FormatAsync(string filePath, CancellationToken ct)
    {
        var document = await AspxDocumentService.GetAsync(filePath, ct);
        if (document is null)
        {
            return $"Error: Couldn't load '{Path.GetFileName(filePath)}'. " +
                   "The file must exist and belong to a project that produces a compilation.";
        }

        var symbols = await AspxLanguageHandler.DocumentSymbolAsync(
            new DocumentSymbolParams(new TextDocumentIdentifier(LspConverters.PathToUri(filePath))), ct);

        var parse = document.Parse;
        var sb = new StringBuilder();
        sb.AppendLine($"# ASPX File: {Path.GetFileName(document.FilePath)}");
        sb.AppendLine();

        AppendDirectives(sb, parse.Directives);
        AppendControls(sb, symbols);
        AppendExpressions(sb, parse.Expressions);
        AppendCodeBlocks(sb, parse.CodeBlocks);
        AppendErrors(sb, parse.Errors);

        return sb.ToString();
    }

    private static void AppendDirectives(StringBuilder sb, List<AspxDirectiveInfo> directives)
    {
        if (directives.Count == 0)
            return;

        sb.AppendLine("## Directives");
        foreach (var directive in directives)
        {
            sb.AppendLine($"- **{directive.Type}** at line {directive.Line}");
            foreach (var (key, value) in directive.Attributes)
                sb.AppendLine($"  - {key}=\"{value}\"");
        }
        sb.AppendLine();
    }

    private static void AppendControls(StringBuilder sb, DocumentSymbol[] symbols)
    {
        // The walk emits the directives as modules; they already have their own section above,
        // with the attributes this one has no room for.
        var controls = symbols.Where(s => s.Kind != LspSymbolKind.Module).ToArray();
        if (controls.Length == 0)
            return;

        sb.AppendLine("## Server Controls");
        foreach (var control in controls)
            AppendControl(sb, control, depth: 0);
        sb.AppendLine();
    }

    private static void AppendControl(StringBuilder sb, DocumentSymbol symbol, int depth)
    {
        string indent = new(' ', depth * 2);
        string detail = symbol.Detail is { Length: > 0 } d ? $" ({d})" : "";
        sb.AppendLine($"{indent}- **{symbol.Name}**{detail} at line {symbol.Range.Start.Line + 1}");

        foreach (var child in symbol.Children)
            AppendControl(sb, child, depth + 1);
    }

    private static void AppendExpressions(StringBuilder sb, List<AspxExpressionInfo> expressions)
    {
        if (expressions.Count == 0)
            return;

        sb.AppendLine("## Inline Expressions");
        foreach (var expr in expressions)
        {
            string opener = expr.Kind switch
            {
                AspxExpressionKind.Encoded => "<%:",
                AspxExpressionKind.DataBinding => "<%#",
                _ => "<%=",
            };
            sb.AppendLine($"- `{opener} {Truncate(expr.Code)} %>` at line {expr.Line}");
        }
        sb.AppendLine();
    }

    private static void AppendCodeBlocks(StringBuilder sb, List<AspxCodeBlockInfo> codeBlocks)
    {
        if (codeBlocks.Count == 0)
            return;

        sb.AppendLine("## Code Blocks");
        foreach (var block in codeBlocks)
            sb.AppendLine($"- `<% {Truncate(block.Code.Split('\n')[0].Trim())} %>` at line {block.Line}");
        sb.AppendLine();
    }

    private static void AppendErrors(StringBuilder sb, List<string> errors)
    {
        if (errors.Count == 0)
            return;

        sb.AppendLine("## Parse Errors");
        foreach (var error in errors)
            sb.AppendLine($"- {error}");
    }

    private static string Truncate(string text) =>
        text.Length > 60 ? text[..57] + "..." : text;
}
