using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMCP.Services;

namespace RoslynMCP.Lsp.Completion;

/// <summary>Where a member came from, relative to the type completion is running on.</summary>
public enum MemberProvenance
{
    /// <summary>Not a member of that type at all — an extension method, or something unrelated.</summary>
    Unknown,

    /// <summary>Declared by the type itself.</summary>
    CurrentType,

    /// <summary>Inherited from a base type or interface.</summary>
    BaseType,

    /// <summary>Declared by <see cref="object"/>: ToString, GetHashCode, …</summary>
    Object,
}

/// <summary>
/// The two things the ranking needs that a Roslyn completion item does not carry: which type a
/// member is declared by, and which local was declared closest above the caret.
/// </summary>
/// <remarks>
/// Resolving each item's symbol would mean a SymbolKey resolution per item — hundreds per
/// keystroke. Instead the answer is computed once from the type being completed on (or the
/// enclosing type) and keyed by name, which is all the ranking compares anyway.
/// </remarks>
public sealed class CompletionSemanticContext
{
    public static readonly CompletionSemanticContext Empty = new(new Dictionary<string, MemberProvenance>(), null);

    private readonly Dictionary<string, MemberProvenance> _members;

    private CompletionSemanticContext(Dictionary<string, MemberProvenance> members, string? closestLocalName)
    {
        _members = members;
        ClosestLocalName = closestLocalName;
    }

    /// <summary>Name of the local variable declared nearest above the caret, if any.</summary>
    public string? ClosestLocalName { get; }

    /// <summary>Builds a context from already-known names, for tests.</summary>
    public static CompletionSemanticContext FromNames(
        IReadOnlyDictionary<string, MemberProvenance> members, string? closestLocalName = null) =>
        new(new Dictionary<string, MemberProvenance>(members, StringComparer.Ordinal), closestLocalName);

    public MemberProvenance ProvenanceOf(string name) =>
        _members.TryGetValue(name, out var provenance) ? provenance : MemberProvenance.Unknown;

    public static async Task<CompletionSemanticContext> CreateAsync(
        Document document, int position, CancellationToken ct)
    {
        var timing = RunwayTrace.Begin("completion ranking context");
        try
        {
            var semanticModel = await document.GetSemanticModelAsync(ct);
            timing?.Mark("get semantic model");
            var root = await document.GetSyntaxRootAsync(ct);
            timing?.Mark("get syntax root");
            if (semanticModel is null || root is null)
                return Empty;

            var members = new Dictionary<string, MemberProvenance>(StringComparer.Ordinal);
            var qualifierType = QualifierType(semanticModel, root, position, ct);
            timing?.Mark("bind qualifier type");

            if (qualifierType is not null)
            {
                // After a dot: everything in the list is a member of that type (or an extension).
                CollectMembers(qualifierType, members);
                timing?.Mark("collect qualifier members");
                return new CompletionSemanticContext(members, null);
            }

            if (semanticModel.GetEnclosingSymbol(position, ct)?.ContainingType is { } enclosingType)
                CollectMembers(enclosingType, members);

            var closestLocal = ClosestLocal(semanticModel, position, ct);
            timing?.Mark("collect enclosing members and closest local");
            return new CompletionSemanticContext(members, closestLocal);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Ranking without this is merely less precise; failing completion over it is not.
            return Empty;
        }
    }

    /// <summary>The type left of the dot, when completion was triggered on a member access.</summary>
    private static ITypeSymbol? QualifierType(
        SemanticModel semanticModel, SyntaxNode root, int position, CancellationToken ct)
    {
        if (position <= 0)
            return null;

        var token = root.FindToken(position - 1);
        if (!token.IsKind(SyntaxKind.DotToken))
        {
            // The caret sits inside the partially typed name; step back over it to the dot.
            token = token.GetPreviousToken();
            if (!token.IsKind(SyntaxKind.DotToken))
                return null;
        }

        ExpressionSyntax? qualifier = token.Parent switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Expression,
            MemberBindingExpressionSyntax binding =>
                (binding.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault())?.Expression,
            _ => null,
        };

        if (qualifier is null)
            return null;

        var typeInfo = semanticModel.GetTypeInfo(qualifier, ct);
        if (typeInfo.Type is { } type)
            return NullableUnwrapped(type);

        // Static access: the qualifier resolves to the type itself rather than to a value.
        return semanticModel.GetSymbolInfo(qualifier, ct).Symbol as ITypeSymbol;
    }

    private static ITypeSymbol NullableUnwrapped(ITypeSymbol type) =>
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            ? nullable.TypeArguments[0]
            : type;

    /// <summary>
    /// Names declared by the type, then by each base type, then by object. The most derived
    /// declaration wins, so an override is attributed to the type that overrides it.
    /// </summary>
    private static void CollectMembers(ITypeSymbol type, Dictionary<string, MemberProvenance> members)
    {
        bool isCurrent = true;
        for (ITypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            var provenance = current.SpecialType == SpecialType.System_Object
                ? MemberProvenance.Object
                : isCurrent ? MemberProvenance.CurrentType : MemberProvenance.BaseType;

            foreach (var member in current.GetMembers())
            {
                if (!member.CanBeReferencedByName)
                    continue;

                // An override belongs to whoever first declared it: string.ToString is still
                // object's ToString as far as "did I mean this?" goes.
                members.TryAdd(member.Name, IsRootedInObject(member) ? MemberProvenance.Object : provenance);
            }

            isCurrent = false;
        }

        // Interfaces have no base chain: their inherited members hang off AllInterfaces.
        if (type.TypeKind is TypeKind.Interface or TypeKind.TypeParameter)
        {
            foreach (var @interface in type.AllInterfaces)
            {
                foreach (var member in @interface.GetMembers())
                {
                    if (member.CanBeReferencedByName)
                        members.TryAdd(member.Name, MemberProvenance.BaseType);
                }
            }
        }
    }

    /// <summary>Whether the member ultimately overrides one declared by <see cref="object"/>.</summary>
    private static bool IsRootedInObject(ISymbol member)
    {
        for (ISymbol? current = member; current is not null; current = OverriddenBy(current))
        {
            if (!current.IsOverride)
                return current.ContainingType?.SpecialType == SpecialType.System_Object;
        }

        return false;
    }

    private static ISymbol? OverriddenBy(ISymbol member) => member switch
    {
        IMethodSymbol method => method.OverriddenMethod,
        IPropertySymbol property => property.OverriddenProperty,
        IEventSymbol @event => @event.OverriddenEvent,
        _ => null,
    };

    /// <summary>
    /// The local declared nearest above the caret — the one a "the thing I just made" completion
    /// is almost always after.
    /// </summary>
    private static string? ClosestLocal(SemanticModel semanticModel, int position, CancellationToken ct)
    {
        string? closest = null;
        int closestStart = -1;

        foreach (var symbol in semanticModel.LookupSymbols(position))
        {
            ct.ThrowIfCancellationRequested();
            if (symbol is not ILocalSymbol)
                continue;

            var reference = symbol.DeclaringSyntaxReferences.FirstOrDefault();
            if (reference is null)
                continue;

            int start = reference.Span.Start;
            if (start < position && start > closestStart)
            {
                closestStart = start;
                closest = symbol.Name;
            }
        }

        return closest;
    }
}
