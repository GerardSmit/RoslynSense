using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>roslynSense/onAutoInsert (custom): after the user types <c>///</c> on its own
/// line, returns an XML doc skeleton (summary + typeparam/param/returns from the following
/// member's signature) and the caret position inside the summary.</summary>
internal static class OnAutoInsertHandler
{
    public static async Task<OnAutoInsertResult?> OnAutoInsertAsync(
        OnAutoInsertParams p, CancellationToken ct)
    {
        var resolved = await HandlerHelpers.ResolveAsync(p.TextDocument, p.Position, ct);
        if (resolved is not var (document, text, offset))
            return null;

        // Trigger only when the caret sits right after "///" on an otherwise empty line.
        var line = text.Lines[p.Position.Line];
        string beforeCaret = text.ToString(Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(
            line.Start, Math.Min(offset, line.End)));
        if (beforeCaret.TrimStart() != "///"
            || text.ToString(Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(
                Math.Min(offset, line.End), line.End)).Trim().Length > 0)
            return null;

        // Already documented? (another "///" line directly above or below)
        if (IsDocCommentLine(text, p.Position.Line - 1) || IsDocCommentLine(text, p.Position.Line + 1))
            return null;

        var root = await document.GetSyntaxRootAsync(ct);
        var member = root?.FindToken(Math.Min(offset, Math.Max(0, text.Length - 1)))
            .Parent?.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault();
        // The token lookup can land on the enclosing type (e.g. "///" on a blank line whose
        // trivia binds to the type) — the comment is really for the next member after the caret.
        if (member is TypeDeclarationSyntax enclosing && enclosing.SpanStart < offset)
            member = enclosing.Members.FirstOrDefault(m => m.SpanStart >= offset) ?? member;
        if (member is null)
            return null;

        string indent = beforeCaret[..beforeCaret.IndexOf('/')];
        string eol = DetectEol(text);
        var sb = new StringBuilder();
        sb.Append(" <summary>").Append(eol).Append(indent).Append("/// ").Append(eol)
          .Append(indent).Append("/// </summary>");

        foreach (var typeParam in TypeParameters(member))
            sb.Append(eol).Append(indent).Append($"/// <typeparam name=\"{typeParam}\"></typeparam>");
        foreach (var param in Parameters(member))
            sb.Append(eol).Append(indent).Append($"/// <param name=\"{param}\"></param>");
        if (HasReturnValue(member))
            sb.Append(eol).Append(indent).Append("/// <returns></returns>");

        return new OnAutoInsertResult(
            new TextEdit(new Protocol.Range(p.Position, p.Position), sb.ToString()),
            new Position(p.Position.Line + 1, indent.Length + "/// ".Length));
    }

    private static string DetectEol(Microsoft.CodeAnalysis.Text.SourceText text)
    {
        var first = text.Lines.Count > 0 ? text.Lines[0] : default;
        return first.EndIncludingLineBreak > first.End
            ? text.ToString(Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(first.End, first.EndIncludingLineBreak))
            : "\r\n";
    }

    private static bool IsDocCommentLine(Microsoft.CodeAnalysis.Text.SourceText text, int line) =>
        line >= 0 && line < text.Lines.Count
        && text.ToString(text.Lines[line].Span).TrimStart().StartsWith("///", StringComparison.Ordinal);

    private static IEnumerable<string> TypeParameters(MemberDeclarationSyntax member) =>
        (member switch
        {
            MethodDeclarationSyntax m => m.TypeParameterList,
            TypeDeclarationSyntax t => t.TypeParameterList,
            DelegateDeclarationSyntax d => d.TypeParameterList,
            _ => null,
        })?.Parameters.Select(p => p.Identifier.Text) ?? [];

    private static IEnumerable<string> Parameters(MemberDeclarationSyntax member) =>
        (member switch
        {
            BaseMethodDeclarationSyntax m => m.ParameterList,
            DelegateDeclarationSyntax d => d.ParameterList,
            IndexerDeclarationSyntax i => (BaseParameterListSyntax)i.ParameterList,
            _ => null,
        })?.Parameters.Select(p => p.Identifier.Text) ?? [];

    private static bool HasReturnValue(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax m => ReturnsValue(m.ReturnType),
        IndexerDeclarationSyntax => true,
        DelegateDeclarationSyntax d => ReturnsValue(d.ReturnType),
        _ => false,
    };

    private static bool ReturnsValue(TypeSyntax returnType) => returnType switch
    {
        PredefinedTypeSyntax { Keyword.Text: "void" } => false,
        IdentifierNameSyntax { Identifier.Text: "Task" or "ValueTask" } => false, // no value to document
        QualifiedNameSyntax q => ReturnsValue(q.Right),
        _ => true,
    };
}
