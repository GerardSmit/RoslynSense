using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMCP.Languages.Cron.Core;

/// <summary>
/// Reading an argument of a registration: its value where that is knowable, and where it came from
/// where it is not.
/// </summary>
/// <remarks>
/// <para>
/// The part of the pack that decides what the tree is allowed to claim. Roslyn already folds most
/// of what people write — literals, <c>const</c>s, <c>nameof</c>, joined constants, an interpolation
/// over constants — so the first question is simply whether the compiler knows the value, and the
/// interesting work is all in the second: when it does not, saying <i>why</i>, because "read from
/// <c>Jobs:Nightly:Cron</c>" is a useful row and "&lt;unknown&gt;" is not.
/// </para>
/// <para>
/// Nothing here guesses. A value this cannot follow is reported as one, never rendered as a
/// schedule — a wrong fire time in a list of jobs is worse than a missing one, because a reader has
/// no way to tell that it is wrong.
/// </para>
/// </remarks>
internal static class CronFacts
{
    /// <summary>How much of an unreadable expression is worth showing.</summary>
    /// <remarks>
    /// Enough to recognise the call, not enough to push the schedule off the row. The expression is
    /// a hint about where to look, and the row already carries a link to the exact line.
    /// </remarks>
    private const int DetailLimit = 40;

    /// <summary>What an argument says, and how firmly.</summary>
    public static CronFacet Read(ExpressionSyntax? expression, SemanticModel model, CancellationToken ct)
    {
        if (expression is null)
            return CronFacet.Absent;

        ct.ThrowIfCancellationRequested();

        // The compiler first. It already folds a literal, a const local or field, a nameof, a
        // constant '+' chain and an all-constant interpolation — every shape where the value is
        // genuinely written in the source, however indirectly.
        if (model.GetConstantValue(expression, ct) is { HasValue: true, Value: string folded })
        {
            return new CronFacet(
                folded,
                expression is LiteralExpressionSyntax ? CronOrigin.Literal : CronOrigin.Constant,
                null);
        }

        if (Configuration(expression, model, ct) is { } configuration)
            return configuration;

        return BySymbol(expression, model, ct);
    }

    /// <summary>
    /// The job's method, which is named by a shape rather than by a value.
    /// </summary>
    /// <remarks>
    /// Three shapes and they have nothing in common. Hangfire takes an expression tree —
    /// <c>x =&gt; x.SyncOrders()</c> — whose whole purpose is to be read rather than run, so the
    /// method is found by binding the invocation inside the lambda. A method group binds directly.
    /// Quartz names a type instead, through a type argument, because its unit of work is a class.
    /// <para>
    /// A lambda body that is not a single invocation is dynamic: a body that branches has no one
    /// method to name, and naming the first one it happens to call would be a row pointing at the
    /// wrong code.
    /// </para>
    /// </remarks>
    public static (CronFacet Facet, ISymbol? Symbol) Method(
        ExpressionSyntax? expression, SemanticModel model, CancellationToken ct)
    {
        if (expression is null)
            return (CronFacet.Absent, null);

        ct.ThrowIfCancellationRequested();

        var body = expression switch
        {
            SimpleLambdaExpressionSyntax lambda => lambda.Body,
            ParenthesizedLambdaExpressionSyntax lambda => lambda.Body,
            _ => null,
        };

        if (body is not null)
        {
            var call = body as InvocationExpressionSyntax
                ?? (body as ExpressionStatementSyntax)?.Expression as InvocationExpressionSyntax;

            if (call is null)
                return (new CronFacet(null, CronOrigin.Expression, Shorten(body.ToString())), null);

            return model.GetSymbolInfo(call, ct).Symbol is IMethodSymbol called
                ? (new CronFacet(Qualified(called), CronOrigin.Literal, null), called)
                : (new CronFacet(null, CronOrigin.Expression, Shorten(call.ToString())), null);
        }

        // A method group, and the same fact said more directly.
        if (model.GetSymbolInfo(expression, ct).Symbol is IMethodSymbol group)
            return (new CronFacet(Qualified(group), CronOrigin.Literal, null), group);

        // Quartz's unit of work is a class, named by a type argument rather than passed.
        if (model.GetSymbolInfo(expression, ct).Symbol is INamedTypeSymbol type)
            return (new CronFacet(type.Name, CronOrigin.Literal, null), type);

        return (new CronFacet(null, CronOrigin.Expression, Shorten(expression.ToString())), null);
    }

    /// <summary>The job type a generic registration names, as in <c>AddJob&lt;SyncOrders&gt;()</c>.</summary>
    public static (CronFacet Facet, ISymbol? Symbol) TypeArgument(
        IMethodSymbol method, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // The first type argument that is a real class rather than a type parameter still open.
        foreach (var argument in method.TypeArguments)
        {
            if (argument is INamedTypeSymbol { TypeKind: TypeKind.Class } type)
                return (new CronFacet(type.Name, CronOrigin.Literal, null), type);
        }

        return (CronFacet.Absent, null);
    }

    /// <summary>
    /// A configuration read, and the key it reads — which is the one fact worth having about a
    /// value that only exists at run time.
    /// </summary>
    /// <remarks>
    /// Four shapes cover nearly all of it: the indexer, <c>GetValue&lt;string&gt;("…")</c>,
    /// <c>GetSection("…").Value</c>, and .NET Framework's <c>AppSettings["…"]</c>. The key itself
    /// goes back through <see cref="Read"/>, because it is as often a <c>const</c> as a literal.
    /// </remarks>
    private static CronFacet? Configuration(
        ExpressionSyntax expression, SemanticModel model, CancellationToken ct)
    {
        // `.Value` on a section, which is the shape that wraps another one.
        if (expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Value" } value)
            expression = value.Expression;

        switch (expression)
        {
            case ElementAccessExpressionSyntax access
                when IsConfiguration(access.Expression, model, ct)
                    && access.ArgumentList.Arguments.Count == 1:
                return Key(access.ArgumentList.Arguments[0].Expression, model, ct);

            case InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax member,
            } call
                when member.Name.Identifier.ValueText is "GetValue" or "GetSection"
                    && call.ArgumentList.Arguments.Count > 0
                    && IsConfiguration(member.Expression, model, ct):
                return Key(call.ArgumentList.Arguments[^1].Expression, model, ct);

            default:
                return null;
        }
    }

    private static CronFacet Key(ExpressionSyntax key, SemanticModel model, CancellationToken ct)
    {
        var read = Read(key, model, ct);
        return new CronFacet(null, CronOrigin.Configuration, read.Text ?? read.Detail ?? "configuration");
    }

    /// <summary>
    /// Whether an expression is something configuration is read from.
    /// </summary>
    /// <remarks>
    /// By name rather than by resolving <c>Microsoft.Extensions.Configuration.IConfiguration</c>:
    /// the same shape is written against <c>ConfigurationManager.AppSettings</c>, against an
    /// options object a solution wrote for itself, and against a mock — and being wrong here costs
    /// a row that says "read from a key" about something that reads from a dictionary, which is
    /// close enough to true to be useful and nowhere near a claim about when the job runs.
    /// </remarks>
    private static bool IsConfiguration(ExpressionSyntax expression, SemanticModel model, CancellationToken ct)
    {
        var type = model.GetTypeInfo(expression, ct).Type;

        if (type is not null
            && (Mentions(type.Name) || type.AllInterfaces.Any(i => Mentions(i.Name))))
        {
            return true;
        }

        // Static shapes — ConfigurationManager.AppSettings — have a type that says nothing, so the
        // written expression is what is left to go on.
        return Mentions(expression.ToString());

        static bool Mentions(string name) =>
            name.Contains("Configuration", StringComparison.Ordinal)
            || name.Contains("AppSettings", StringComparison.Ordinal)
            || name.Contains("Settings", StringComparison.Ordinal);
    }

    /// <summary>Where a value the compiler could not fold came from.</summary>
    private static CronFacet BySymbol(
        ExpressionSyntax expression, SemanticModel model, CancellationToken ct)
    {
        var symbol = model.GetSymbolInfo(expression, ct).Symbol;

        switch (symbol)
        {
            case IParameterSymbol parameter:
                // The caller decides, and which caller is a question this list cannot answer.
                return new CronFacet(null, CronOrigin.Parameter, parameter.Name);

            case IFieldSymbol field:
                // `static readonly` is how a schedule is most often written once and shared, and
                // GetConstantValue says nothing about it — a readonly field is not a constant even
                // when its initializer is one.
                if (Initializer(field, ct) is { } initialiser
                    && model.Compilation.GetSemanticModel(initialiser.SyntaxTree)
                        .GetConstantValue(initialiser, ct) is { HasValue: true, Value: string folded })
                {
                    return new CronFacet(folded, CronOrigin.Constant, null);
                }

                return new CronFacet(null, CronOrigin.Variable, field.Name);

            case ILocalSymbol local:
                // A local assigned once is the value of its initializer, which is what a reader
                // would conclude too. Assigned more than once and it is genuinely a variable.
                return Local(local, model, ct) ?? new CronFacet(null, CronOrigin.Variable, local.Name);

            case IPropertySymbol property:
                return new CronFacet(null, CronOrigin.Variable, property.Name);

            default:
                return new CronFacet(null, CronOrigin.Expression, Shorten(expression.ToString()));
        }
    }

    /// <summary>The value of a local that is written exactly once, in its own declaration.</summary>
    private static CronFacet? Local(ILocalSymbol local, SemanticModel model, CancellationToken ct)
    {
        if (local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct)
            is not VariableDeclaratorSyntax { Initializer.Value: { } initialiser } declarator)
        {
            return null;
        }

        // Reassigned somewhere in the method it lives in, so the declaration is only its first
        // value rather than its value.
        if (declarator.FirstAncestorOrSelf<MemberDeclarationSyntax>() is { } member
            && member.DescendantNodes()
                .OfType<AssignmentExpressionSyntax>()
                .Any(assignment =>
                    SymbolEqualityComparer.Default.Equals(
                        model.GetSymbolInfo(assignment.Left, ct).Symbol, local)))
        {
            return null;
        }

        var read = Read(initialiser, model, ct);

        // A folded initializer is as good as a constant; anything else keeps its own origin, so a
        // local read out of configuration still says so.
        return read.Text is not null ? read with { Origin = CronOrigin.Constant } : read;
    }

    private static ExpressionSyntax? Initializer(IFieldSymbol field, CancellationToken ct) =>
        field.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct)
            is VariableDeclaratorSyntax { Initializer.Value: { } value }
            ? value
            : null;

    private static string Qualified(IMethodSymbol method) =>
        $"{method.ContainingType.Name}.{method.Name}";

    private static string Shorten(string text)
    {
        string flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= DetailLimit ? flat : flat[..(DetailLimit - 1)] + "…";
    }
}
