using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMCP.Languages;

/// <summary>
/// The mechanics behind reading a setting through a method of one's own —
/// <c>Config.GetSetting("Test")</c> where <c>GetSetting</c> hands its parameter to
/// <c>ConfigurationManager.AppSettings</c> or to <c>IConfiguration</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every long-lived codebase has one of these. The read is wrapped once — for a default value, a
/// cast, a null check, a log line — and from then on nothing in the solution names a configuration
/// API at the call site, so a scan that only recognises the framework's own shapes reports every
/// setting in the file as unused and every name in the code as unknown. The wrapper is not an
/// obstacle to see past; it <em>is</em> the read, as far as the people writing the calls are
/// concerned.
/// </para>
/// <para>
/// What makes a method a forwarder is decided by binding, never by name: the parameter the caller
/// filled in has to reach a key position of a read the pack already recognises. So a
/// <c>GetSetting</c> that reads a dictionary of its own is not one, and a wrapper called
/// <c>Q</c> is. Wrappers of wrappers resolve by recursion, bounded by
/// <see cref="MaxDepth"/> — deep enough for the two or three layers legacy code accumulates, and
/// short enough that a cycle costs nothing.
/// </para>
/// <para>
/// The pack-specific half — what a read at that key position <em>means</em>, a configuration path
/// or a <c>.config</c> section — stays with the pack. This file only answers where the parameter
/// goes.
/// </para>
/// </remarks>
internal static class ConfigForwarding
{
    /// <summary>How many wrappers deep a read is still followed.</summary>
    public const int MaxDepth = 3;

    /// <summary>
    /// The name a forwarder is recognised by across compilations.
    /// </summary>
    /// <remarks>
    /// A symbol identity would be wrong here: a forwarder is discovered in the compilation of the
    /// project declaring it and matched against calls in the compilations of the projects using
    /// it, and those are different symbol instances for the same method. The declaring type and
    /// the method name are what both sides agree on.
    /// </remarks>
    public static string Key(IMethodSymbol method)
    {
        var definition = (method.ReducedFrom ?? method).OriginalDefinition;

        return definition.ContainingType is { } type
            ? type.ToDisplayString() + "." + definition.Name
            : definition.Name;
    }

    /// <summary>
    /// The method an expression is an argument to, and which parameter it fills — or null when it
    /// is not an argument at all.
    /// </summary>
    /// <remarks>
    /// Named arguments are resolved to their parameter rather than their position, and an
    /// extension method invoked as one has its <c>this</c> parameter added back, so the index
    /// always refers to the declaration.
    /// </remarks>
    public static (IMethodSymbol Method, int Index)? Callee(
        ExpressionSyntax expression, SemanticModel model)
    {
        if (expression.Parent is not ArgumentSyntax argument
            || argument.Parent is not BaseArgumentListSyntax list
            || list.Parent is not (InvocationExpressionSyntax or ObjectCreationExpressionSyntax))
        {
            return null;
        }

        if (model.GetSymbolInfo(list.Parent).Symbol is not IMethodSymbol method)
            return null;

        var parameters = (method.ReducedFrom ?? method).Parameters;
        int shift = method.ReducedFrom is null ? 0 : 1;

        if (argument.NameColon?.Name.Identifier.Text is { Length: > 0 } named)
        {
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].Name == named)
                    return (method, i);
            }

            return null;
        }

        int position = list.Arguments.IndexOf(argument);
        if (position < 0)
            return null;

        int index = position + shift;

        // A params array collects everything from its own position on; the key is never one, so
        // an argument past the last parameter is nobody's.
        return index < parameters.Length ? (method, index) : null;
    }

    /// <summary>
    /// The method's declaration and a model that can bind it, or null when its source is not in
    /// the solution — a method from a real assembly reference has nothing to read.
    /// </summary>
    public static async Task<(BaseMethodDeclarationSyntax Declaration, SemanticModel Model)?>
        DeclarationAsync(IMethodSymbol method, Solution solution, CancellationToken ct)
    {
        var definition = (method.ReducedFrom ?? method).OriginalDefinition;

        // A partial method's body lives on the implementation part; the reference the caller binds
        // to may be the defining one.
        if (definition.PartialImplementationPart is { } implementation)
            definition = implementation;

        foreach (var reference in definition.DeclaringSyntaxReferences)
        {
            ct.ThrowIfCancellationRequested();

            if (await reference.GetSyntaxAsync(ct) is not BaseMethodDeclarationSyntax declaration)
                continue;

            if (declaration is { Body: null, ExpressionBody: null })
                continue;

            if (solution.GetDocument(declaration.SyntaxTree) is not { } document)
                continue;

            if (await document.GetSemanticModelAsync(ct) is not { } model)
                continue;

            return (declaration, model);
        }

        return null;
    }

    /// <summary>
    /// Every place inside a declaration that reads parameter <paramref name="index"/> — the
    /// positions a caller's argument arrives at.
    /// </summary>
    /// <remarks>
    /// Matched on the bound symbol rather than the identifier's text, so a local or a lambda
    /// parameter that shadows the name is not mistaken for it.
    /// </remarks>
    public static IEnumerable<IdentifierNameSyntax> ParameterReads(
        BaseMethodDeclarationSyntax declaration, SemanticModel model, int index)
    {
        if (declaration.ParameterList.Parameters.Count <= index)
            yield break;

        string name = declaration.ParameterList.Parameters[index].Identifier.Text;

        foreach (var identifier in declaration.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (identifier.Identifier.Text != name)
                continue;

            if (model.GetSymbolInfo(identifier).Symbol is IParameterSymbol { Ordinal: { } ordinal }
                && ordinal == index)
            {
                yield return identifier;
            }
        }
    }

    /// <summary>
    /// The method and parameter an expression reads, when it reads one of its own method's
    /// parameters — how a read site says "this is a forwarder" during a scan.
    /// </summary>
    public static (IMethodSymbol Method, int Index)? ForwardedParameter(
        ExpressionSyntax expression, SemanticModel model)
    {
        if (expression is not IdentifierNameSyntax)
            return null;

        return model.GetSymbolInfo(expression).Symbol is IParameterSymbol
        {
            ContainingSymbol: IMethodSymbol { MethodKind: MethodKind.Ordinary } method,
        } parameter
            ? (method, parameter.Ordinal)
            : null;
    }

    /// <summary>Whether an invocation names a method that could be the forwarder — the cheap gate
    /// before binding, matching the name the declaration carries.</summary>
    public static string? InvokedName(InvocationExpressionSyntax invocation) =>
        invocation.Expression switch
        {
            MemberAccessExpressionSyntax { Name: { } name } => name.Identifier.Text,
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            GenericNameSyntax generic => generic.Identifier.Text,
            MemberBindingExpressionSyntax { Name: { } name } => name.Identifier.Text,
            _ => null,
        };
}
