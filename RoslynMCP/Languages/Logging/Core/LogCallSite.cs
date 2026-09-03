using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services.Symbols;

namespace RoslynMCP.Languages.Logging.Core;

/// <summary>Which library's template this is. What differs between them is documented per rule.</summary>
internal enum LoggingFramework
{
    MicrosoftExtensions,
    Serilog,
    NLog,
}

/// <summary>
/// How the template's holes reach the values.
/// </summary>
/// <remarks>
/// The single most surprising thing about structured logging in .NET, and the reason hover on a
/// hole is worth having: at a call site every framework binds <b>by position</b> — the names in
/// <c>"{User} did {Thing}"</c> are labels on the log event, not a lookup, so swapping the two
/// arguments swaps the values and nothing complains. Only the <c>[LoggerMessage]</c> source
/// generator binds by name, matching holes to parameters case-insensitively.
/// </remarks>
internal enum TemplateBinding
{
    Positional,
    ByName,
}

/// <summary>
/// One value a template can render: a parameter of a generated logging method, or an argument of
/// a logging call.
/// </summary>
/// <param name="Name">The parameter's name, or a readable name for the expression — which is what
/// the by-name binding matches on and what completion offers.</param>
/// <param name="Span">Where in the document to report about it. The parameter's identifier, or the
/// argument expression.</param>
internal readonly record struct TemplateValue(
    string Name,
    string Type,
    TextSpan Span,
    bool IsException);

/// <summary>
/// A string literal that turned out to be a logging message template, together with everything
/// that decides what its holes mean.
/// </summary>
/// <param name="ValuesAreComplete">False when the values could not all be enumerated — a
/// <c>params</c> array passed as an array, a call that did not bind. Every count-based rule stands
/// down, because the alternative is reporting a mismatch against a list we know is short.</param>
/// <param name="Subject">What to call this in a message: <c>LogWarning</c>, <c>Log.Error</c>,
/// the generated method's name.</param>
internal sealed record LogCallSite(
    LoggingFramework Framework,
    TemplateBinding Binding,
    ImmutableArray<TemplateValue> Values,
    bool ValuesAreComplete,
    string Subject)
{
    /// <summary>Parameter names a message template is passed under, across the three libraries.</summary>
    private static readonly string[] s_templateParameters =
        ["message", "messageTemplate", "format", "formatString"];

    /// <summary>
    /// The cheap gate, run against every string literal in a document before anything binds.
    /// </summary>
    /// <remarks>
    /// Syntax only, and deliberately generous — an argument of a call, or the <c>Message</c> of an
    /// attribute. What it exists to reject is the overwhelming majority of literals, which are
    /// neither.
    /// </remarks>
    public static bool CouldBeTemplate(SyntaxToken token)
    {
        if (!token.IsKind(SyntaxKind.StringLiteralToken) || token.Parent is not ExpressionSyntax literal)
            return false;

        return literal.Parent switch
        {
            ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax } => true,
            AttributeArgumentSyntax { NameEquals.Name.Identifier.ValueText: "Message" } => true,
            _ => false,
        };
    }

    /// <summary>
    /// What binds this literal, or null when it is not a template after all.
    /// </summary>
    public static LogCallSite? Resolve(SemanticModel model, SyntaxToken token, CancellationToken ct)
    {
        if (!CouldBeTemplate(token) || token.Parent is not ExpressionSyntax literal)
            return null;

        return literal.Parent switch
        {
            ArgumentSyntax argument => FromCall(model, argument, ct),
            AttributeArgumentSyntax attribute => FromAttribute(model, attribute, ct),
            _ => null,
        };
    }

    // ---- A call: logger.LogWarning(ex, "…", value) --------------------------------------------

    private static LogCallSite? FromCall(SemanticModel model, ArgumentSyntax argument, CancellationToken ct)
    {
        if (argument.Parent is not ArgumentListSyntax list
            || list.Parent is not InvocationExpressionSyntax invocation)
        {
            return null;
        }

        if (model.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method)
            return null;

        if (FrameworkOf(method.ContainingType) is not { } framework)
            return null;

        var parameters = method.Parameters;
        int index = ParameterIndex(method, list, argument);
        if (index < 0 || index >= parameters.Length)
            return null;

        var parameter = parameters[index];
        if (!MemberSignature.Named(parameter.Type, "string")
            || !s_templateParameters.Contains(parameter.Name, StringComparer.Ordinal))
        {
            return null;
        }

        // LoggerMessage.Define<int, string>(…, "{Count} of {Name}") names its values by type
        // argument and takes no arguments at all for them. Never an exception, whatever the type
        // argument says: the delegate Define returns takes the exception in its own last
        // parameter, so "pass it first instead" would be advice about a call that does not exist.
        if (method.Name == "Define" && method.TypeArguments.Length > 0)
        {
            return new LogCallSite(
                framework, TemplateBinding.Positional,
                [.. method.TypeArguments.Select((type, i) => new TemplateValue(
                    $"arg{i}", type.ToDisplayString(MemberSignature.TypeName),
                    argument.Span, IsException: false))],
                ValuesAreComplete: true,
                $"{method.ContainingType.Name}.{method.Name}");
        }

        var (values, complete) = ArgumentValues(model, list, argument, ct);

        return new LogCallSite(
            framework, TemplateBinding.Positional, values, complete, SubjectOf(invocation, method));
    }

    /// <summary>
    /// Everything the call passes after the template, in order, with a name worth showing.
    /// </summary>
    /// <remarks>
    /// Reads the syntax rather than the bound parameters because what a positional template
    /// consumes is the argument list as written: a <c>params</c> parameter is one symbol and any
    /// number of arguments, and the count is the whole question here.
    /// </remarks>
    private static (ImmutableArray<TemplateValue> Values, bool Complete) ArgumentValues(
        SemanticModel model, ArgumentListSyntax list, ArgumentSyntax template, CancellationToken ct)
    {
        var arguments = list.Arguments;

        // A named argument anywhere means position is no longer the order they are written in,
        // and nothing here is worth guessing at.
        if (arguments.Any(a => a.NameColon is not null))
            return ([], false);

        int templateArgument = arguments.IndexOf(template);
        var values = ImmutableArray.CreateBuilder<TemplateValue>();
        bool complete = true;

        for (int i = templateArgument + 1; i < arguments.Count; i++)
        {
            var expression = arguments[i].Expression;
            var type = model.GetTypeInfo(expression, ct).Type;

            // `new object[] { a, b }` handed to a params parameter is one argument standing for
            // any number of values, so the list stops being countable here.
            if (i == templateArgument + 1
                && arguments.Count == templateArgument + 2
                && type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Object })
            {
                complete = false;
                break;
            }

            values.Add(new TemplateValue(
                Readable(expression),
                type?.ToDisplayString(MemberSignature.TypeName) ?? "?",
                expression.Span,
                IsException(type)));
        }

        return (values.ToImmutable(), complete);
    }

    // ---- An attribute: [LoggerMessage(Message = "…")] on a partial method ----------------------

    private static LogCallSite? FromAttribute(
        SemanticModel model, AttributeArgumentSyntax argument, CancellationToken ct)
    {
        if (argument.FirstAncestorOrSelf<AttributeSyntax>() is not { } attribute
            || model.GetSymbolInfo(attribute, ct).Symbol?.ContainingType is not { } attributeType
            || attributeType.ToDisplayString(MemberSignature.DeclarationName)
                != "Microsoft.Extensions.Logging.LoggerMessageAttribute")
        {
            return null;
        }

        if (attribute.FirstAncestorOrSelf<MethodDeclarationSyntax>() is not { } declaration
            || model.GetDeclaredSymbol(declaration, ct) is not { } method)
        {
            return null;
        }

        var values = ImmutableArray.CreateBuilder<TemplateValue>();

        foreach (var parameter in method.Parameters)
        {
            // The three the generator consumes itself and never renders. Reporting them as unused
            // is the first thing that would get the rule switched off.
            if (IsException(parameter.Type) || IsLogger(parameter.Type) || IsLogLevel(parameter.Type))
                continue;

            var identifier = parameter.DeclaringSyntaxReferences.Length > 0
                && parameter.DeclaringSyntaxReferences[0].GetSyntax(ct) is ParameterSyntax syntax
                ? syntax.Identifier.Span
                : declaration.Identifier.Span;

            values.Add(new TemplateValue(
                parameter.Name,
                parameter.Type.ToDisplayString(MemberSignature.TypeName),
                identifier,
                IsException: false));
        }

        return new LogCallSite(
            LoggingFramework.MicrosoftExtensions, TemplateBinding.ByName, values.ToImmutable(),
            ValuesAreComplete: true, method.Name);
    }

    // ---- Recognising the libraries -------------------------------------------------------------

    /// <summary>
    /// The library a method's declaring type belongs to, by namespace root.
    /// </summary>
    /// <remarks>
    /// By namespace rather than by a list of type names because each library spreads its logging
    /// methods over several types — an interface, a concrete logger, one or more extension classes
    /// — and gains more between versions. The parameter name and shape are what actually narrow
    /// this to a template; the namespace only says whose dialect it is.
    /// </remarks>
    private static LoggingFramework? FrameworkOf(INamedTypeSymbol? type) =>
        Root(type?.ContainingNamespace) switch
        {
            "Serilog" => LoggingFramework.Serilog,
            "NLog" => LoggingFramework.NLog,
            "Microsoft" when Namespace(type).StartsWith("Microsoft.Extensions.Logging", StringComparison.Ordinal)
                => LoggingFramework.MicrosoftExtensions,
            _ => null,
        };

    private static string Root(INamespaceSymbol? ns)
    {
        while (ns is { IsGlobalNamespace: false, ContainingNamespace.IsGlobalNamespace: false })
            ns = ns.ContainingNamespace;

        return ns is { IsGlobalNamespace: false } ? ns.Name : "";
    }

    private static string Namespace(INamedTypeSymbol? type) =>
        type?.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : "";

    private static bool IsException(ITypeSymbol? type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (MemberSignature.Named(current, "System.Exception"))
                return true;
        }

        return false;
    }

    private static bool IsLogger(ITypeSymbol type) =>
        type.ToDisplayString(MemberSignature.DeclarationName)
            .StartsWith("Microsoft.Extensions.Logging.ILogger", StringComparison.Ordinal);

    private static bool IsLogLevel(ITypeSymbol type) =>
        MemberSignature.Named(type, "Microsoft.Extensions.Logging.LogLevel");

    // ---- Naming things for a message -----------------------------------------------------------

    /// <summary>Which parameter an argument reaches.</summary>
    /// <remarks>
    /// Written position is parameter position in both call forms: <c>GetSymbolInfo</c> hands back
    /// the <i>reduced</i> extension method for <c>logger.LogWarning(…)</c>, whose parameter list
    /// already excludes the receiver, and the unreduced one only for
    /// <c>LoggerExtensions.LogWarning(logger, …)</c>, where the receiver is written as an argument.
    /// </remarks>
    private static int ParameterIndex(
        IMethodSymbol method, ArgumentListSyntax list, ArgumentSyntax argument)
    {
        if (argument.NameColon?.Name.Identifier.ValueText is not { } named)
            return list.Arguments.IndexOf(argument);

        for (int i = 0; i < method.Parameters.Length; i++)
        {
            if (method.Parameters[i].Name == named)
                return i;
        }

        return -1;
    }

    /// <summary>
    /// A short name for an expression, the way an editor's parameter hints name one: the identifier
    /// if it is one, the last name of a member access or a call, else the text itself.
    /// </summary>
    private static string Readable(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax name => name.Identifier.ValueText,
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
        InvocationExpressionSyntax call => Readable(call.Expression),
        ConditionalAccessExpressionSyntax conditional => Readable(conditional.Expression),
        CastExpressionSyntax cast => Readable(cast.Expression),
        ParenthesizedExpressionSyntax parenthesized => Readable(parenthesized.Expression),
        _ => expression.ToString(),
    };

    private static string SubjectOf(InvocationExpressionSyntax invocation, IMethodSymbol method) =>
        invocation.Expression is MemberAccessExpressionSyntax access
            ? $"{access.Expression}.{access.Name.Identifier.ValueText}"
            : method.Name;
}
