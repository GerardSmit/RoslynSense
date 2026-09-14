using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMCP.Debugger;

/// <summary>Recognizes paths that can be read directly, without compiling or running code.</summary>
internal static class DebugExpressionPath
{
    internal sealed record Segment(string Name, List<object> Indexes, bool IsCall = false);

    internal static bool TryParse(string expression, out List<Segment> segments)
    {
        segments = [];
        var syntax = SyntaxFactory.ParseExpression(expression);
        return !syntax.ContainsDiagnostics && Append(syntax, segments);
    }

    private static bool Append(ExpressionSyntax expression, List<Segment> into)
    {
        switch (expression)
        {
            case IdentifierNameSyntax name:
                into.Add(new(name.Identifier.ValueText, []));
                return true;
            case ThisExpressionSyntax:
                into.Add(new("this", []));
                return true;
            case ParenthesizedExpressionSyntax parentheses:
                return Append(parentheses.Expression, into);
            case MemberAccessExpressionSyntax { Name: IdentifierNameSyntax name } member:
                if (!Append(member.Expression, into)) return false;
                into.Add(new(name.Identifier.ValueText, []));
                return true;
            case InvocationExpressionSyntax call when call.ArgumentList.Arguments.Count == 0:
                if (!Append(call.Expression, into)) return false;
                into[^1] = into[^1] with { IsCall = true };
                return true;
            case ElementAccessExpressionSyntax index:
                if (!Append(index.Expression, into)) return false;
                var arguments = index.ArgumentList.Arguments;
                if (arguments.Count == 1 && arguments[0].Expression is LiteralExpressionSyntax { Token.Value: string key })
                {
                    into[^1].Indexes.Add(key);
                    return true;
                }
                var numbers = new List<int>();
                foreach (var argument in arguments)
                {
                    if (argument.Expression is LiteralExpressionSyntax { Token.Value: int number }) numbers.Add(number);
                    else if (argument.Expression is PrefixUnaryExpressionSyntax unary && unary.IsKind(SyntaxKind.UnaryMinusExpression)
                             && unary.Operand is LiteralExpressionSyntax { Token.Value: int magnitude }) numbers.Add(-magnitude);
                    else return false;
                }
                if (numbers.Count == 0) return false;
                into[^1].Indexes.Add(numbers.ToArray());
                return true;
            default:
                return false;
        }
    }
}
