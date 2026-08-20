using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services.Symbols;

namespace RoslynMCP.Languages.Formatting.Core;

/// <summary>What a claimed token holds.</summary>
internal enum FormatTextKind
{
    /// <summary>A whole composite string — <c>"{0:dd-MM-yyyy} of {1}"</c>.</summary>
    Composite,

    /// <summary>One specifier, with no holes around it — the <c>yyyyMMdd</c> of
    /// <c>$"{DateTime.Now:yyyyMMdd}"</c> or of <c>value.ToString("yyyyMMdd")</c>.</summary>
    Specifier,
}

/// <summary>One value a composite string can render.</summary>
/// <param name="Name">The expression as written, which is what a hover shows.</param>
internal readonly record struct FormatValue(string Name, ITypeSymbol? Type, TextSpan Span);

/// <summary>
/// A string that turned out to be a format string, and what decides how to read it.
/// </summary>
/// <param name="ValuesAreComplete">False when the values could not be enumerated — a
/// <c>params object[]</c> passed as an array, a named argument, a call that did not bind. A hole
/// that reaches past the end of an incomplete list says nothing rather than saying it is unbound.
/// </param>
/// <param name="Subject">What to call this in a message — <c>String.Format</c>,
/// <c>DateTime.ToString</c>.</param>
internal sealed record FormatSite(
    FormatTextKind Kind,
    ImmutableArray<FormatValue> Values,
    bool ValuesAreComplete,
    string Subject)
{
    /// <summary>
    /// The parameter name a composite format string is passed under.
    /// </summary>
    /// <remarks>
    /// One name rather than a list, unlike the logging pack's four: this is the BCL's own
    /// convention and every method that takes a composite string honours it —
    /// <c>String.Format</c>, <c>StringBuilder.AppendFormat</c>, <c>Console.WriteLine</c>,
    /// <c>TextWriter.Write</c>, <c>Debug.WriteLine</c>, <c>CompositeFormat.Parse</c>.
    /// </remarks>
    private const string FormatParameter = "format";

    /// <summary>
    /// The methods whose <c>format</c> parameter holds a lone specifier rather than a composite
    /// string, and which therefore cannot be recognised by a brace.
    /// </summary>
    private static readonly HashSet<string> s_specifierMethods = new(StringComparer.Ordinal)
    {
        "ToString", "TryFormat", "ParseExact", "TryParseExact",
    };

    /// <summary>
    /// The cheap gate, run against every candidate token in a document before anything binds.
    /// </summary>
    /// <remarks>
    /// Syntax only, and it has to be tight: this runs per keystroke over every string literal in
    /// the file, and the step after it binds the enclosing call. A composite string announces
    /// itself with a brace; a lone specifier does not, so the short list of methods that take one
    /// is named instead. Everything else — the overwhelming majority, ordinary prose passed to
    /// ordinary methods — is rejected here for the cost of a substring search.
    /// </remarks>
    public static bool CouldBeFormat(SyntaxToken token)
    {
        if (token.IsKind(SyntaxKind.InterpolatedStringTextToken))
            return token.Parent is InterpolationFormatClauseSyntax;

        if (!token.IsKind(SyntaxKind.StringLiteralToken)
            || token.Parent is not ExpressionSyntax literal
            || literal.Parent is not ArgumentSyntax
            {
                Parent.Parent: InvocationExpressionSyntax invocation,
            })
        {
            return false;
        }

        return token.Text.Contains('{') || s_specifierMethods.Contains(Called(invocation));
    }

    /// <summary>The simple name a call invokes, without binding it.</summary>
    private static string Called(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        _ => string.Empty,
    };

    /// <summary>What reads this string as a format, or null when nothing does.</summary>
    public static FormatSite? Resolve(SemanticModel model, SyntaxToken token, CancellationToken ct)
    {
        if (token.Parent is InterpolationFormatClauseSyntax { Parent: InterpolationSyntax interpolation })
            return FromInterpolation(model, interpolation, ct);

        if (!CouldBeFormat(token)
            || token.Parent is not ExpressionSyntax literal
            || literal.Parent is not ArgumentSyntax argument
            || argument.Parent is not ArgumentListSyntax list
            || list.Parent is not InvocationExpressionSyntax invocation)
        {
            return null;
        }

        return FromCall(model, invocation, list, argument, ct);
    }

    // ---- `$"{DateTime.Now:yyyyMMdd}"` ---------------------------------------------------------

    /// <summary>
    /// The format clause of an interpolation, whose value is the expression right beside it.
    /// </summary>
    /// <remarks>
    /// The one case where the value costs nothing to find, which is also why colouring is most
    /// worth having here: the compiler hands the clause to the value's own <c>ToString</c> without
    /// looking at it, so <c>$"{total:dd}"</c> on a decimal is a compiling, running, wrong program.
    /// </remarks>
    private static FormatSite FromInterpolation(
        SemanticModel model, InterpolationSyntax interpolation, CancellationToken ct)
    {
        var expression = interpolation.Expression;
        string name = expression.ToString();

        return new FormatSite(
            FormatTextKind.Specifier,
            [new FormatValue(name, model.GetTypeInfo(expression, ct).Type, expression.Span)],
            ValuesAreComplete: true,
            name);
    }

    // ---- `string.Format("…", a, b)` and `value.ToString("…")` ---------------------------------

    private static FormatSite? FromCall(
        SemanticModel model, InvocationExpressionSyntax invocation, ArgumentListSyntax list,
        ArgumentSyntax argument, CancellationToken ct)
    {
        if (model.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method)
            return null;

        int index = ParameterIndex(method, list, argument);
        if (index < 0 || index >= method.Parameters.Length)
            return null;

        var parameter = method.Parameters[index];
        if (!MemberSignature.Named(parameter.Type, "string")
            || !parameter.Name.Equals(FormatParameter, StringComparison.Ordinal))
        {
            return null;
        }

        string subject = $"{method.ContainingType.Name}.{method.Name}";

        // `value.ToString("yyyyMMdd")` — one specifier, and the receiver is what it formats.
        if (method.Name is "ToString" or "TryFormat")
            return new FormatSite(FormatTextKind.Specifier, Receiver(model, invocation, ct), true, subject);

        // `DateTime.ParseExact(text, "yyyyMMdd", provider)` — the same grammar read backwards, and
        // the value it describes has not been produced yet.
        if (method.Name is "ParseExact" or "TryParseExact")
            return new FormatSite(FormatTextKind.Specifier, [], ValuesAreComplete: true, subject);

        // Everything else has to prove it is composite rather than merely have a parameter called
        // `format`: what makes a string composite is that the call takes values to put in it.
        if (!TakesValues(method, index))
            return null;

        var (values, complete) = ArgumentValues(model, list, argument, ct);
        return new FormatSite(FormatTextKind.Composite, values, complete, subject);
    }

    /// <summary>
    /// The receiver of an instance call, as the single value the specifier formats.
    /// </summary>
    /// <remarks>
    /// Empty for a static <c>ToString</c> — <c>XmlConvert.ToString(value, format)</c> — where the
    /// value is an argument and which of them it is depends on the overload. The specifier is still
    /// read and coloured; only the sentence naming the value is dropped.
    /// </remarks>
    private static ImmutableArray<FormatValue> Receiver(
        SemanticModel model, InvocationExpressionSyntax invocation, CancellationToken ct) =>
        invocation.Expression is MemberAccessExpressionSyntax member
        && model.GetTypeInfo(member.Expression, ct).Type is { } type
            ? [new FormatValue(member.Expression.ToString(), type, member.Expression.Span)]
            : [];

    /// <summary>
    /// Whether the method takes values for the holes, which is what makes its string composite.
    /// </summary>
    /// <remarks>
    /// <c>CompositeFormat.Parse(string format)</c> takes none and is still composite, and it is the
    /// one method that has to be named rather than recognised — every other one is recognised by
    /// the <c>object</c> or <c>object[]</c> parameter that follows the format.
    /// </remarks>
    private static bool TakesValues(IMethodSymbol method, int formatIndex)
    {
        if (method.Name == "Parse" && method.ContainingType.Name == "CompositeFormat")
            return true;

        for (int i = formatIndex + 1; i < method.Parameters.Length; i++)
        {
            var type = method.Parameters[i].Type;

            if (type.SpecialType == SpecialType.System_Object
                || (type is IArrayTypeSymbol array
                    && array.ElementType.SpecialType == SpecialType.System_Object))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Everything the call passes after the format string, in order.
    /// </summary>
    /// <remarks>
    /// Reads the syntax rather than the bound parameters, because what a hole's index counts is the
    /// argument list as written: a <c>params</c> parameter is one symbol and any number of
    /// arguments.
    /// </remarks>
    private static (ImmutableArray<FormatValue> Values, bool Complete) ArgumentValues(
        SemanticModel model, ArgumentListSyntax list, ArgumentSyntax format, CancellationToken ct)
    {
        var arguments = list.Arguments;

        // A named argument anywhere means position is no longer the order they are written in.
        if (arguments.Any(a => a.NameColon is not null))
            return ([], false);

        int start = arguments.IndexOf(format);
        var values = ImmutableArray.CreateBuilder<FormatValue>();

        for (int i = start + 1; i < arguments.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var expression = arguments[i].Expression;
            var type = model.GetTypeInfo(expression, ct).Type;

            // `new object[] { a, b }` handed to a params parameter is one argument standing for
            // any number of values, so the list stops being countable here.
            if (i == start + 1
                && arguments.Count == start + 2
                && type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Object })
            {
                return ([], false);
            }

            values.Add(new FormatValue(expression.ToString(), type, expression.Span));
        }

        return (values.ToImmutable(), true);
    }

    /// <summary>
    /// Which parameter an argument binds to, honouring <c>name:</c> and <c>params</c>.
    /// </summary>
    private static int ParameterIndex(
        IMethodSymbol method, ArgumentListSyntax list, ArgumentSyntax argument)
    {
        if (argument.NameColon is { Name.Identifier.ValueText: { } named })
        {
            for (int i = 0; i < method.Parameters.Length; i++)
            {
                if (method.Parameters[i].Name == named)
                    return i;
            }

            return -1;
        }

        int position = list.Arguments.IndexOf(argument);
        return position < method.Parameters.Length ? position : -1;
    }
}
