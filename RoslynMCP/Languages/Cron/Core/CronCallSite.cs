using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMCP.Services.Symbols;

namespace RoslynMCP.Languages.Cron.Core;

/// <summary>
/// Whether a string literal is a schedule, and which library reads it.
/// </summary>
/// <remarks>
/// Two stages, and the split is what makes the pack affordable. <see cref="CouldBeCron"/> is syntax
/// and characters only, and it runs against every string literal in a document on every diagnostics
/// pass; <see cref="Resolve"/> binds the enclosing call and only ever sees the few that survived.
/// The overwhelming majority of literals in a codebase — prose, paths, SQL, keys — are rejected for
/// the cost of a scan over their own characters.
/// </remarks>
internal static class CronCallSite
{
    /// <summary>
    /// The cheap gate.
    /// </summary>
    /// <remarks>
    /// A crontab expression is written from a tiny alphabet: digits, the five operators, the
    /// three-letter month and day names, and spaces. Anything holding a lowercase letter or a
    /// punctuation mark outside that set is not one, which throws away ordinary prose immediately.
    /// The method-name list is the other half, and it exists for the case the character test is
    /// wrong about: the empty string somebody is part-way through typing a schedule into.
    /// </remarks>
    public static bool CouldBeCron(SyntaxToken token)
    {
        if (!token.IsKind(SyntaxKind.StringLiteralToken)
            || token.Parent is not ExpressionSyntax literal
            || literal.Parent is not ArgumentSyntax { Parent: BaseArgumentListSyntax list }
            || list.Parent is not (InvocationExpressionSyntax or ObjectCreationExpressionSyntax))
        {
            return false;
        }

        return LooksLikeCron(token.ValueText)
            || (list.Parent is InvocationExpressionSyntax invocation
                && CronPresets.SchedulingMethods.Contains(Called(invocation)));
    }

    /// <summary>Whether text is written in the alphabet a schedule is written in.</summary>
    /// <remarks>
    /// Deliberately permissive about what it accepts and strict about what it rejects: this is a
    /// filter in front of a binder, not a validator. A string that passes but turns out to be
    /// nobody's schedule costs one symbol lookup; one that fails when it should not is a feature
    /// that silently does not work.
    /// </remarks>
    public static bool LooksLikeCron(string text)
    {
        if (text.Length == 0)
            return false;

        // A macro is the one shape written in lowercase words, so it is recognised before the
        // alphabet test rather than inside it.
        if (text[0] == '@')
            return true;

        int spaces = 0;
        bool marker = false;

        foreach (char c in text)
        {
            switch (c)
            {
                case ' ':
                    spaces++;
                    break;
                case '*' or '?' or '#' or '/':
                    marker = true;
                    break;
                case ',' or '-':
                    break;
                default:
                    if (!char.IsAsciiDigit(c) && !char.IsAsciiLetterUpper(c))
                        return false;
                    break;
            }
        }

        // Four spaces is a five-field expression; a marker catches the rest, including the
        // one-field nonsense somebody is half-way through typing.
        return spaces >= 4 || marker;
    }

    /// <summary>The simple name a call invokes, without binding it.</summary>
    private static string Called(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        _ => string.Empty,
    };

    /// <summary>
    /// What reads this string as a schedule, or null when nothing does.
    /// </summary>
    /// <remarks>
    /// A configured or shipped binding first, then the parameter's name. The order matters: a
    /// binding says which library reads the expression and therefore how to read it, and the name
    /// alone does not — so a Hangfire call that would also satisfy the name rule must be claimed by
    /// the binding, or its day of week would be numbered as a plain crontab's.
    /// </remarks>
    public static CronCall? Resolve(
        ImmutableArray<CronBinding> bindings,
        ImmutableArray<string> parameterNames,
        SemanticModel model,
        SyntaxToken token,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (token.Parent is not ExpressionSyntax literal)
            return null;

        if (ConfigForwarding.Callee(literal, model) is not var (method, index))
            return null;

        var parameters = (method.ReducedFrom ?? method).Parameters;
        if (index < 0 || index >= parameters.Length)
            return null;

        var parameter = parameters[index];
        if (!MemberSignature.Named(parameter.Type, "string"))
            return null;

        string subject = $"{method.ContainingType.Name}.{method.Name}";

        foreach (var binding in bindings)
        {
            if (!Binds(binding, method, parameters, index, parameterNames))
                continue;

            // A removal names a job and carries no schedule. Reaching here means a string argument
            // of one looked like a crontab, which is a coincidence rather than a claim.
            if (binding.Kind == CronRegistrationKind.Remove)
                return null;

            return new CronCall(binding, Dialect(binding, model), subject, literal.Span);
        }

        if (!parameterNames.Contains(parameter.Name, StringComparer.OrdinalIgnoreCase))
            return null;

        // Nothing named a library, so the compilation is asked, and if it hosts both — or neither —
        // the plain crontab reading stands. It is the conservative answer: its only difference from
        // Hangfire's is two macros, and its day of week agrees with Hangfire's rather than Quartz's.
        return new CronCall(
            Binding: null,
            CronTypes.For(model.Compilation).Dialect ?? CronDialect.Standard,
            subject,
            literal.Span);
    }

    private static CronDialect Dialect(CronBinding binding, SemanticModel model) =>
        binding.Library == CronLibrary.Unknown
            ? CronTypes.For(model.Compilation).Dialect ?? binding.Dialect
            : binding.Dialect;

    /// <summary>Whether a binding claims this argument of this method.</summary>
    private static bool Binds(
        CronBinding binding,
        IMethodSymbol method,
        ImmutableArray<IParameterSymbol> parameters,
        int index,
        ImmutableArray<string> parameterNames)
    {
        if (!binding.MemberName.Equals(method.Name, StringComparison.Ordinal))
            return false;

        if (binding.ContainingType is { } type
            && !MemberSignature.DeclaredBy(method.ContainingType, type))
        {
            return false;
        }

        if (binding.ParameterTypes is { } expected && !MemberSignature.Matches(method, expected))
            return false;

        // A removal has no cron position at all; it matches on the member alone so that the caller
        // can tell "this is a scheduling API that carries no schedule" from "this is not one".
        if (binding.Kind == CronRegistrationKind.Remove)
            return true;

        if (binding.CronIndex is { } position)
            return position == index;

        // No position given, so the parameter's name is what says which one it is — the shape the
        // Hangfire entry uses to stand in for every one of its overloads.
        return parameterNames.Contains(parameters[index].Name, StringComparer.OrdinalIgnoreCase);
    }
}
