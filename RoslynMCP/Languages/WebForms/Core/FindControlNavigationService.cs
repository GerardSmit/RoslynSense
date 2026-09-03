using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp;
using RoslynMCP.Services;
using WebFormsCore.Nodes;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Languages.WebForms.Core;

/// <summary>
/// Navigation from a <c>FindControl("id")</c> string literal to the markup <c>ID</c> it names.
/// </summary>
/// <remarks>
/// The literal is the only declaration-shaped thing a template-nested control has on the C# side:
/// no designer field is generated for a control inside a multi-instance template, so the string is
/// how the code refers to it. Which <c>ID</c> the string means depends on the naming container the
/// lookup runs in — <c>FindControl</c> inside <c>list_ItemDataBound</c> searches the item of the
/// control that <c>OnItemDataBound="list_ItemDataBound"</c> is written on — which is why resolution
/// is a ladder rather than a file-wide name match: the handler-wired control's subtree first, then
/// the pages the containing class is the code-behind of, then the whole project.
/// </remarks>
internal static class FindControlNavigationService
{
    /// <summary>Whether the compilation can host WebForms at all, memoized per compilation
    /// because the detection pass asks per string literal.</summary>
    private static readonly ConditionalWeakTable<Compilation, object> s_webFormsGate = new();

    /// <summary>
    /// The synchronous core of <see cref="IConfiguredStringLanguage.Detect"/>: whether the token
    /// is the control-ID argument of <c>FindControl</c> or of a discovered wrapper.
    /// </summary>
    /// <remarks>
    /// Syntax first: a literal that is not directly an invocation argument — one half of a
    /// concatenation, a switch arm — is rejected before anything binds, which is also what keeps
    /// computed IDs out by construction. Wrapper names come from
    /// <see cref="FindControlWrapperRegistry"/>'s synchronous snapshot; when none has been
    /// published yet the claim is declined and a scan is started, so the miss repairs itself.
    /// </remarks>
    public static bool IsFindControlIdLiteral(
        SyntaxToken token, SemanticModel semanticModel, CancellationToken ct)
    {
        if (token.ValueText.Length == 0
            || Invocation(token) is not var (invocation, argumentIndex)
            || AspxSourceMappingService.GetInvocationMemberName(invocation) is not { } memberName)
        {
            return false;
        }

        if (IsDirectFindControl(memberName, argumentIndex))
            return IsWebFormsCompilation(semanticModel.Compilation);

        var wrappers = FindControlWrapperRegistry.Snapshot(semanticModel.Compilation);
        if (wrappers.IsEmpty)
        {
            if (IsWebFormsCompilation(semanticModel.Compilation))
                FindControlWrapperRegistry.EnsureWarm(semanticModel.Compilation);
            return false;
        }

        return MatchesWrapper(wrappers, memberName, argumentIndex, invocation)
            && IsWebFormsCompilation(semanticModel.Compilation);
    }

    /// <summary>
    /// The markup <c>ID</c> declarations a claimed literal names, resolved through the scoping
    /// ladder. Empty when the literal turns out not to be a control ID after all.
    /// </summary>
    public static async Task<LspLocation[]> DefinitionsAsync(
        EmbeddedStringContext context, CancellationToken ct)
    {
        string controlId = context.Token.ValueText;

        if (controlId.Length == 0
            || Invocation(context.Token) is not var (invocation, argumentIndex)
            || AspxSourceMappingService.GetInvocationMemberName(invocation) is not { } memberName)
        {
            return [];
        }

        // Detect answered from a snapshot; this path can wait, so it re-validates against the
        // authoritative list — a stale snapshot may cost a claim, never a wrong answer.
        if (!IsDirectFindControl(memberName, argumentIndex))
        {
            var wrappers = await ProjectIndexCacheService.GetFindControlWrappersAsync(
                context.Document.Project, ct);

            if (!MatchesWrapper(wrappers, memberName, argumentIndex, invocation))
                return [];
        }

        var files = await WebFormsIndex.ForProjectAsync(context.Document.Project, ct);
        if (files.Count == 0)
            return [];

        var containingType = invocation.AncestorsAndSelf()
                .OfType<TypeDeclarationSyntax>().FirstOrDefault() is { } typeDeclaration
            ? context.SemanticModel.GetDeclaredSymbol(typeDeclaration, ct)
            : null;

        var candidates = containingType is null
            ? []
            : files.Where(file => InheritsMatches(file, containingType)).ToList();

        string? methodName = invocation.AncestorsAndSelf()
            .OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.Text;

        if (methodName is not null
            && await HandlerScopedAsync(candidates, methodName, controlId, ct) is { Length: > 0 } scoped)
        {
            return scoped;
        }

        // The containing class's own page(s) — several markup files may share one code-behind.
        // This is the scope for a FindControl outside any handler, and the fallback for a handler
        // whose owner control does not declare the ID after all.
        if (Declarations(candidates, controlId) is { Length: > 0 } inOwnPages)
            return inOwnPages;

        return Declarations(files, controlId);
    }

    /// <summary>
    /// The declarations inside the subtrees of the controls whose <c>On…</c> attributes name the
    /// containing method — the naming containers the lookup actually runs in at runtime.
    /// </summary>
    private static async Task<LspLocation[]> HandlerScopedAsync(
        IReadOnlyList<WebFormsFileIndex> candidates, string methodName, string controlId,
        CancellationToken ct)
    {
        var results = new List<LspLocation>();

        foreach (var file in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var ownerIds = file.Handlers
                .Where(handler =>
                    handler.OwnerControlId is not null
                    && string.Equals(handler.MethodName, methodName, StringComparison.Ordinal))
                .Select(handler => handler.OwnerControlId!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (ownerIds.Count == 0)
                continue;

            if (await AspxDocumentService.GetAsync(file.FilePath, ct) is not { Tree: { } root })
                continue;

            var controls = AspxSymbolResolver.EnumerateControls(root).ToList();

            foreach (var owner in controls.Where(control =>
                         control.Id is { } id
                         && ownerIds.Contains(id, StringComparer.OrdinalIgnoreCase)))
            {
                foreach (var control in controls)
                {
                    if (string.Equals(control.Id, controlId, StringComparison.OrdinalIgnoreCase)
                        && IsDescendantOf(control, owner)
                        && IdAttributeSpan(control) is { } span)
                    {
                        results.Add(new LspLocation(
                            LspConverters.PathToUri(file.FilePath), LspConverters.ToRange(span)));
                    }
                }
            }
        }

        return [.. results.Distinct()];
    }

    /// <summary>Every <c>ID</c> declaration matching <paramref name="controlId"/> across
    /// <paramref name="files"/>, from the index alone — no parse needed.</summary>
    private static LspLocation[] Declarations(
        IReadOnlyList<WebFormsFileIndex> files, string controlId) =>
        [.. files
            .SelectMany(file => file.Controls
                .Where(control =>
                    string.Equals(control.Id, controlId, StringComparison.OrdinalIgnoreCase))
                .Select(control => new LspLocation(
                    LspConverters.PathToUri(file.FilePath), LspConverters.ToRange(control.Span))))
            .Distinct()];

    /// <summary>The invocation the token is a direct argument of, with the argument's index —
    /// or null, which is what a computed ID (concatenation, switch arm) resolves to.</summary>
    private static (InvocationExpressionSyntax Invocation, int ArgumentIndex)? Invocation(
        SyntaxToken token)
    {
        if (token.Parent is LiteralExpressionSyntax
            {
                Parent: ArgumentSyntax
                {
                    Parent: ArgumentListSyntax
                    {
                        Parent: InvocationExpressionSyntax invocation
                    } argumentList
                } argument
            })
        {
            return (invocation, argumentList.Arguments.IndexOf(argument));
        }

        return null;
    }

    private static bool IsDirectFindControl(string memberName, int argumentIndex) =>
        argumentIndex == 0 && string.Equals(memberName, "FindControl", StringComparison.Ordinal);

    private static bool MatchesWrapper(
        IReadOnlyList<(string MethodName, int ParamIndex, bool IsExtension)> wrappers,
        string memberName, int argumentIndex, InvocationExpressionSyntax invocation)
    {
        foreach (var (wrapperName, paramIndex, isExtension) in wrappers)
        {
            if (!string.Equals(wrapperName, memberName, StringComparison.Ordinal))
                continue;

            // Extension methods called receiver-style don't spell the 'this' argument, so the
            // declared parameter index sits one to the right of the written argument's.
            int effectiveIndex = isExtension && invocation.Expression is MemberAccessExpressionSyntax
                ? paramIndex - 1
                : paramIndex;

            if (effectiveIndex == argumentIndex)
                return true;
        }

        return false;
    }

    private static bool InheritsMatches(WebFormsFileIndex file, INamedTypeSymbol type)
    {
        if (file.Inherits is not { Length: > 0 })
            return false;

        // An Inherits with no namespace can only be compared by name; one with a namespace is
        // compared whole, which is what keeps same-named pages in different folders apart.
        return file.InheritsNamespace is null
            ? string.Equals(file.InheritsName, type.Name, StringComparison.Ordinal)
            : string.Equals(file.Inherits, type.ToDisplayString(), StringComparison.Ordinal);
    }

    private static bool IsDescendantOf(Node node, ControlNode owner)
    {
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            if (ReferenceEquals(parent, owner))
                return true;
        }

        return false;
    }

    private static LinePositionSpan? IdAttributeSpan(ControlNode control) =>
        control.RawAttributes.TryGetValue("ID", out var id) ? id.Range : null;

    private static bool IsWebFormsCompilation(Compilation compilation) =>
        (bool)s_webFormsGate.GetValue(
            compilation,
            static c => c.GetTypeByMetadataName("System.Web.UI.Control") is not null
                || c.GetTypeByMetadataName("WebFormsCore.UI.Control") is not null);
}
