using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.Mediator.Core;

/// <summary>How a call site reaches its handler.</summary>
internal enum MediatorDispatchKind
{
    Send,
    Publish,
    CreateStream,
    GeneratedExtension,
}

/// <summary>One call site that dispatches to a handler.</summary>
/// <remarks>
/// A Roslyn <see cref="Microsoft.CodeAnalysis.Location"/> rather than a path and a span, because
/// unlike a markup hit every mediator dispatch is in a C# document the workspace already has open —
/// there is nothing to re-resolve, and the generated-document machinery downstream needs the real
/// location to turn it into something an editor can open.
/// </remarks>
internal sealed record MediatorDispatchSite(
    Location Location, MediatorDispatchKind Kind, string LineText, string? ContainingMember)
{
    public string FilePath => Location.SourceTree?.FilePath ?? Location.GetLineSpan().Path;

    /// <summary>1-based, as a reader counts them.</summary>
    public int Line => Location.GetLineSpan().StartLinePosition.Line + 1;
}

/// <summary>
/// From a handler to the call sites that reach it. The half of the pack Shift+F12 goes through.
/// </summary>
internal static class MediatorReferenceService
{
    /// <summary>The cheap decline: whether this project hosts a mediator at all.</summary>
    public static async Task<bool> UsesMediatorAsync(Project project, CancellationToken ct)
    {
        var compilation = await project.GetCompilationAsync(ct);
        return compilation is not null && MediatorTypes.For(compilation) is not null;
    }

    /// <summary>
    /// Every dispatch that reaches <paramref name="symbol"/>, or nothing when the symbol is neither
    /// a handler nor a message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What is reported depends on which end the caller started from, and the difference is not
    /// cosmetic — the LSP folds this into Roslyn's own answer and de-duplicates structurally, so a
    /// hit whose span differs from Roslyn's for the same reference appears twice rather than
    /// merging.
    /// </para>
    /// <para>
    /// From a handler, everything: Roslyn sees none of it. From a message, only the calls through a
    /// generated extension method, because <c>Send(new CreateUserRequest())</c> names the type and
    /// is already in Roslyn's answer — while <c>mediator.CreateUserAsync("bob")</c>, the overload
    /// taking the request's constructor arguments, names it nowhere at all.
    /// </para>
    /// </remarks>
    public static async Task<IReadOnlyList<MediatorDispatchSite>> FindAsync(
        ISymbol symbol, Project project, CancellationToken ct, TimeSpan? scopeBudget = null)
    {
        if (symbol.Kind is not (SymbolKind.NamedType or SymbolKind.Method))
            return [];

        var compilation = await project.GetCompilationAsync(ct);
        if (compilation is null || MediatorTypes.For(compilation) is not { } types)
            return [];

        if (!TryGetSubject(symbol, types, out var messages, out bool handlerSide))
            return [];

        // The dispatch sites live in projects that reference the message's project, which lazy
        // loading never followed. Only the caller that may wait passes a budget — Shift+F12 —
        // while a code lens resolving on scroll searches what is open, exactly like the C# side.
        if (scopeBudget is { } budget)
        {
            foreach (var message in messages.Distinct())
            {
                var solution = await SearchScopeService.WidenForSymbolAsync(
                    message.Type, project, budget, ct);
                project = solution.GetProject(project.Id) ?? project;
            }
        }

        var sites = new List<MediatorDispatchSite>();
        var seen = new HashSet<(DocumentId, TextSpan)>();
        var models = new Dictionary<DocumentId, SemanticModel?>();

        foreach (var message in messages.Distinct())
        {
            var generated = new List<IMethodSymbol>();

            await CollectFromMessageAsync(
                message, project, types, handlerSide, sites, seen, models, generated, ct);

            // Nothing found where the generator's output should be. Either this project has no
            // generator, or the reference search cannot see generated documents — and the second
            // would silently cost every Zapto call site, so fall back to finding the class by name
            // rather than reporting a short answer as a complete one.
            if (generated.Count == 0)
                generated.AddRange(GeneratedByName(compilation, message));

            await CollectFromGeneratedAsync(generated, project, sites, seen, ct);
        }

        return sites;
    }

    /// <summary>
    /// The messages a search should run on, and whether the caller is on the handler side.
    /// </summary>
    private static bool TryGetSubject(
        ISymbol symbol, MediatorTypes types,
        out ImmutableArray<MediatorMessage> messages, out bool handlerSide)
    {
        messages = [];
        handlerSide = false;

        switch (symbol)
        {
            case INamedTypeSymbol type:
            {
                var handled = MediatorSymbols.HandlerInterfacesOf(type);
                if (handled.Length > 0)
                {
                    messages = [.. handled.Select(h => h.Message)];
                    handlerSide = true;
                    return true;
                }

                if (MediatorSymbols.TryGetMessage(type, out var message))
                {
                    messages = [message];
                    return true;
                }

                return false;
            }

            case IMethodSymbol method when MediatorSymbols.IsHandleMethod(method, types):
            {
                var handled = MediatorSymbols.HandlerInterfacesOf(method.ContainingType);
                if (handled.Length == 0)
                    return false;

                messages = [.. handled.Select(h => h.Message)];
                handlerSide = true;
                return true;
            }

            default:
                return false;
        }
    }

    private static async Task CollectFromMessageAsync(
        MediatorMessage message, Project project, MediatorTypes types, bool handlerSide,
        List<MediatorDispatchSite> sites, HashSet<(DocumentId, TextSpan)> seen,
        Dictionary<DocumentId, SemanticModel?> models, List<IMethodSymbol> generated,
        CancellationToken ct)
    {
        var references = await SymbolFinder.FindReferencesAsync(
            message.Type.OriginalDefinition, project.Solution, ct);

        foreach (var referenced in references)
        {
            foreach (var location in referenced.Locations)
            {
                ct.ThrowIfCancellationRequested();

                var document = location.Document;
                var root = await document.GetSyntaxRootAsync(ct);
                var model = await ModelForAsync(models, document, ct);
                if (root is null || model is null)
                    continue;

                var node = root.FindNode(
                    location.Location.SourceSpan, findInsideTrivia: true, getInnermostNodeForTie: true);

                // The generated body naming its own message: not a usage, but the way to the call
                // sites that go through it.
                if (node.FirstAncestorOrSelf<MethodDeclarationSyntax>() is { } enclosing
                    && model.GetDeclaredSymbol(enclosing, ct) is IMethodSymbol declared
                    && MediatorSymbols.IsGeneratedSenderExtension(declared))
                {
                    generated.Add(declared);
                    continue;
                }

                // The generated registration names every handler once. Reporting it as a usage
                // would put one meaningless hit in front of every real one.
                if (MediatorSymbols.IsGeneratedRegistration(model.GetEnclosingSymbol(node.SpanStart, ct)))
                    continue;

                // A <see cref="…"/>: Roslyn already reports it, and reporting it again with a
                // different span would show it twice.
                if (node.FirstAncestorOrSelf<CrefSyntax>() is not null)
                    continue;

                if (!handlerSide)
                    continue;

                if (DispatchInvocationAround(node) is not { } invocation)
                    continue;

                if (await MediatorNavigationService.MessageOfAsync(model, invocation, project, types, ct)
                        is not { } dispatched
                    || !MediatorSymbols.MessagesMatch(dispatched.Type, message.Type))
                {
                    continue;
                }

                await AddSiteAsync(
                    sites, seen, document, invocation.Span, KindOf(invocation), ct);
            }
        }
    }

    private static async Task CollectFromGeneratedAsync(
        List<IMethodSymbol> generated, Project project,
        List<MediatorDispatchSite> sites, HashSet<(DocumentId, TextSpan)> seen, CancellationToken ct)
    {
        foreach (var method in generated.Distinct<IMethodSymbol>(SymbolEqualityComparer.Default))
        {
            ct.ThrowIfCancellationRequested();

            var references = await SymbolFinder.FindReferencesAsync(
                method.OriginalDefinition, project.Solution, ct);

            foreach (var referenced in references)
            {
                foreach (var location in referenced.Locations)
                {
                    var root = await location.Document.GetSyntaxRootAsync(ct);
                    if (root is null)
                        continue;

                    var node = root.FindNode(
                        location.Location.SourceSpan, getInnermostNodeForTie: true);

                    // The generator's own overloads call one another; only calls from outside the
                    // class are somebody's dispatch.
                    if (node.Ancestors().OfType<TypeDeclarationSyntax>()
                        .Any(t => t.Identifier.ValueText == MediatorTypes.SenderExtensionsName))
                    {
                        continue;
                    }

                    var span = node.FirstAncestorOrSelf<InvocationExpressionSyntax>()?.Span
                        ?? location.Location.SourceSpan;

                    await AddSiteAsync(
                        sites, seen, location.Document, span,
                        MediatorDispatchKind.GeneratedExtension, ct);
                }
            }
        }
    }

    /// <summary>
    /// The invocation a reference sits in an argument or type argument of.
    /// </summary>
    /// <remarks>
    /// The whole invocation is the span, not the identifier: <c>Send(new CreateUserRequest(id))</c>
    /// reads as a dispatch and <c>CreateUserRequest</c> on its own does not. It is also what makes
    /// the de-duplication work, since the call reached two ways is one call.
    /// </remarks>
    private static InvocationExpressionSyntax? DispatchInvocationAround(SyntaxNode node)
    {
        if (node.FirstAncestorOrSelf<ArgumentSyntax>() is
            { Parent.Parent: InvocationExpressionSyntax fromArgument })
        {
            return fromArgument;
        }

        if (node.FirstAncestorOrSelf<TypeArgumentListSyntax>() is { Parent: GenericNameSyntax generic })
        {
            return generic.Parent switch
            {
                MemberAccessExpressionSyntax access => access.Parent as InvocationExpressionSyntax,
                MemberBindingExpressionSyntax binding => binding.Parent as InvocationExpressionSyntax,
                InvocationExpressionSyntax direct => direct,
                _ => null,
            };
        }

        return null;
    }

    private static MediatorDispatchKind KindOf(InvocationExpressionSyntax invocation)
    {
        string name = invocation.Expression switch
        {
            MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
            SimpleNameSyntax simple => simple.Identifier.ValueText,
            _ => string.Empty,
        };

        return name switch
        {
            "Publish" => MediatorDispatchKind.Publish,
            "CreateStream" => MediatorDispatchKind.CreateStream,
            _ => MediatorDispatchKind.Send,
        };
    }

    private static async Task AddSiteAsync(
        List<MediatorDispatchSite> sites, HashSet<(DocumentId, TextSpan)> seen,
        Document document, TextSpan span, MediatorDispatchKind kind, CancellationToken ct)
    {
        if (!seen.Add((document.Id, span)))
            return;

        var tree = await document.GetSyntaxTreeAsync(ct);
        var text = await document.GetTextAsync(ct);
        if (tree is null)
            return;

        var root = await tree.GetRootAsync(ct);

        sites.Add(new MediatorDispatchSite(
            Location.Create(tree, span),
            kind,
            text.Lines.GetLineFromPosition(span.Start).ToString().Trim(),
            ContainingMemberOf(root.FindNode(span))));
    }

    /// <summary>
    /// The member the call is written in, read off the tree rather than bound. It names the caller
    /// in a call hierarchy and labels the row in a report, and neither is worth a semantic model
    /// the search would otherwise not need.
    /// </summary>
    private static string? ContainingMemberOf(SyntaxNode node)
    {
        foreach (var ancestor in node.AncestorsAndSelf())
        {
            switch (ancestor)
            {
                case MethodDeclarationSyntax method:
                    return method.Identifier.ValueText;
                case LocalFunctionStatementSyntax local:
                    return local.Identifier.ValueText;
                case ConstructorDeclarationSyntax constructor:
                    return constructor.Identifier.ValueText;
                case PropertyDeclarationSyntax property:
                    return property.Identifier.ValueText;
                case BaseTypeDeclarationSyntax type:
                    return type.Identifier.ValueText;
            }
        }

        return null;
    }

    private static async Task<SemanticModel?> ModelForAsync(
        Dictionary<DocumentId, SemanticModel?> models, Document document, CancellationToken ct)
    {
        if (models.TryGetValue(document.Id, out var cached))
            return cached;

        var model = await document.GetSemanticModelAsync(ct);
        models[document.Id] = model;
        return model;
    }

    /// <summary>
    /// The generated extension methods for a message, found by name rather than by reading what
    /// they contain.
    /// </summary>
    /// <remarks>
    /// The fallback, never the first answer. The generator emits <c>SenderExtensions</c> into the
    /// <em>message's</em> namespace, so two assemblies whose messages share a namespace both
    /// declare the class — which is why the lookup is the plural one, the singular overload
    /// answering null when more than one candidate exists. And a name is not proof: a request and a
    /// notification with the same stem compute to the same method name, so each candidate still has
    /// to name the message in its own signature or be the shape the rule predicts.
    /// </remarks>
    private static IEnumerable<IMethodSymbol> GeneratedByName(
        Compilation compilation, MediatorMessage message)
    {
        string? ns = MediatorSymbols.NamespaceOf(message.Type);
        string metadataName = ns is null
            ? MediatorTypes.SenderExtensionsName
            : $"{ns}.{MediatorTypes.SenderExtensionsName}";

        string awaited = MediatorSymbols.ComputeExtensionName(message.Kind, message.Type.Name, voidReturn: false);
        string immediate = MediatorSymbols.ComputeExtensionName(message.Kind, message.Type.Name, voidReturn: true);

        foreach (var extensions in compilation.GetTypesByMetadataName(metadataName))
        {
            foreach (var candidate in extensions.GetMembers().OfType<IMethodSymbol>())
            {
                if (!MediatorSymbols.IsGeneratedSenderExtension(candidate))
                    continue;

                bool namesTheMessage = candidate.Parameters
                    .Skip(1)
                    .Any(p => MediatorSymbols.MessagesMatch(p.Type, message.Type));

                if (namesTheMessage || candidate.Name == awaited || candidate.Name == immediate)
                    yield return candidate;
            }
        }
    }
}
