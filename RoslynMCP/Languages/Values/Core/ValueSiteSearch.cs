using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services.Symbols;

namespace RoslynMCP.Languages.Values.Core;

/// <summary>
/// Which string literals a set of bindings claims.
/// </summary>
/// <remarks>
/// <para>
/// Two shapes, and they are not symmetrical. An <i>argument</i> is easy: the literal is written
/// where the value goes, and one look at the call answers it. A <i>comparison</i> is the harder
/// half and the more useful one, because the literal is nowhere near the member it is about —
/// <c>status?.Code is "rejected" or "waiting"</c> puts two of them inside a pattern, a
/// <c>switch</c> puts them in labels a screen away from the governing expression, and every one of
/// them is a plain <c>string</c> as far as the compiler is concerned.
/// </para>
/// <para>
/// The order of the checks is the same discipline <c>ResourceKeySearch</c> follows and for the same
/// reason: this runs against every string literal in the file on every diagnostics pass. Syntax
/// answers first and rejects almost everything; the simple name of the member is read out of the
/// tree and compared against the configured names; only what survives both is bound.
/// </para>
/// </remarks>
internal static class ValueSiteSearch
{
    /// <summary>The name an indexer binds under — <c>codes["x"]</c>.</summary>
    private const string IndexerName = "Item";

    /// <summary>The one method whose arguments are a comparison rather than a call.</summary>
    private const string EqualsName = "Equals";

    /// <summary>
    /// The binding this literal belongs to, or null.
    /// </summary>
    public static ValueSite? Match(
        ValueSettings settings, SemanticModel model, SyntaxToken token, CancellationToken ct)
    {
        if (settings.Bindings.IsDefaultOrEmpty
            || !token.IsKind(SyntaxKind.StringLiteralToken)
            || token.Parent is not LiteralExpressionSyntax literal)
        {
            return null;
        }

        return Argument(settings, model, literal, token, ct)
            ?? Compared(settings, model, literal, token, ct);
    }

    // ---- The literal is an argument ----------------------------------------------------------------

    private static ValueSite? Argument(
        ValueSettings settings, SemanticModel model, LiteralExpressionSyntax literal,
        SyntaxToken token, CancellationToken ct)
    {
        if (literal.Parent is not ArgumentSyntax argument
            || argument.Parent is not BaseArgumentListSyntax list)
        {
            return null;
        }

        ExpressionSyntax call;
        string invoked;

        switch (list.Parent)
        {
            case InvocationExpressionSyntax invocation when list is ArgumentListSyntax:
                if (InvokedName(invocation.Expression) is not { } name)
                    return null;

                call = invocation;
                invoked = name;
                break;

            case ElementAccessExpressionSyntax access when list is BracketedArgumentListSyntax:
                call = access;
                invoked = IndexerName;
                break;

            default:
                return null;
        }

        var candidates = Named(settings, invoked, wantsIndex: true);
        if (candidates.Count == 0)
            return null;

        if (model.GetSymbolInfo(call, ct).Symbol is not { } member)
            return null;

        var definition = member.OriginalDefinition;

        if (Ordinal(definition, argument, list.Arguments) is not { } ordinal)
            return null;

        foreach (var binding in candidates)
        {
            if (binding.ValueIndex == ordinal
                && Binds(binding, definition)
                && settings.Set(binding.SetId) is { } set)
            {
                return new ValueSite(
                    binding, set, ValueSiteKind.Argument, Content(token), token.ValueText,
                    Subject(definition));
            }
        }

        return null;
    }

    /// <summary>
    /// Which parameter the argument fills.
    /// </summary>
    /// <remarks>
    /// Not the argument's place in the list: a named argument can be written anywhere, and
    /// <c>Get(root: "x", code: "y")</c> would otherwise be read as a value in slot 0. An argument
    /// past the last parameter fills a <c>params</c> one, which is the only way the count can
    /// legitimately disagree.
    /// </remarks>
    private static int? Ordinal(
        ISymbol member, ArgumentSyntax argument, SeparatedSyntaxList<ArgumentSyntax> arguments)
    {
        var parameters = MemberSignature.Parameters(member);

        if (argument.NameColon is { Name.Identifier.ValueText: var named })
        {
            foreach (var parameter in parameters)
            {
                if (parameter.Name.Equals(named, StringComparison.Ordinal))
                    return parameter.Ordinal;
            }

            return null;
        }

        int index = arguments.IndexOf(argument);

        if (index < parameters.Length)
            return index;

        return parameters is [.., { IsParams: true } last] ? last.Ordinal : null;
    }

    // ---- The literal is compared against a member ---------------------------------------------------

    private static ValueSite? Compared(
        ValueSettings settings, SemanticModel model, LiteralExpressionSyntax literal,
        SyntaxToken token, CancellationToken ct)
    {
        if (Governing(literal) is not { } governed)
            return null;

        var target = Unwrap(governed);

        if (SimpleName(target) is not { } name)
            return null;

        var candidates = Named(settings, name, wantsIndex: false);
        if (candidates.Count == 0)
            return null;

        if (model.GetSymbolInfo(target, ct).Symbol is not { } member)
            return null;

        var definition = member.OriginalDefinition;

        foreach (var binding in candidates)
        {
            if (Binds(binding, definition) && settings.Set(binding.SetId) is { } set)
            {
                return new ValueSite(
                    binding, set, ValueSiteKind.Compared, Content(token), token.ValueText,
                    Subject(definition));
            }
        }

        return null;
    }

    /// <summary>
    /// The expression this literal is being measured against.
    /// </summary>
    /// <remarks>
    /// Every shape C# has for "is this string that string", which is more of them than it looks:
    /// two operators, four kinds of pattern position, a label, an assignment and one method. What
    /// they have in common is that the literal is a leaf and the thing it is about is somewhere
    /// above it — sometimes directly, sometimes past a chain of <c>or</c> patterns and a switch
    /// arm.
    /// </remarks>
    private static ExpressionSyntax? Governing(ExpressionSyntax literal) => literal.Parent switch
    {
        BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.EqualsExpression)
            || binary.IsKind(SyntaxKind.NotEqualsExpression) =>
            binary.Left == literal ? binary.Right : binary.Left,

        // `code = "x"`, and the object-initializer form, which parses as the same node.
        AssignmentExpressionSyntax assignment
            when assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                && assignment.Right == literal => assignment.Left,

        CaseSwitchLabelSyntax { Parent: SwitchSectionSyntax { Parent: SwitchStatementSyntax switched } } =>
            switched.Expression,

        ConstantPatternSyntax pattern => FromPattern(pattern),

        ArgumentSyntax argument => FromEqualsCall(argument, literal),

        _ => null,
    };

    /// <summary>
    /// What a pattern containing this constant is matching against.
    /// </summary>
    /// <remarks>
    /// The climb is what makes <c>is "a" or "b"</c> work: the constant's parent is the
    /// <c>or</c>, and only above that is there anything naming a member. <c>not</c> and
    /// parentheses nest the same way.
    /// </remarks>
    private static ExpressionSyntax? FromPattern(PatternSyntax pattern)
    {
        SyntaxNode node = pattern;

        while (node.Parent is BinaryPatternSyntax or ParenthesizedPatternSyntax or UnaryPatternSyntax)
            node = node.Parent;

        return node.Parent switch
        {
            IsPatternExpressionSyntax expression => expression.Expression,

            SwitchExpressionArmSyntax { Parent: SwitchExpressionSyntax switched } =>
                switched.GoverningExpression,

            CasePatternSwitchLabelSyntax
            {
                Parent: SwitchSectionSyntax { Parent: SwitchStatementSyntax switched },
            } => switched.Expression,

            // `order is { Code: "rejected" }` — the name in the subpattern binds to the member
            // directly, which is a shorter road than any of the others.
            SubpatternSyntax { NameColon.Name: { } named } => named,

            _ => null,
        };
    }

    /// <summary>
    /// The member behind an <c>Equals</c> call, in either of its two shapes.
    /// </summary>
    /// <remarks>
    /// Worth the special case because a codebase that compares codes case-insensitively writes
    /// nothing else: <c>code.Equals("rejected", StringComparison.OrdinalIgnoreCase)</c> is the
    /// idiomatic form and it hides the member behind a receiver.
    /// </remarks>
    private static ExpressionSyntax? FromEqualsCall(ArgumentSyntax argument, ExpressionSyntax literal)
    {
        if (argument.Parent is not ArgumentListSyntax list
            || list.Parent is not InvocationExpressionSyntax invocation
            || InvokedName(invocation.Expression) is not EqualsName)
        {
            return null;
        }

        // `x.Code.Equals("a")`: the receiver is the member. `string.Equals(x.Code, "a")`: the
        // receiver is the type, so the member is the other argument.
        if (invocation.Expression is MemberAccessExpressionSyntax { Expression: { } receiver }
            && !IsStringType(receiver))
        {
            return receiver;
        }

        foreach (var other in list.Arguments)
        {
            if (other.Expression != literal && other.Expression is not LiteralExpressionSyntax)
                return other.Expression;
        }

        return null;
    }

    private static bool IsStringType(ExpressionSyntax expression) =>
        expression is PredefinedTypeSyntax
        || expression is IdentifierNameSyntax { Identifier.ValueText: "String" };

    /// <summary>
    /// Peels off everything that does not change which member is named.
    /// </summary>
    /// <remarks>
    /// <c>?.</c> above all: <c>status?.Code</c> is a conditional access whose own symbol is
    /// nothing, and the member binding inside it is what actually resolves.
    /// </remarks>
    private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
    {
        while (true)
        {
            switch (expression)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    expression = parenthesized.Expression;
                    continue;

                case ConditionalAccessExpressionSyntax conditional:
                    expression = conditional.WhenNotNull;
                    continue;

                case PostfixUnaryExpressionSyntax suppressed
                    when suppressed.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                    expression = suppressed.Operand;
                    continue;

                case CastExpressionSyntax cast:
                    expression = cast.Expression;
                    continue;

                default:
                    return expression;
            }
        }
    }

    // ---- Shared ------------------------------------------------------------------------------------

    /// <summary>The bindings that could name this member, from the name alone.</summary>
    private static List<ValueBinding> Named(ValueSettings settings, string name, bool wantsIndex)
    {
        var found = new List<ValueBinding>();

        foreach (var binding in settings.Bindings)
        {
            if (binding.MemberName.Equals(name, StringComparison.Ordinal)
                && binding.ValueIndex.HasValue == wantsIndex)
            {
                found.Add(binding);
            }
        }

        return found;
    }

    private static bool Binds(ValueBinding binding, ISymbol member)
    {
        if (binding.ContainingType is { Length: > 0 } declaring
            && (member.ContainingType is not { } containing
                || !MemberSignature.DeclaredBy(containing, declaring)))
        {
            return false;
        }

        return binding.ParameterTypes is not { } expected
            || MemberSignature.Matches(member, expected);
    }

    /// <summary>The simple name an invocation calls, from syntax alone.</summary>
    private static string? InvokedName(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        SimpleNameSyntax name => name.Identifier.ValueText,
        _ => null,
    };

    /// <summary>The simple name of whatever a comparison is about, from syntax alone.</summary>
    private static string? SimpleName(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        InvocationExpressionSyntax invocation => InvokedName(invocation.Expression),
        ElementAccessExpressionSyntax => IndexerName,
        SimpleNameSyntax name => name.Identifier.ValueText,
        _ => null,
    };

    /// <summary>The member as it should be named to a person: <c>Type.Member</c>.</summary>
    private static string Subject(ISymbol member) =>
        member.ContainingType is { } containing
            ? $"{containing.Name}.{member.Name}"
            : member.Name;

    /// <summary>
    /// The literal without its quotes, which is what the value actually is. Falls back to the whole
    /// token for a shape the prefix walk does not recognise.
    /// </summary>
    private static TextSpan Content(SyntaxToken token)
    {
        string text = token.Text;
        int start = 0;

        while (start < text.Length && (text[start] == '@' || text[start] == '$'))
            start++;

        int quotes = 0;
        while (start + quotes < text.Length && text[start + quotes] == '"')
            quotes++;

        return quotes == 0 || text.Length < start + (2 * quotes)
            ? token.Span
            : TextSpan.FromBounds(token.SpanStart + start + quotes, token.Span.End - quotes);
    }
}
