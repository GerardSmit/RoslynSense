using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp;
using RoslynMCP.Services.Symbols;

namespace RoslynMCP.Languages.Routes.Core;

/// <summary>
/// Every HTTP endpoint a project declares, computed once per <see cref="Compilation"/>.
/// </summary>
/// <remarks>
/// <para>
/// Two shapes with nothing in common, found in one pass. An action is a method carrying an
/// attribute, and its path is half written on the method and half on the type; a minimal API is an
/// ordinary call, and its path is half written in the call and half in a group opened somewhere
/// above it. Both end as the same row because a reader looking for "what does this service serve"
/// does not care which style declared it.
/// </para>
/// <para>
/// Syntax before symbols, the same bargain <see cref="Cron.Core.CronJobIndex"/> makes: an
/// attribute's name and a call's simple name are both readable without binding anything, so the
/// semantic model is asked about the few dozen places that could be endpoints rather than the few
/// thousand that are not. A compilation is immutable, so the answer cannot change under it — a
/// keystroke produces a new one whose index is built on first ask, and the old one falls out with
/// its compilation.
/// </para>
/// </remarks>
internal sealed class RouteIndex(RoutesSettings settings)
{
    /// <summary>
    /// How far up a chain of groups to follow a prefix.
    /// </summary>
    /// <remarks>
    /// Groups nest, and a cap costs nothing real — three levels is an unusually organised API and
    /// eight is beyond anything written on purpose. It is here so that a cycle a malformed edit
    /// produces mid-keystroke cannot spin the index.
    /// </remarks>
    private const int GroupDepthLimit = 8;

    /// <summary>One table per index, and one index per pack — so the settings are not part of the
    /// key. See <see cref="Cron.Core.CronJobIndex"/> for why that matters.</summary>
    private readonly ConditionalWeakTable<Compilation, IReadOnlyList<RouteEndpoint>> _cache = new();

    /// <summary>The endpoints of a compilation, built once.</summary>
    public IReadOnlyList<RouteEndpoint> Of(
        Compilation compilation, string projectPath, CancellationToken ct)
    {
        if (_cache.TryGetValue(compilation, out var cached))
            return cached;

        return _cache.GetValue(compilation, c => Build(c, projectPath, settings, ct));
    }

    private static IReadOnlyList<RouteEndpoint> Build(
        Compilation compilation, string projectPath, RoutesSettings settings, CancellationToken ct)
    {
        var found = ImmutableArray.CreateBuilder<RouteEndpoint>();
        var memo = new Memo();

        foreach (var tree in compilation.SyntaxTrees)
        {
            ct.ThrowIfCancellationRequested();

            if (tree.FilePath is not { Length: > 0 } filePath)
                continue;

            var root = tree.GetRoot(ct);

            // One walk, not one per shape. The two candidate kinds are found in the same pass
            // because the pass itself — every node of every file in the project — is the cost,
            // and it is paid again on the next keystroke's compilation.
            var actions = new List<MethodDeclarationSyntax>();
            var calls = new List<InvocationExpressionSyntax>();

            foreach (var node in root.DescendantNodes())
            {
                switch (node)
                {
                    case MethodDeclarationSyntax method when Routed(method, settings):
                        actions.Add(method);
                        break;

                    case InvocationExpressionSyntax call
                        when settings.MethodNames.Contains(Called(call)):
                        calls.Add(call);
                        break;
                }
            }

            if (actions.Count == 0 && calls.Count == 0)
                continue;

            // Only now, and only for a file that holds a candidate: asking for a semantic model
            // binds the tree, and most files in a project declare no endpoint at all.
            var model = compilation.GetSemanticModel(tree);
            var text = tree.GetText(ct);

            foreach (var action in actions)
            {
                ct.ThrowIfCancellationRequested();
                FromAttributes(action, model, text, projectPath, filePath, settings, memo, found, ct);
            }

            foreach (var call in calls)
            {
                ct.ThrowIfCancellationRequested();
                FromCall(call, model, text, projectPath, filePath, settings, memo, found, ct);
            }
        }

        return found.ToImmutable();
    }

    /// <summary>
    /// What one <see cref="Build"/> has already worked out, so it does not work it out again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything in here is a function of one immutable compilation, so a memo is safe for
    /// exactly as long as the build that owns it and is thrown away with it — no invalidation to
    /// get wrong, and nothing outlives the snapshot it was derived from.
    /// </para>
    /// <para>
    /// Both entries are about the same shape: a fact written once and read once per <em>use</em>.
    /// A controller's <c>[Route]</c> is read again for each of its forty actions, and a group's
    /// prefix again for each endpoint registered on it — and each re-read binds an attribute or
    /// walks a receiver chain, in the two files where endpoints are dense by construction.
    /// </para>
    /// </remarks>
    private sealed class Memo
    {
        /// <summary>The prefix a controller contributes, per controller.</summary>
        public Dictionary<ISymbol, RegistrationFacet> Prefixes { get; } =
            new(SymbolEqualityComparer.Default);

        /// <summary>The prefix a <c>MapGroup</c> call opens, per call.</summary>
        public Dictionary<InvocationExpressionSyntax, RegistrationFacet> Groups { get; } = [];

        /// <summary>
        /// A semantic model per tree.
        /// </summary>
        /// <remarks>
        /// <see cref="Compilation.GetSemanticModel"/> is not cached — it constructs a fresh model
        /// with an empty binder cache every call. A partial controller whose <c>[Route]</c> is
        /// written in the other half asks for one per action, so a forty-action controller built
        /// forty models and re-bound the same file into each.
        /// </remarks>
        public Dictionary<SyntaxTree, SemanticModel> Models { get; } = [];

        public SemanticModel ModelFor(SemanticModel owner, SyntaxTree tree)
        {
            if (tree == owner.SyntaxTree)
                return owner;

            if (!Models.TryGetValue(tree, out var model))
                Models[tree] = model = owner.Compilation.GetSemanticModel(tree);

            return model;
        }
    }

    // ---- The syntax gates ---------------------------------------------------------------------

    /// <summary>Whether a method carries anything the tables call a route attribute.</summary>
    private static bool Routed(MethodDeclarationSyntax method, RoutesSettings settings)
    {
        foreach (var list in method.AttributeLists)
        {
            foreach (var attribute in list.Attributes)
            {
                if (settings.AttributeNames.Contains(RouteNames.Bare(RouteNames.Written(attribute))))
                    return true;
            }
        }

        return false;
    }

    private static string Called(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        _ => string.Empty,
    };

    // ---- Attribute routing --------------------------------------------------------------------

    /// <summary>
    /// One action method, as however many endpoints its attributes declare.
    /// </summary>
    /// <remarks>
    /// Three shapes, and the rule between them is the framework's rather than a choice. An
    /// attribute carrying a template is an endpoint. An attribute carrying only a verb constrains
    /// the template written beside it — <c>[Route("orders")]</c> with <c>[HttpGet]</c> under it is
    /// one endpoint and not two — and where there is no template beside it, it is an endpoint on
    /// the type's prefix alone, which is what <c>[HttpGet]</c> on a controller with a
    /// <c>[Route]</c> means and is the commonest shape of all.
    /// </remarks>
    private static void FromAttributes(
        MethodDeclarationSyntax action,
        SemanticModel model,
        SourceText text,
        string projectPath,
        string filePath,
        RoutesSettings settings,
        Memo memo,
        ImmutableArray<RouteEndpoint>.Builder found,
        CancellationToken ct)
    {
        if (model.GetDeclaredSymbol(action, ct) is not IMethodSymbol symbol)
            return;

        var declared = new List<(RouteAttributeBinding Binding, RegistrationFacet Template, AttributeSyntax Syntax)>();

        foreach (var list in action.AttributeLists)
        {
            foreach (var attribute in list.Attributes)
            {
                if (Match(attribute, model, settings, ct) is not { } binding)
                    continue;

                declared.Add((binding, Template(attribute, binding, model, ct), attribute));
            }
        }

        if (declared.Count == 0)
            return;

        var prefix = Prefix(symbol, model, settings, memo, ct);
        var target = RegistrationFacts.Declaration(symbol, ct);

        var handler = new RegistrationFacet(
            $"{symbol.ContainingType.Name}.{symbol.Name}", RegistrationOrigin.Literal, null);

        var templated = declared.Where(entry => Written(entry.Template)).ToList();
        var verbs = declared
            .Where(entry => !Written(entry.Template) && entry.Binding.Verb is not null)
            .ToList();

        if (templated.Count == 0)
        {
            foreach (var entry in verbs)
                Add(RegistrationFacet.Absent, entry.Binding.Verb, entry.Syntax);

            return;
        }

        foreach (var entry in templated)
        {
            if (entry.Binding.Verb is { } own)
            {
                Add(entry.Template, own, entry.Syntax);
            }
            else if (verbs.Count > 0)
            {
                foreach (var verb in verbs)
                    Add(entry.Template, verb.Binding.Verb, entry.Syntax);
            }
            else
            {
                Add(entry.Template, null, entry.Syntax);
            }
        }

        void Add(RegistrationFacet template, string? verb, AttributeSyntax at) =>
            found.Add(new RouteEndpoint(
                Path: RouteTemplates.Expand(
                    RouteTemplates.Combine(prefix, template, RouteSource.Attribute),
                    symbol.ContainingType.Name,
                    symbol.Name),
                Verb: verb,
                Handler: handler,
                Source: RouteSource.Attribute,
                ProjectPath: projectPath,
                FilePath: filePath,
                Offset: at.SpanStart,

                // The attribute rather than the whole method: it is where the path is written, and
                // the method's own name is one button away on the same row.
                Declaration: LspConverters.ToRange(text.Lines, at.Span),
                Target: target.Range,
                TargetUri: target.Uri));
    }

    /// <summary>
    /// The prefix a controller contributes to every action it declares.
    /// </summary>
    /// <remarks>
    /// Read off the type symbol rather than the syntax the method is nested in, so a partial class
    /// whose <c>[Route]</c> is written in the other half still contributes it. Attributes that
    /// carry a verb are skipped: an <c>[HttpGet]</c> on a type says nothing about a path, and the
    /// first attribute with a template is the prefix.
    /// <para>
    /// A prefix inherited from a base controller is not followed. It is legal and it happens, but
    /// following it means deciding which of several base attributes wins, and a wrong prefix
    /// produces a row that is well-formed and served by nobody — the one outcome worth more than
    /// the coverage.
    /// </para>
    /// </remarks>
    private static RegistrationFacet Prefix(
        IMethodSymbol symbol,
        SemanticModel model,
        RoutesSettings settings,
        Memo memo,
        CancellationToken ct)
    {
        if (symbol.ContainingType is not { } type)
            return RegistrationFacet.Absent;

        if (memo.Prefixes.TryGetValue(type, out var known))
            return known;

        var found = Read(type);
        memo.Prefixes[type] = found;
        return found;

        RegistrationFacet Read(INamedTypeSymbol declaring)
        {
            foreach (var data in declaring.GetAttributes())
            {
                ct.ThrowIfCancellationRequested();

                if (data.ApplicationSyntaxReference?.GetSyntax(ct) is not AttributeSyntax attribute)
                    continue;

                var owner = memo.ModelFor(model, attribute.SyntaxTree);

                if (Match(attribute, owner, settings, ct) is not { Verb: null } binding)
                    continue;

                var template = Template(attribute, binding, owner, ct);
                if (Written(template))
                    return template;
            }

            return RegistrationFacet.Absent;
        }
    }

    /// <summary>The table entry an attribute satisfies, if any does.</summary>
    private static RouteAttributeBinding? Match(
        AttributeSyntax attribute, SemanticModel model, RoutesSettings settings, CancellationToken ct)
    {
        string written = RouteNames.Bare(RouteNames.Written(attribute));
        if (written.Length == 0)
            return null;

        INamedTypeSymbol? declaring = null;
        bool resolved = false;

        foreach (var binding in settings.Attributes)
        {
            if (!RouteNames.Bare(binding.AttributeName).Equals(written, StringComparison.Ordinal))
                continue;

            if (binding.ContainingType is not { } expected)
                return binding;

            // Bound only for an entry that asked, which is none of the shipped ones — the whole
            // point of matching on the name is that four frameworks spell it the same.
            if (!resolved)
            {
                declaring = model.GetSymbolInfo(attribute, ct).Symbol?.ContainingType;
                resolved = true;
            }

            if (declaring is not null && MemberSignature.DeclaredBy(declaring, expected))
                return binding;
        }

        return null;
    }

    /// <summary>The template an attribute carries, or absent when it carries none.</summary>
    private static RegistrationFacet Template(
        AttributeSyntax attribute,
        RouteAttributeBinding binding,
        SemanticModel model,
        CancellationToken ct)
    {
        if (attribute.ArgumentList is not { Arguments.Count: > 0 } list)
            return RegistrationFacet.Absent;

        // A property setter — [HttpGet(Name = "…")] — is not a constructor argument and cannot be
        // the template however the positions are counted.
        var positional = list.Arguments.Where(argument => argument.NameEquals is null).ToList();

        if (binding.PathIndex is { } index)
        {
            return index >= 0 && index < positional.Count
                ? RegistrationFacts.Read(positional[index].Expression, model, ct)
                : RegistrationFacet.Absent;
        }

        foreach (var argument in positional)
        {
            // Typed rather than folded, so an [HttpGet(Order = …)] written positionally is passed
            // over instead of being reported as a path nobody could read.
            if (model.GetTypeInfo(argument.Expression, ct).Type?.SpecialType
                != SpecialType.System_String)
            {
                continue;
            }

            return RegistrationFacts.Read(argument.Expression, model, ct);
        }

        return RegistrationFacet.Absent;
    }

    // ---- Registration calls -------------------------------------------------------------------

    private static void FromCall(
        InvocationExpressionSyntax call,
        SemanticModel model,
        SourceText text,
        string projectPath,
        string filePath,
        RoutesSettings settings,
        Memo memo,
        ImmutableArray<RouteEndpoint>.Builder found,
        CancellationToken ct)
    {
        if (model.GetSymbolInfo(call, ct).Symbol is not IMethodSymbol method)
            return;

        var declared = method.ReducedFrom ?? method;

        // A group is a prefix, not an endpoint. It is read when an endpoint registered on it asks
        // where it sits, and it is not a row of its own — a path with nothing serving it.
        if (Match(declared, settings) is not { Kind: RouteCallKind.Endpoint } binding)
            return;

        var arguments = RegistrationFacts.Arguments(call, method, declared);

        var template = RegistrationFacts.Read(
            RegistrationFacts.At(arguments, PathPosition(binding, declared)), model, ct);

        var (handler, symbol) = Handler(
            RegistrationFacts.At(arguments, binding.HandlerIndex ?? HandlerPosition(declared)),
            model,
            ct);

        // A registration always writes its pattern, so finding no pattern argument at all means
        // this call is not the endpoint the name suggested — an overload of the same name, or a
        // member of somebody else's library that spells it the same way. An attribute may leave
        // both halves unwritten and still be a real endpoint (conventional routing), which is why
        // the guard is here and not in Combine.
        if (template.Origin == RegistrationOrigin.Absent)
            return;

        var (range, uri) = RegistrationFacts.Declaration(symbol, ct);

        found.Add(new RouteEndpoint(
            Path: RouteTemplates.Combine(
                GroupPrefix(call, model, settings, memo, GroupDepthLimit, ct),
                template,
                RouteSource.Registration),
            Verb: binding.Verb,
            Handler: handler,
            Source: RouteSource.Registration,
            ProjectPath: projectPath,
            FilePath: filePath,
            Offset: call.SpanStart,
            Declaration: LspConverters.ToRange(text.Lines, call.Span),
            Target: range,
            TargetUri: uri));
    }

    /// <summary>The table entry a call satisfies, if any does.</summary>
    private static RouteMethodBinding? Match(IMethodSymbol declared, RoutesSettings settings)
    {
        foreach (var binding in settings.Methods)
        {
            if (!binding.MemberName.Equals(declared.Name, StringComparison.Ordinal))
                continue;

            if (binding.ContainingType is { } type
                && !MemberSignature.DeclaredBy(declared.ContainingType, type))
            {
                continue;
            }

            if (binding.ParameterTypes is { } expected && !MemberSignature.Matches(declared, expected))
                continue;

            return binding;
        }

        return null;
    }

    /// <summary>
    /// The prefix the group this call was registered on contributes, if it was registered on one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two shapes reach a group: written inline —
    /// <c>app.MapGroup("/api").MapGet("/orders", …)</c> — or held in a local, which is how anything
    /// with more than one endpoint under it is written. The local is followed only when it is
    /// assigned exactly once, which is the same rule everything else here follows and for the same
    /// reason: a variable reassigned later has no one value to report.
    /// </para>
    /// <para>
    /// Anything else — a field, a property, a parameter — contributes no prefix. A builder passed
    /// into a method really can be a group, and this will then show that method's endpoints without
    /// it; the alternative is to mark every endpoint in every <c>MapOrders(this
    /// IEndpointRouteBuilder)</c> extension unknowable, which is the overwhelmingly common shape
    /// and overwhelmingly usually not a group.
    /// </para>
    /// </remarks>
    private static RegistrationFacet GroupPrefix(
        InvocationExpressionSyntax call,
        SemanticModel model,
        RoutesSettings settings,
        Memo memo,
        int depth,
        CancellationToken ct)
    {
        if (depth <= 0 || (call.Expression as MemberAccessExpressionSyntax)?.Expression is not { } receiver)
            return RegistrationFacet.Absent;

        var opened = receiver switch
        {
            InvocationExpressionSyntax inline => inline,
            IdentifierNameSyntax name
                when model.GetSymbolInfo(name, ct).Symbol is ILocalSymbol local =>
                RegistrationFacts.SingleAssignment(local, model, ct) as InvocationExpressionSyntax,
            _ => null,
        };

        if (opened is null)
            return RegistrationFacet.Absent;

        // Keyed on the group's own call, which is what every endpoint registered on it shares.
        // A hundred endpoints under one `api` group asked the same question a hundred times, and
        // answering it walks the receiver chain, binds each step and re-reads the pattern.
        if (memo.Groups.TryGetValue(opened, out var known))
            return known;

        var resolved = Resolve(opened);
        memo.Groups[opened] = resolved;
        return resolved;

        RegistrationFacet Resolve(InvocationExpressionSyntax group)
        {
            if (model.GetSymbolInfo(group, ct).Symbol is not IMethodSymbol method)
                return RegistrationFacet.Absent;

            var declared = method.ReducedFrom ?? method;
            if (Match(declared, settings) is not { Kind: RouteCallKind.Group } binding)
                return RegistrationFacet.Absent;

            var arguments = RegistrationFacts.Arguments(group, method, declared);
            var own = RegistrationFacts.Read(
                RegistrationFacts.At(arguments, PathPosition(binding, declared)), model, ct);

            return RouteTemplates.Combine(
                GroupPrefix(group, model, settings, memo, depth - 1, ct),
                own,
                RouteSource.Registration);
        }
    }

    /// <summary>
    /// What runs, when it is a method there is a name for.
    /// </summary>
    /// <remarks>
    /// A lambda is deliberately nothing. It <i>is</i> the handler, written where the row already
    /// points, so there is no second place to go — and reading a method out of its body would name
    /// whichever call it happens to make first, which for the usual
    /// <c>() =&gt; Results.Ok(…)</c> is a helper of the framework's.
    /// </remarks>
    private static (RegistrationFacet Facet, ISymbol? Symbol) Handler(
        ExpressionSyntax? expression, SemanticModel model, CancellationToken ct)
    {
        if (expression is null or LambdaExpressionSyntax or AnonymousMethodExpressionSyntax)
            return (RegistrationFacet.Absent, null);

        var info = model.GetSymbolInfo(expression, ct);

        // A method group written as an argument has no one symbol until the conversion picks an
        // overload, so it arrives as candidates. One candidate is still one place to go; several
        // are several methods sharing a name, and the row would have to pick arbitrarily.
        var found = info.Symbol
            ?? (info.CandidateSymbols is [var only] ? only : null);

        if (found is IMethodSymbol group)
        {
            return (
                new RegistrationFacet(
                    $"{group.ContainingType.Name}.{group.Name}", RegistrationOrigin.Literal, null),
                group);
        }

        return (RegistrationFacet.Absent, null);
    }

    /// <summary>Which parameter carries the template: the entry's position, or the first string.</summary>
    private static int? PathPosition(RouteMethodBinding binding, IMethodSymbol declared)
    {
        if (binding.PathIndex is { } position)
            return position;

        for (int i = 0; i < declared.Parameters.Length; i++)
        {
            if (MemberSignature.Named(declared.Parameters[i].Type, "string"))
                return i;
        }

        return null;
    }

    /// <summary>The first parameter that takes something callable.</summary>
    private static int? HandlerPosition(IMethodSymbol declared)
    {
        for (int i = 0; i < declared.Parameters.Length; i++)
        {
            var type = declared.Parameters[i].Type;
            if (type.TypeKind == TypeKind.Delegate || type.Name is "Delegate" or "RequestDelegate")
                return i;
        }

        return null;
    }

    /// <summary>Whether a facet carries a template at all, however knowable it turned out to be.</summary>
    private static bool Written(RegistrationFacet facet) =>
        facet.Origin != RegistrationOrigin.Absent;
}
