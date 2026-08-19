using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMCP.Services.ExternalSource;

/// <summary>
/// Finds where a metadata symbol is declared inside a C# file that Roslyn did not compile — a
/// decompilation, a file fetched from Source Link, or one pulled from the reference source.
/// </summary>
/// <remarks>
/// <para>
/// The problem all three share is that the symbol and the text come from different worlds: there
/// is no shared compilation to bind through, so the match has to be made on what the declaration
/// looks like. Two strengths are offered. The semantic one binds the file and compares real
/// symbols, and is what decompiled output gets, because that output was generated from the very
/// metadata being matched and so agrees with it exactly. The syntactic one compares identifiers,
/// arity and how the parameter types are spelled, and is what hand-written source gets, because a
/// human wrote <c>int</c> where the symbol says <c>System.Int32</c> and neither is wrong.
/// </para>
/// </remarks>
internal static class SourceMemberLocator
{
    private static readonly SymbolDisplayFormat s_typeMatchDisplayFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.OmittedAsContaining,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>The identifier position of the type a file was produced for.</summary>
    public static (int Line, int Character) FindTypeDeclaration(
        string sourceText, string reflectionTypeName, CancellationToken cancellationToken)
    {
        var (simpleName, arity) = SplitReflectionName(reflectionTypeName);

        var root = CSharpSyntaxTree.ParseText(sourceText, cancellationToken: cancellationToken)
            .GetRoot(cancellationToken);

        SyntaxToken? nameOnlyMatch = null;
        foreach (var node in root.DescendantNodes())
        {
            var (identifier, candidateArity) = node switch
            {
                BaseTypeDeclarationSyntax type when type.Identifier.Text == simpleName =>
                    ((SyntaxToken?)type.Identifier, GetTypeParameterCount(type)),
                DelegateDeclarationSyntax del when del.Identifier.Text == simpleName =>
                    (del.Identifier, del.TypeParameterList?.Parameters.Count ?? 0),
                _ => (null, 0),
            };

            if (identifier is not { } token)
                continue;

            if (candidateArity == arity)
            {
                var position = token.GetLocation().GetLineSpan().StartLinePosition;
                return (position.Line, position.Character);
            }

            nameOnlyMatch ??= token;
        }

        if (nameOnlyMatch is { } fallback)
        {
            var position = fallback.GetLocation().GetLineSpan().StartLinePosition;
            return (position.Line, position.Character);
        }

        return (0, 0);
    }

    /// <summary>
    /// "Ns.Outer`1+Inner`2" declares its innermost type as "Inner" with two type parameters.
    /// Arity has to take part in the match: a container can declare both <c>Result</c> and
    /// <c>Result&lt;T&gt;</c>.
    /// </summary>
    public static (string SimpleName, int Arity) SplitReflectionName(string reflectionTypeName)
    {
        string simpleName = reflectionTypeName;
        int lastSeparator = simpleName.LastIndexOfAny(['+', '.']);
        if (lastSeparator >= 0)
            simpleName = simpleName[(lastSeparator + 1)..];

        int arity = 0;
        int backtick = simpleName.IndexOf('`');
        if (backtick >= 0)
        {
            _ = int.TryParse(simpleName[(backtick + 1)..], out arity);
            simpleName = simpleName[..backtick];
        }

        return (simpleName, arity);
    }

    /// <summary>The namespace part of a reflection type name, or empty for the global namespace.</summary>
    public static string NamespaceOf(string reflectionTypeName)
    {
        // Only the part before the first nested-type separator can carry a namespace.
        int nested = reflectionTypeName.IndexOf('+');
        string topLevel = nested < 0 ? reflectionTypeName : reflectionTypeName[..nested];

        int lastDot = topLevel.LastIndexOf('.');
        return lastDot < 0 ? string.Empty : topLevel[..lastDot];
    }

    /// <summary>The symbol in <paramref name="document"/> that is this metadata symbol, if any.</summary>
    public static async Task<ISymbol?> FindMatchingSourceSymbolAsync(
        Document document,
        ISymbol originalSymbol,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root is null || semanticModel is null)
            return null;

        foreach (var candidate in EnumerateDeclaredSymbols(root, semanticModel, cancellationToken))
        {
            if (SymbolsMatch(candidate, originalSymbol))
                return candidate;
        }

        return null;
    }

    /// <summary>Where this symbol appears to be declared, matched on syntax alone.</summary>
    public static async Task<IReadOnlyList<Location>> FindMatchingLocationsBySyntaxAsync(
        Document document,
        ISymbol originalSymbol,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        return root is null ? [] : FindLocations(root, originalSymbol);
    }

    /// <summary>
    /// Where this symbol appears to be declared in an already-parsed tree.
    /// </summary>
    /// <param name="requireMatchingNamespace">
    /// Rejects a declaration whose enclosing namespace is not the symbol's. Off by default because
    /// a decompiled file contains exactly one type and the question does not arise; on for a file
    /// picked out of a repository by name, where <c>Timer</c> in the wrong namespace is a real and
    /// likely candidate.
    /// </param>
    public static IReadOnlyList<Location> FindLocations(
        SyntaxNode root, ISymbol symbol, bool requireMatchingNamespace = false)
    {
        var locations = symbol switch
        {
            IMethodSymbol method => FindMethodLocations(root, method),
            IPropertySymbol property => FindPropertyLocations(root, property),
            IFieldSymbol field => FindFieldLocations(root, field),
            IEventSymbol @event => FindEventLocations(root, @event),
            INamedTypeSymbol type => FindTypeLocations(root, type),
            _ => [],
        };

        if (!requireMatchingNamespace || locations.Count == 0)
            return locations;

        string expected = symbol.ContainingNamespace is { IsGlobalNamespace: false } ns
            ? ns.ToDisplayString()
            : string.Empty;

        return [.. locations.Where(location => DeclaredNamespaceAt(root, location) == expected)];
    }

    /// <summary>The namespace a location sits in, as written in the file.</summary>
    private static string DeclaredNamespaceAt(SyntaxNode root, Location location)
    {
        var node = root.FindNode(location.SourceSpan, getInnermostNodeForTie: true);

        var parts = new Stack<string>();
        for (var current = node; current is not null; current = current.Parent)
        {
            if (current is BaseNamespaceDeclarationSyntax declaration)
                parts.Push(declaration.Name.ToString());
        }

        return string.Join(".", parts);
    }

    private static IEnumerable<ISymbol> EnumerateDeclaredSymbols(
        SyntaxNode root,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        foreach (var node in root.DescendantNodesAndSelf())
        {
            cancellationToken.ThrowIfCancellationRequested();

            ISymbol? symbol = node switch
            {
                MemberDeclarationSyntax member => semanticModel.GetDeclaredSymbol(member, cancellationToken),
                VariableDeclaratorSyntax variable when variable.Parent?.Parent is BaseFieldDeclarationSyntax =>
                    semanticModel.GetDeclaredSymbol(variable, cancellationToken),
                _ => null
            };

            if (symbol is not null)
                yield return symbol;
        }
    }

    private static IReadOnlyList<Location> FindMethodLocations(SyntaxNode root, IMethodSymbol method)
    {
        if (method.MethodKind == MethodKind.Constructor)
        {
            return root.DescendantNodes()
                .OfType<ConstructorDeclarationSyntax>()
                .Where(candidate => ParametersLookCompatible(candidate.ParameterList.Parameters, method.Parameters))
                .Select(candidate => candidate.Identifier.GetLocation())
                .ToList();
        }

        return root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(candidate =>
                string.Equals(candidate.Identifier.ValueText, method.Name, StringComparison.Ordinal) &&
                ParametersLookCompatible(candidate.ParameterList.Parameters, method.Parameters))
            .Select(candidate => candidate.Identifier.GetLocation())
            .ToList();
    }

    private static IReadOnlyList<Location> FindPropertyLocations(SyntaxNode root, IPropertySymbol property)
    {
        var locations = root.DescendantNodes()
            .OfType<PropertyDeclarationSyntax>()
            .Where(candidate =>
                string.Equals(candidate.Identifier.ValueText, property.Name, StringComparison.Ordinal) &&
                TypesLookCompatible(candidate.Type, property.Type))
            .Select(candidate => candidate.Identifier.GetLocation())
            .ToList();

        if (locations.Count > 0)
            return locations;

        return root.DescendantNodes()
            .OfType<IndexerDeclarationSyntax>()
            .Where(candidate => ParametersLookCompatible(candidate.ParameterList.Parameters, property.Parameters))
            .Select(candidate => candidate.ThisKeyword.GetLocation())
            .ToList();
    }

    private static IReadOnlyList<Location> FindFieldLocations(SyntaxNode root, IFieldSymbol field) =>
        root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(candidate =>
                candidate.Parent?.Parent is FieldDeclarationSyntax declaration &&
                string.Equals(candidate.Identifier.ValueText, field.Name, StringComparison.Ordinal) &&
                TypesLookCompatible(declaration.Declaration.Type, field.Type))
            .Select(candidate => candidate.Identifier.GetLocation())
            .ToList();

    private static IReadOnlyList<Location> FindEventLocations(SyntaxNode root, IEventSymbol @event)
    {
        var eventLocations = root.DescendantNodes()
            .OfType<EventDeclarationSyntax>()
            .Where(candidate =>
                string.Equals(candidate.Identifier.ValueText, @event.Name, StringComparison.Ordinal) &&
                TypesLookCompatible(candidate.Type, @event.Type))
            .Select(candidate => candidate.Identifier.GetLocation())
            .ToList();

        if (eventLocations.Count > 0)
            return eventLocations;

        return root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(candidate =>
                candidate.Parent?.Parent is EventFieldDeclarationSyntax declaration &&
                string.Equals(candidate.Identifier.ValueText, @event.Name, StringComparison.Ordinal) &&
                TypesLookCompatible(declaration.Declaration.Type, @event.Type))
            .Select(candidate => candidate.Identifier.GetLocation())
            .ToList();
    }

    private static IReadOnlyList<Location> FindTypeLocations(SyntaxNode root, INamedTypeSymbol type)
    {
        var locations = root.DescendantNodes()
            .OfType<BaseTypeDeclarationSyntax>()
            .Where(candidate =>
                string.Equals(candidate.Identifier.ValueText, type.Name, StringComparison.Ordinal) &&
                GetTypeParameterCount(candidate) == type.Arity)
            .Select(candidate => candidate.Identifier.GetLocation())
            .ToList();

        if (locations.Count > 0)
            return locations;

        return root.DescendantNodes()
            .OfType<DelegateDeclarationSyntax>()
            .Where(candidate =>
                string.Equals(candidate.Identifier.ValueText, type.Name, StringComparison.Ordinal) &&
                candidate.TypeParameterList?.Parameters.Count == type.Arity)
            .Select(candidate => candidate.Identifier.GetLocation())
            .ToList();
    }

    private static bool SymbolsMatch(ISymbol candidate, ISymbol original)
    {
        if (candidate.Kind != original.Kind)
            return false;

        if (!string.Equals(
            GetContainingTypeIdentity(candidate),
            GetContainingTypeIdentity(original),
            StringComparison.Ordinal))
        {
            return false;
        }

        return (candidate, original) switch
        {
            (INamedTypeSymbol candidateType, INamedTypeSymbol originalType) =>
                string.Equals(
                    GetReflectionTypeName(candidateType),
                    GetReflectionTypeName(originalType),
                    StringComparison.Ordinal),
            (IMethodSymbol candidateMethod, IMethodSymbol originalMethod) =>
                MethodsMatch(candidateMethod, originalMethod),
            (IPropertySymbol candidateProperty, IPropertySymbol originalProperty) =>
                MembersMatch(candidateProperty, originalProperty) &&
                ParametersMatch(candidateProperty.Parameters, originalProperty.Parameters) &&
                TypesMatch(candidateProperty.Type, originalProperty.Type),
            (IFieldSymbol candidateField, IFieldSymbol originalField) =>
                MembersMatch(candidateField, originalField) &&
                TypesMatch(candidateField.Type, originalField.Type),
            (IEventSymbol candidateEvent, IEventSymbol originalEvent) =>
                MembersMatch(candidateEvent, originalEvent) &&
                TypesMatch(candidateEvent.Type, originalEvent.Type),
            _ => false
        };
    }

    private static bool MethodsMatch(IMethodSymbol candidate, IMethodSymbol original)
    {
        if (!MembersMatch(candidate, original) ||
            candidate.MethodKind != original.MethodKind ||
            candidate.Arity != original.Arity ||
            !ParametersMatch(candidate.Parameters, original.Parameters))
        {
            return false;
        }

        return candidate.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor
            ? true
            : TypesMatch(candidate.ReturnType, original.ReturnType);
    }

    private static bool MembersMatch(ISymbol candidate, ISymbol original) =>
        string.Equals(candidate.MetadataName, original.MetadataName, StringComparison.Ordinal);

    private static bool ParametersMatch(
        ImmutableArray<IParameterSymbol> candidateParameters,
        ImmutableArray<IParameterSymbol> originalParameters)
    {
        if (candidateParameters.Length != originalParameters.Length)
            return false;

        for (int i = 0; i < candidateParameters.Length; i++)
        {
            if (candidateParameters[i].RefKind != originalParameters[i].RefKind ||
                !TypesMatch(candidateParameters[i].Type, originalParameters[i].Type))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ParametersLookCompatible(
        SeparatedSyntaxList<ParameterSyntax> candidateParameters,
        ImmutableArray<IParameterSymbol> originalParameters)
    {
        if (candidateParameters.Count != originalParameters.Length)
            return false;

        for (int i = 0; i < candidateParameters.Count; i++)
        {
            var candidateParameter = candidateParameters[i];
            var originalParameter = originalParameters[i];

            if (!ModifiersLookCompatible(candidateParameter.Modifiers, originalParameter.RefKind))
                return false;

            if (!TypesLookCompatible(candidateParameter.Type, originalParameter.Type))
                return false;
        }

        return true;
    }

    private static bool TypesMatch(ITypeSymbol candidate, ITypeSymbol original)
    {
        if (SymbolEqualityComparer.Default.Equals(candidate, original))
            return true;

        return string.Equals(
            candidate.ToDisplayString(s_typeMatchDisplayFormat),
            original.ToDisplayString(s_typeMatchDisplayFormat),
            StringComparison.Ordinal);
    }

    private static bool TypesLookCompatible(TypeSyntax? candidateType, ITypeSymbol originalType)
    {
        if (candidateType is null)
            return false;

        string candidateText = NormalizeTypeText(candidateType.ToString());
        var expectedTexts = GetExpectedTypeTexts(originalType);
        return expectedTexts.Contains(candidateText);
    }

    private static HashSet<string> GetExpectedTypeTexts(ITypeSymbol type)
    {
        var texts = new HashSet<string>(StringComparer.Ordinal)
        {
            NormalizeTypeText(type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)),
            NormalizeTypeText(type.ToDisplayString(s_typeMatchDisplayFormat))
        };

        if (type is INamedTypeSymbol namedType)
        {
            texts.Add(NormalizeTypeText(namedType.Name));

            if (!namedType.ContainingNamespace.IsGlobalNamespace)
                texts.Add(NormalizeTypeText($"{namedType.ContainingNamespace.ToDisplayString()}.{namedType.Name}"));
        }

        return texts;
    }

    private static string NormalizeTypeText(string text) =>
        text.Replace("global::", string.Empty, StringComparison.Ordinal)
            .Replace("?", string.Empty, StringComparison.Ordinal)
            .Replace("scoped", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);

    private static bool ModifiersLookCompatible(SyntaxTokenList modifiers, RefKind refKind)
    {
        bool hasRef = modifiers.Any(modifier => modifier.IsKind(SyntaxKind.RefKeyword));
        bool hasOut = modifiers.Any(modifier => modifier.IsKind(SyntaxKind.OutKeyword));
        bool hasIn = modifiers.Any(modifier => modifier.IsKind(SyntaxKind.InKeyword));

        return refKind switch
        {
            RefKind.None => !hasRef && !hasOut && !hasIn,
            RefKind.Ref => hasRef,
            RefKind.Out => hasOut,
            RefKind.In => hasIn,
            _ => true
        };
    }

    private static int GetTypeParameterCount(BaseTypeDeclarationSyntax declaration) => declaration switch
    {
        TypeDeclarationSyntax typeDeclaration => typeDeclaration.TypeParameterList?.Parameters.Count ?? 0,
        _ => 0
    };

    private static string GetContainingTypeIdentity(ISymbol symbol) =>
        symbol.ContainingType is null ? string.Empty : GetReflectionTypeName(symbol.ContainingType);

    /// <summary>The type a navigation is really about: the symbol itself, or what declares it.</summary>
    public static INamedTypeSymbol? GetOwningType(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol type => type,
        _ when symbol.ContainingType is not null => symbol.ContainingType,
        _ => null
    };

    /// <summary>
    /// The assembly file a metadata symbol came from, as the compilation sees it — a reference
    /// assembly for the framework and for packages that ship a <c>ref</c> folder.
    /// </summary>
    public static async Task<string?> AssemblyPathAsync(
        ISymbol symbol, Project project, CancellationToken ct)
    {
        if (symbol.ContainingAssembly is not { } assembly)
            return null;

        var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
        if (compilation?.GetMetadataReference(assembly) is not PortableExecutableReference reference)
            return null;

        return reference.FilePath is { Length: > 0 } path && File.Exists(path) ? path : null;
    }

    /// <summary>The metadata spelling of a type: <c>Ns.Outer`1+Inner</c>.</summary>
    public static string GetReflectionTypeName(INamedTypeSymbol type)
    {
        var containingTypes = new Stack<string>();
        for (var current = type; current is not null; current = current.ContainingType)
            containingTypes.Push(current.MetadataName);

        string typeName = string.Join("+", containingTypes);
        return type.ContainingNamespace.IsGlobalNamespace
            ? typeName
            : $"{type.ContainingNamespace.ToDisplayString()}.{typeName}";
    }

    /// <summary>
    /// Where a string literal is used inside decompiled output, preferring the method it was
    /// compiled into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What this is for: a configuration read found in IL knows its key and the method around it,
    /// and the decompiled file is the only source there will ever be for it. Landing on the type
    /// declaration would make the reader hunt through a thousand-line class for the one line the
    /// lens promised.
    /// </para>
    /// <para>
    /// Matched through the syntax tree rather than by searching the text, so a key named in a
    /// comment or spelled inside a longer string is not mistaken for the call. The method name
    /// only ranks candidates: a decompiler may inline, rename, or lift a call into a nested
    /// closure, and a literal in the right file is a better answer than the top of it.
    /// </para>
    /// </remarks>
    /// <returns>The 0-based position of the literal, or null when the file does not contain it.</returns>
    public static (int Line, int Character)? FindLiteral(
        string sourceText, string literal, string? methodName, CancellationToken cancellationToken)
    {
        if (literal.Length == 0)
            return null;

        var tree = CSharpSyntaxTree.ParseText(sourceText, cancellationToken: cancellationToken);
        var root = tree.GetRoot(cancellationToken);

        LiteralExpressionSyntax? fallback = null;

        foreach (var node in root.DescendantNodes())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (node is not LiteralExpressionSyntax
                {
                    RawKind: (int)SyntaxKind.StringLiteralExpression,
                } candidate)
            {
                continue;
            }

            if (candidate.Token.ValueText != literal)
                continue;

            if (methodName is { Length: > 0 } && InMethod(candidate, methodName))
                return Position(tree, candidate, cancellationToken);

            fallback ??= candidate;
        }

        return fallback is null ? null : Position(tree, fallback, cancellationToken);
    }

    private static (int Line, int Character) Position(
        SyntaxTree tree, SyntaxNode node, CancellationToken cancellationToken)
    {
        var line = tree.GetLineSpan(node.Span, cancellationToken).StartLinePosition;
        return (line.Line, line.Character);
    }

    /// <summary>Whether a node sits inside a member of the given name, accessors and local
    /// functions included — decompiled property accessors keep the property's name.</summary>
    private static bool InMethod(SyntaxNode node, string methodName)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            string? name = current switch
            {
                MethodDeclarationSyntax method => method.Identifier.Text,
                LocalFunctionStatementSyntax local => local.Identifier.Text,
                ConstructorDeclarationSyntax constructor => constructor.Identifier.Text,
                PropertyDeclarationSyntax property => property.Identifier.Text,
                BaseTypeDeclarationSyntax => null,
                _ => "",
            };

            if (name is null)
                return false;

            if (name.Length == 0)
                continue;

            if (string.Equals(name, methodName, StringComparison.Ordinal)
                || string.Equals("get_" + name, methodName, StringComparison.Ordinal)
                || string.Equals("set_" + name, methodName, StringComparison.Ordinal)
                || string.Equals(".ctor", methodName, StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        return false;
    }
}
