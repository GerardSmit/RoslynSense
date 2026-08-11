using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMCP.Languages;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;

namespace RoslynMCP.Lsp.Handlers;

internal static class HoverHandler
{
    /// <summary>
    /// A signature shaped like the declaration it came from, rather than like an error message.
    /// </summary>
    /// <remarks>
    /// The shape is the point. A hover body is a fenced <c>csharp</c> block, and the client
    /// colours fenced code with its C# TextMate grammar — which only recognises real declaration
    /// syntax. <c>CSharpErrorMessageFormat</c> produces prose ("local variable Foo bar") and
    /// fully-qualified member names, neither of which the grammar matches, so the whole hover
    /// rendered in one flat colour. Accessibility and modifiers are in, the containing type is
    /// out (it goes on its own line below, the way Rider shows it), and properties get their
    /// <c>{ get; set; }</c> back.
    /// </remarks>
    private static readonly SymbolDisplayFormat s_displayFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters
            | SymbolDisplayGenericsOptions.IncludeVariance,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters
            | SymbolDisplayMemberOptions.IncludeType
            | SymbolDisplayMemberOptions.IncludeRef
            | SymbolDisplayMemberOptions.IncludeModifiers
            | SymbolDisplayMemberOptions.IncludeAccessibility
            | SymbolDisplayMemberOptions.IncludeExplicitInterface,
        delegateStyle: SymbolDisplayDelegateStyle.NameAndSignature,
        parameterOptions: SymbolDisplayParameterOptions.IncludeName
            | SymbolDisplayParameterOptions.IncludeType
            | SymbolDisplayParameterOptions.IncludeParamsRefOut
            | SymbolDisplayParameterOptions.IncludeDefaultValue
            | SymbolDisplayParameterOptions.IncludeExtensionThis,
        propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
        localOptions: SymbolDisplayLocalOptions.IncludeType
            | SymbolDisplayLocalOptions.IncludeRef
            | SymbolDisplayLocalOptions.IncludeConstantValue,
        kindOptions: SymbolDisplayKindOptions.None,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
            | SymbolDisplayMiscellaneousOptions.UseErrorTypeSymbolName);

    /// <summary>A type as it is written inside a declaration: short name plus type parameters,
    /// with the namespace left to the line underneath.</summary>
    private static readonly SymbolDisplayFormat s_typeNameFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters
            | SymbolDisplayGenericsOptions.IncludeVariance,
        delegateStyle: SymbolDisplayDelegateStyle.NameOnly,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
            | SymbolDisplayMiscellaneousOptions.UseErrorTypeSymbolName
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <summary>How the containing type is named on the line under the signature — fully
    /// qualified, because that line is the only place the namespace still appears.</summary>
    private static readonly SymbolDisplayFormat s_containerFormat =
        SymbolDisplayFormat.FullyQualifiedFormat
            .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted);

    /// <summary>
    /// Attributes the compiler writes for its own bookkeeping. They are on nearly every symbol
    /// from metadata and say nothing a reader wants, so listing them would bury the one or two
    /// attributes that were actually written in source.
    /// </summary>
    private static readonly HashSet<string> s_noiseAttributes = new(StringComparer.Ordinal)
    {
        "System.Runtime.CompilerServices.CompilerGeneratedAttribute",
        "System.Runtime.CompilerServices.NullableAttribute",
        "System.Runtime.CompilerServices.NullableContextAttribute",
        "System.Runtime.CompilerServices.IsReadOnlyAttribute",
        "System.Runtime.CompilerServices.IsByRefLikeAttribute",
        "System.Runtime.CompilerServices.ExtensionAttribute",
        "System.Runtime.CompilerServices.AsyncStateMachineAttribute",
        "System.Runtime.CompilerServices.IteratorStateMachineAttribute",
        "System.Diagnostics.DebuggerBrowsableAttribute",
        "System.Diagnostics.DebuggerStepThroughAttribute",
        "System.Diagnostics.DebuggerHiddenAttribute",
        "System.Diagnostics.DebuggerNonUserCodeAttribute",
    };

    public static async Task<Hover?> HoverAsync(
        TextDocumentPositionParams p, CancellationToken ct, LanguageSession? languages = null)
    {
        if (await HandlerHelpers.ResolveAsync(p.TextDocument, p.Position, ct) is not
            var (document, text, offset) || document is null)
            return null;

        // Inside a string literal Roslyn binds to nothing, so a resource key would hover blank.
        // Ask the embedded languages first; the check ends after a syntax lookup unless the caret
        // really is in a literal, and before that when none are registered.
        if (await RoslynEmbeddedLanguages.Current.DetectAsync(document, offset, ct) is
            { Language: IEmbeddedHoverProvider embedded } embeddedContext)
        {
            return await embedded.HoverAsync(embeddedContext, ct);
        }

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, offset, ct);
        if (symbol is null)
            return null;

        // Highlight the identifier token under the cursor when we can find it.
        Protocol.Range? range = null;
        var root = await document.GetSyntaxRootAsync(ct);
        var token = root?.FindToken(Math.Min(offset, Math.Max(0, text.Length - 1)));
        if (token is { } t && t.Span.Contains(Math.Min(offset, Math.Max(0, text.Length - 1))))
            range = LspConverters.ToRange(text.Lines, t.Span);

        var markdown = new StringBuilder(Describe(symbol, ct));

        // Appended rather than merged, and after Roslyn's own description: what the pack knows is
        // where the symbol came from, which reads as provenance under the signature rather than in
        // place of it.
        foreach (var contributor in LanguageScope.Of(languages).Contributors<ILanguageHoverContributor>())
        {
            if (await contributor.HoverMarkdownAsync(symbol, document.Project, ct) is { Length: > 0 } extra)
                markdown.Append("\n\n---\n\n").Append(extra);
        }

        return new Hover(new MarkupContent("markdown", markdown.ToString()), range);
    }

    /// <summary>The signature-plus-summary markdown shown for a symbol. Shared with the markup
    /// languages, whose symbols do not come from a syntax position.</summary>
    public static string Describe(ISymbol symbol, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("```csharp\n");
        foreach (string attribute in DeclaredAttributes(symbol))
            sb.Append(attribute).Append('\n');
        sb.Append(Signature(symbol));
        sb.Append("\n```");

        // Outside the fence: it is not part of the declaration, and inside it the grammar would
        // colour "in class" as if `in` were the parameter modifier.
        if (Container(symbol) is { Length: > 0 } container)
            sb.Append("\n\n").Append(container);

        var xmlDoc = symbol.GetDocumentationCommentXml(cancellationToken: ct);
        if (!string.IsNullOrWhiteSpace(xmlDoc))
        {
            var summary = SymbolFormatter.ExtractXmlDocSection(xmlDoc, "summary");
            if (!string.IsNullOrWhiteSpace(summary))
                sb.Append("\n\n").Append(summary);

            var returns = SymbolFormatter.ExtractXmlDocSection(xmlDoc, "returns");
            if (!string.IsNullOrWhiteSpace(returns))
                sb.Append("\n\n**Returns:** ").Append(returns);
        }

        return sb.ToString();
    }

    /// <summary>
    /// The symbol written the way it was declared, terminated so the grammar sees a complete
    /// declaration rather than a dangling one.
    /// </summary>
    /// <remarks>
    /// <see cref="SymbolDisplayFormat"/> alone gets members right and everything else wrong: a
    /// type comes back as the bare identifier <c>Calculator</c>, with no accessibility and no
    /// <c>class</c> in front of it, and a namespace as <c>SampleProject</c>. Neither is anything
    /// the C# grammar recognises, so both hovered in one flat colour however the fence was
    /// labelled. The headers below are assembled by hand for exactly that reason.
    /// </remarks>
    private static string Signature(ISymbol symbol) => symbol switch
    {
        INamespaceSymbol ns => $"namespace {ns.ToDisplayString(s_containerFormat)}",
        INamedTypeSymbol type => TypeHeader(type),

        // `Pending = 0`, not `public ProcessingStatus Pending` — an enum member is written as its
        // name and its value, and the containing type is already on the line underneath.
        IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum, HasConstantValue: true } member =>
            $"{member.Name} = {member.ConstantValue}",

        // A property's `{ get; set; }` already closes it; everything else needs the semicolon to
        // parse as a declaration rather than as the start of one.
        IPropertySymbol => symbol.ToDisplayString(s_displayFormat),
        _ => symbol.ToDisplayString(s_displayFormat) + ";",
    };

    /// <summary>The declaration line of a type: modifiers, keyword, name, and what it derives
    /// from — the same line you would find in the source file.</summary>
    private static string TypeHeader(INamedTypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Delegate)
        {
            // NameAndSignature drops the return type, which leaves `delegate Handler(int x)`.
            var invoke = type.DelegateInvokeMethod;
            string returns = invoke?.ReturnType.ToDisplayString(s_typeNameFormat) ?? "void";
            string parameters = string.Join(", ",
                invoke?.Parameters.Select(p => p.ToDisplayString(s_displayFormat)) ?? []);
            return $"{Modifiers(type)}delegate {returns} "
                + $"{type.ToDisplayString(s_typeNameFormat)}({parameters});";
        }

        var header = new StringBuilder(Modifiers(type))
            .Append(Keyword(type))
            .Append(' ')
            .Append(type.ToDisplayString(s_typeNameFormat));

        var bases = BaseTypes(type).ToArray();
        if (bases.Length > 0)
            header.Append(" : ").Append(string.Join(", ", bases));

        return header.ToString();
    }

    /// <summary>What the type derives from, minus the bases every type of that kind has.</summary>
    private static IEnumerable<string> BaseTypes(INamedTypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Enum)
        {
            // Only when it was written: `enum Status : int` is what you get by saying nothing.
            if (type.EnumUnderlyingType is { SpecialType: not SpecialType.System_Int32 } underlying)
                yield return underlying.ToDisplayString(s_typeNameFormat);
            yield break;
        }

        if (type.BaseType is { SpecialType: SpecialType.None } baseType)
            yield return baseType.ToDisplayString(s_typeNameFormat);

        foreach (var iface in type.Interfaces)
            yield return iface.ToDisplayString(s_typeNameFormat);
    }

    private static string Modifiers(INamedTypeSymbol type)
    {
        var parts = new List<string> { AccessibilityKeyword(type.DeclaredAccessibility) };

        if (type.IsStatic)
            parts.Add("static");
        else if (type.TypeKind == TypeKind.Class)
        {
            if (type.IsAbstract)
                parts.Add("abstract");
            if (type.IsSealed && !type.IsRecord)
                parts.Add("sealed");
        }

        if (type.TypeKind == TypeKind.Struct)
        {
            if (type.IsReadOnly)
                parts.Add("readonly");
            if (type.IsRefLikeType)
                parts.Add("ref");
        }

        parts.RemoveAll(string.IsNullOrEmpty);
        return parts.Count == 0 ? "" : string.Join(" ", parts) + " ";
    }

    private static string AccessibilityKeyword(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.Internal => "internal",
        Accessibility.Private => "private",
        Accessibility.Protected => "protected",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.ProtectedAndInternal => "private protected",
        _ => "",
    };

    /// <summary>
    /// Where the symbol lives, as the italic line under the signature: the containing type for a
    /// member, the namespace for a type. Locals and parameters get nothing — their container is
    /// the method the reader is already looking at.
    /// </summary>
    private static string? Container(ISymbol symbol)
    {
        if (symbol is ILocalSymbol or IParameterSymbol or IRangeVariableSymbol or INamespaceSymbol)
            return null;

        if (symbol is INamedTypeSymbol type)
        {
            return type.ContainingType is { } outer
                ? $"in {Keyword(outer)} `{outer.ToDisplayString(s_containerFormat)}`"
                : type.ContainingNamespace is { IsGlobalNamespace: false } ns
                    ? $"in namespace `{ns.ToDisplayString(s_containerFormat)}`"
                    : null;
        }

        return symbol.ContainingType is { } containing
            ? $"in {Keyword(containing)} `{containing.ToDisplayString(s_containerFormat)}`"
            : null;
    }

    private static string Keyword(INamedTypeSymbol type) => type.TypeKind switch
    {
        TypeKind.Interface => "interface",
        TypeKind.Struct => type.IsRecord ? "record struct" : "struct",
        TypeKind.Enum => "enum",
        TypeKind.Delegate => "delegate",
        _ => type.IsRecord ? "record" : "class",
    };

    /// <summary>
    /// The attributes written on the symbol, one per line above the signature. Rider shows them
    /// and they carry real meaning on generated data models — an <c>[Association]</c> is the only
    /// place the foreign key is written down.
    /// </summary>
    private static IEnumerable<string> DeclaredAttributes(ISymbol symbol)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            if (attribute.AttributeClass is not { } attributeClass)
                continue;
            if (s_noiseAttributes.Contains(attributeClass.ToDisplayString(s_containerFormat)))
                continue;

            // Written the way it was written in source: short name, no `Attribute` suffix.
            string name = attributeClass.Name.EndsWith("Attribute", StringComparison.Ordinal)
                ? attributeClass.Name[..^"Attribute".Length]
                : attributeClass.Name;

            var arguments = attribute.ConstructorArguments
                .Select(a => a.ToString())
                .Concat(attribute.NamedArguments
                    .Select(a => $"{a.Key} = {a.Value.ToString()}"))
                .ToArray();

            yield return arguments.Length == 0
                ? $"[{name}]"
                : $"[{name}({string.Join(", ", arguments)})]";
        }
    }
}
