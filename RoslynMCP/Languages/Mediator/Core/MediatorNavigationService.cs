using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.Mediator.Core;

/// <summary>
/// From a dispatch to the handler that runs. The half of the pack F12 and Ctrl+F12 go through.
/// </summary>
/// <remarks>
/// The message type is the hub: the caret names a dispatch, the dispatch names a message, and the
/// message names its handlers. Going through it rather than matching call sites to handlers
/// directly is what lets one search answer both directions and both libraries.
/// </remarks>
internal static class MediatorNavigationService
{
    /// <summary>
    /// The handlers the caret dispatches to, or nothing when the caret is not on a dispatch — or is
    /// on one whose message cannot be named, which is the same answer, because Roslyn's own
    /// definition beats a guessed one.
    /// </summary>
    /// <param name="wantType">
    /// Answer with the handler type rather than the <c>Handle</c> that runs, for
    /// <c>textDocument/typeDefinition</c>.
    /// </param>
    public static async Task<IReadOnlyList<ISymbol>> ResolveTargetsAsync(
        Document document, int offset, ISymbol symbol, bool wantType, CancellationToken ct)
    {
        if (symbol is not IMethodSymbol method)
            return [];

        var compilation = await document.Project.GetCompilationAsync(ct);
        if (compilation is null || MediatorTypes.For(compilation) is not { } types)
            return [];

        bool generated = MediatorSymbols.IsGeneratedSenderExtension(method);
        if (!generated && !MediatorSymbols.TryGetDispatch(method, types, out _))
            return [];

        var root = await document.GetSyntaxRootAsync(ct);
        var model = await document.GetSemanticModelAsync(ct);
        if (root is null || model is null || InvocationAt(root, offset) is not { } invocation)
            return [];

        if (await MessageOfAsync(model, invocation, document.Project, types, ct) is not { } message)
            return [];

        return await FindHandlersAsync(message, document.Project, types, wantType, ct);
    }

    /// <summary>
    /// The invocation the caret's name belongs to, when the caret is on the name being invoked.
    /// </summary>
    /// <remarks>
    /// The node identity checks are what keep a caret inside an <em>argument</em> from being read as
    /// a caret on the call: F12 on the <c>CreateUserRequest</c> in <c>Send(new CreateUserRequest())</c>
    /// still has to reach the request type.
    /// </remarks>
    private static InvocationExpressionSyntax? InvocationAt(SyntaxNode root, int offset)
    {
        if (root.FindToken(offset).Parent is not SimpleNameSyntax name)
            return null;

        return name.Parent switch
        {
            MemberAccessExpressionSyntax access when access.Name == name =>
                access.Parent as InvocationExpressionSyntax,
            MemberBindingExpressionSyntax binding when binding.Name == name =>
                binding.Parent as InvocationExpressionSyntax,
            InvocationExpressionSyntax direct when direct.Expression == name => direct,
            _ => null,
        };
    }

    /// <summary>
    /// The message an invocation dispatches, or null when it dispatches none — or when it does but
    /// the message cannot be named. Shared with the reference search, which asks the same question
    /// of a call site it found from the other end.
    /// </summary>
    internal static async Task<MediatorMessage?> MessageOfAsync(
        SemanticModel model, InvocationExpressionSyntax invocation, Project project,
        MediatorTypes types, CancellationToken ct)
    {
        var info = model.GetSymbolInfo(invocation, ct);
        if ((info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) is not IMethodSymbol called)
            return null;

        // Read the generated body first and return whatever it says, because the overload taking
        // the request's constructor arguments names the message nowhere at the call site — nothing
        // below this line could recover it.
        if (MediatorSymbols.IsGeneratedSenderExtension(called))
            return await MessageOfGeneratedAsync(called, model.Compilation, project, ct);

        if (!MediatorSymbols.TryGetDispatch(called, types, out var dispatch))
            return null;

        var constructed = called.ReducedFrom ?? called;

        if (dispatch.MessageTypeParameter is { } typeParameter)
        {
            int ordinal = typeParameter.Ordinal;
            if (ordinal < constructed.TypeArguments.Length
                && MediatorSymbols.TryGetMessage(constructed.TypeArguments[ordinal], out var byArgument))
            {
                return byArgument;
            }
        }

        // The operation rather than the syntax: it puts named arguments, omitted optionals and the
        // MediatorNamespace-first overloads in the same shape, so nothing has to know that the
        // request is sometimes the first argument and sometimes the second.
        if (model.GetOperation(invocation, ct) is IInvocationOperation operation)
        {
            foreach (var argument in operation.Arguments)
            {
                if (argument.Parameter is not { } parameter)
                    continue;

                if (dispatch.MessageParameter is { } expected && parameter.Ordinal != expected.Ordinal)
                    continue;

                if (MediatorSymbols.TryGetMessage(Unwrap(argument.Value)?.Type, out var byParameter))
                    return byParameter;
            }
        }

        // A request the caller built elsewhere, or passed as object. Answering nothing hands the
        // caret back to Roslyn, which is right: a wrong jump is worse than an unhelpful one.
        return null;
    }

    private static IOperation? Unwrap(IOperation? operation) => operation switch
    {
        IConversionOperation conversion => Unwrap(conversion.Operand),
        IParenthesizedOperation parenthesized => Unwrap(parenthesized.Operand),
        _ => operation,
    };

    /// <summary>
    /// The message a generated extension method dispatches, read out of the body the generator
    /// emitted for it.
    /// </summary>
    /// <remarks>
    /// Every emitted overload expands to <c>sender.Send&lt;TMessage&gt;(…)</c>,
    /// <c>Publish&lt;TMessage&gt;(…)</c> or <c>CreateStream&lt;TMessage, TResponse&gt;(…)</c>, so
    /// the first type argument is the message in all of them. Read rather than recomputed from the
    /// method's name because the name is ambiguous — <c>CreateUserRequest</c> and
    /// <c>CreateUserNotification</c> in one namespace are both <c>CreateUserAsync</c>.
    /// </remarks>
    private static async Task<MediatorMessage?> MessageOfGeneratedAsync(
        IMethodSymbol method, Compilation compilation, Project project, CancellationToken ct)
    {
        var definition = (method.ReducedFrom ?? method).OriginalDefinition;

        if (ReadGeneratedBody(definition, compilation, ct) is { } fromBody)
            return fromBody;

        // Metadata here, source in the project that declares the message: the generated class lives
        // beside its message, so re-resolve into that compilation and read the body where there is
        // one to read.
        if (definition.DeclaringSyntaxReferences.Length == 0
            && definition.ContainingAssembly is { } assembly
            && project.Solution.GetProject(assembly, ct) is { } declaring)
        {
            var declaringCompilation = await declaring.GetCompilationAsync(ct);
            if (declaringCompilation is not null
                && SymbolFinder.FindSimilarSymbols(definition, declaringCompilation, ct).FirstOrDefault()
                    is { } similar
                && ReadGeneratedBody(similar, declaringCompilation, ct) is { } reResolved)
            {
                return reResolved;
            }
        }

        // Last resort, and enough for every overload that takes the request rather than building
        // it: the message is simply one of the parameters. A hand-written SenderExtensions partial
        // lands here too, which is why nothing above throws on a body it does not recognise.
        foreach (var parameter in definition.Parameters.Skip(1))
        {
            if (MediatorSymbols.TryGetMessage(parameter.Type, out var message))
                return message;
        }

        return null;
    }

    private static MediatorMessage? ReadGeneratedBody(
        IMethodSymbol definition, Compilation compilation, CancellationToken ct)
    {
        foreach (var reference in definition.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax(ct) is not MethodDeclarationSyntax
                { ExpressionBody.Expression: InvocationExpressionSyntax body })
            {
                continue;
            }

            if (body.Expression is not MemberAccessExpressionSyntax { Name: GenericNameSyntax generic }
                || generic.TypeArgumentList.Arguments.Count == 0
                || !compilation.ContainsSyntaxTree(generic.SyntaxTree))
            {
                continue;
            }

            var model = compilation.GetSemanticModel(generic.SyntaxTree);
            if (MediatorSymbols.TryGetMessage(
                    model.GetTypeInfo(generic.TypeArgumentList.Arguments[0], ct).Type, out var message))
            {
                return message;
            }
        }

        return null;
    }

    /// <summary>
    /// Every handler of <paramref name="message"/>, best first.
    /// </summary>
    /// <remarks>
    /// One reference search on the message type answers it, because a handler names its message in
    /// its base list and a delegate handler names it in its lambda's parameter. The scan below is
    /// only for the shape that search cannot see: a handler whose base list names an intermediate
    /// generic base rather than the message itself.
    /// </remarks>
    private static async Task<IReadOnlyList<ISymbol>> FindHandlersAsync(
        MediatorMessage message, Project project, MediatorTypes types, bool wantType, CancellationToken ct)
    {
        // The handlers live in projects that reference the message's project — sibling modules the
        // dispatch project has never heard of — and lazy loading never walks that direction. This
        // is only reached from F12/Ctrl+F12 on a dispatch, a deliberate gesture, so it may wait
        // for that scope to exist; otherwise the answer is whichever handler happened to be loaded.
        var solution = await SearchScopeService.WidenForSymbolAsync(
            message.Type, project, SearchScopeService.ExplicitSearchBudget, ct);
        project = solution.GetProject(project.Id) ?? project;

        var handlerTypes = new List<INamedTypeSymbol>();
        var delegateHandlers = new List<IMethodSymbol>();
        var models = new Dictionary<DocumentId, SemanticModel?>();

        var references = await SymbolFinder.FindReferencesAsync(
            message.Type.OriginalDefinition, project.Solution, ct);

        foreach (var referenced in references)
        {
            foreach (var location in referenced.Locations)
            {
                ct.ThrowIfCancellationRequested();

                var model = await ModelForAsync(models, location.Document, ct);
                var root = await location.Document.GetSyntaxRootAsync(ct);
                if (model is null || root is null)
                    continue;

                var node = root.FindNode(
                    location.Location.SourceSpan, findInsideTrivia: true, getInnermostNodeForTie: true);

                if (HandlerDeclaredAt(node, model, message, ct) is { } declared)
                    handlerTypes.Add(declared);
                else if (DelegateHandlerAt(node, model, ct) is { } lambda)
                    delegateHandlers.Add(lambda);
            }
        }

        if (handlerTypes.Count == 0 && delegateHandlers.Count == 0)
            handlerTypes.AddRange(await ScanForHandlersAsync(message, project, ct));

        var targets = new List<ISymbol>();
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        foreach (var handler in handlerTypes
                     .Distinct(SymbolEqualityComparer.Default)
                     .OfType<INamedTypeSymbol>()
                     .OrderByDescending(h => ExactlyHandles(h, message))
                     .ThenByDescending(h => !h.IsAbstract))
        {
            ISymbol target = wantType
                ? handler
                : (ISymbol?)MediatorSymbols.HandleMethodFor(handler, message, types) ?? handler;

            if (seen.Add(target))
                targets.Add(target);
        }

        // A lambda has no type to answer typeDefinition with, so it is offered whichever verb
        // asked: landing on the registration that carries the body is better than landing nowhere.
        foreach (var lambda in delegateHandlers)
        {
            if (seen.Add(lambda))
                targets.Add(lambda);
        }

        return targets;
    }

    private static bool ExactlyHandles(INamedTypeSymbol handler, MediatorMessage message) =>
        MediatorSymbols.HandlerInterfacesOf(handler).Any(h =>
            SymbolEqualityComparer.Default.Equals(h.Message.Type, message.Type));

    private static async Task<SemanticModel?> ModelForAsync(
        Dictionary<DocumentId, SemanticModel?> models, Document document, CancellationToken ct)
    {
        if (models.TryGetValue(document.Id, out var cached))
            return cached;

        var model = await document.GetSemanticModelAsync(ct);
        models[document.Id] = model;
        return model;
    }

    /// <summary>The handler whose base list this reference is in, if it handles the message.</summary>
    private static INamedTypeSymbol? HandlerDeclaredAt(
        SyntaxNode node, SemanticModel model, MediatorMessage message, CancellationToken ct)
    {
        var baseType = node.FirstAncestorOrSelf<BaseTypeSyntax>();
        if (baseType?.Parent?.Parent is not TypeDeclarationSyntax declaration)
            return null;

        if (model.GetDeclaredSymbol(declaration, ct) is not INamedTypeSymbol handler)
            return null;

        return MediatorSymbols.HandlerInterfacesOf(handler)
            .Any(h => MediatorSymbols.MessagesMatch(h.Message.Type, message.Type))
            ? handler
            : null;
    }

    /// <summary>
    /// The lambda this reference is the parameter type of, when it is being registered as a
    /// handler. Zapto's <c>builder.AddRequestHandler((GetMessage _) =&gt; …)</c> is a handler with
    /// no type of its own, and the lambda's parameter naming the message is the only trace of it.
    /// </summary>
    private static IMethodSymbol? DelegateHandlerAt(SyntaxNode node, SemanticModel model, CancellationToken ct)
    {
        if (node.FirstAncestorOrSelf<ParameterSyntax>() is null)
            return null;

        if (node.FirstAncestorOrSelf<AnonymousFunctionExpressionSyntax>() is not { } lambda)
            return null;

        if (lambda.FirstAncestorOrSelf<InvocationExpressionSyntax>() is not { } registration)
            return null;

        string name = registration.Expression switch
        {
            MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
            SimpleNameSyntax simple => simple.Identifier.ValueText,
            _ => string.Empty,
        };

        if (!MediatorSymbols.IsDelegateRegistration(name))
            return null;

        return model.GetSymbolInfo(lambda, ct).Symbol as IMethodSymbol;
    }

    /// <summary>
    /// The miss path: every source type in the projects that could reference the message, asked
    /// what it implements. Only reached when the reference search found no handler at all, which in
    /// practice means the handler's base list names an intermediate generic base.
    /// </summary>
    private static async Task<IReadOnlyList<INamedTypeSymbol>> ScanForHandlersAsync(
        MediatorMessage message, Project project, CancellationToken ct)
    {
        var solution = project.Solution;
        var declaring = message.Type.ContainingAssembly is { } assembly
            ? solution.GetProject(assembly, ct) ?? project
            : project;

        var candidates = new List<ProjectId> { declaring.Id };
        candidates.AddRange(
            solution.GetProjectDependencyGraph().GetProjectsThatTransitivelyDependOnThisProject(declaring.Id));

        var found = new List<INamedTypeSymbol>();

        foreach (var id in candidates.Distinct())
        {
            ct.ThrowIfCancellationRequested();

            if (solution.GetProject(id) is not { } candidate)
                continue;

            var compilation = await candidate.GetCompilationAsync(ct);
            if (compilation is null || MediatorTypes.For(compilation) is null)
                continue;

            foreach (var type in DeclaredTypes(compilation.Assembly.GlobalNamespace, ct))
            {
                if (MediatorSymbols.HandlerInterfacesOf(type)
                    .Any(h => MediatorSymbols.MessagesMatch(h.Message.Type, message.Type)))
                {
                    found.Add(type);
                }
            }
        }

        return found;
    }

    private static IEnumerable<INamedTypeSymbol> DeclaredTypes(INamespaceSymbol root, CancellationToken ct)
    {
        foreach (var member in root.GetMembers())
        {
            ct.ThrowIfCancellationRequested();

            switch (member)
            {
                case INamespaceSymbol nested:
                    foreach (var type in DeclaredTypes(nested, ct))
                        yield return type;
                    break;

                case INamedTypeSymbol type:
                    yield return type;
                    foreach (var nested in type.GetTypeMembers())
                        yield return nested;
                    break;
            }
        }
    }
}
