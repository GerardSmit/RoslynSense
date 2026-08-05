using System.Buffers;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.Resources.Core;

/// <summary>One place a key is written, and the exact characters that change when it is renamed.</summary>
/// <param name="KeySuffix">The suffix the lookup at this site appends when the key carries no dot
/// of its own — DNN's <c>.Text</c>. Null where the site writes the key out in full, which is every
/// <c>.resx</c> and every markup mention.</param>
internal readonly record struct ResourceKeySite(
    string FilePath, SourceText Text, TextSpan Span, string? KeySuffix);

/// <summary>The key a caret is on, and the families it was resolved against.</summary>
/// <remarks>
/// Identity is the pair (entry name, family). The same <c>Title</c> in two
/// <c>App_LocalResources</c> folders is two keys; the same key written <c>"Save"</c> at a DNN call
/// site and <c>Save.Text</c> in the <c>.resx</c> is one.
/// </remarks>
internal sealed record ResourceKeyTarget
{
    /// <summary>The entry name as the runtime probes for it, or the group prefix — never the
    /// abbreviated form a call site happened to write.</summary>
    public required string Key { get; init; }

    /// <summary>
    /// Whether the caret named a group rather than an entry. <c>meta:resourcekey="btnSave"</c>
    /// declares nothing itself: the entries it reaches are <c>btnSave.Text</c>,
    /// <c>btnSave.ToolTip</c> and whatever else the control sets, so renaming it moves all of them
    /// and rewrites only the leading segment of each.
    /// </summary>
    public required bool Group { get; init; }

    /// <summary>Loaded, and narrowed to the families that actually declare the key.</summary>
    public required ImmutableArray<ResourceFamily> Families { get; init; }

    public required RootConfidence Confidence { get; init; }

    /// <summary>The project the caret's file belongs to, carried because the request that reached a
    /// <c>.resx</c> had none to give — a <c>.resx</c> is not a Roslyn document.</summary>
    public required Project? Project { get; init; }

    public required string FilePath { get; init; }

    public required SourceText Text { get; init; }

    /// <summary>The characters under the caret, which are the key as this site wrote it.</summary>
    public required TextSpan Span { get; init; }

    public required string? KeySuffix { get; init; }

    public string Written => Text.ToString(Span);
}

/// <summary>
/// A resource key as something a request can be about: which one a literal or a caret names, and
/// everywhere in the solution it is written.
/// </summary>
/// <remarks>
/// The counterpart of <c>SymbolFinder</c> for something that is not a symbol. Both directions run
/// the same resolution — a key in C# through <see cref="KeyAtAsync"/>, a key in markup through
/// <see cref="AspxResourceService"/> — so a site find-references reports is by construction a site
/// rename would rewrite.
/// </remarks>
internal static class ResourceKeySearch
{
    /// <summary>The property DNN and ASP.NET both read a control's own resource file from.</summary>
    private const string LocalResourceFile = "LocalResourceFile";

    /// <summary>The name an indexer binds under — <c>localizer["Title"]</c>.</summary>
    private const string IndexerName = "Item";

    /// <summary>One parameter of any type, in a configured signature.</summary>
    private const string Wildcard = "*";

    /// <summary>Proximity is a guess, and eight guesses is already more than anyone will read.</summary>
    private const int ProximityLimit = 8;

    /// <summary>What a string literal turned out to name.</summary>
    /// <remarks>
    /// <see cref="Candidates"/> is every family the call could be reading, not only the ones that
    /// have the key: hover has to be able to say a key is declared nowhere, and the missing-key
    /// diagnostic has to be able to name the files it looked in.
    /// </remarks>
    public sealed record CodeMatch
    {
        /// <summary>The key as the runtime probes for it, suffix and all.</summary>
        public required string Key { get; init; }

        /// <summary>The lookup's default suffix, so a rewrite of this site can put back whatever
        /// abbreviation it used.</summary>
        public required string? Suffix { get; init; }

        /// <summary>The literal without its quotes.</summary>
        public required TextSpan Span { get; init; }

        public required ImmutableArray<ResourceFamily> Candidates { get; init; }

        public required RootConfidence Confidence { get; init; }
    }

    // ---- C# literals -----------------------------------------------------------------------------

    /// <summary>
    /// Whether a literal is a resource key at all — the question the embedded-language detector
    /// asks, and it asks it of every string in the file.
    /// </summary>
    /// <remarks>
    /// No root resolution and no file opened. Which <c>.resx</c> a key lives in has no bearing on
    /// whether the literal <em>is</em> a key, and resolving it here would put a directory walk
    /// behind every string in every diagnostics pass.
    /// </remarks>
    public static bool IsKeyLiteral(
        ResourceSettings settings, SemanticModel semanticModel, SyntaxToken token, CancellationToken ct) =>
        Match(settings, semanticModel, token, ct) is not null;

    /// <summary>The key a literal carries and the families behind it, or null when it carries none.</summary>
    public static async Task<CodeMatch?> KeyAtAsync(
        ResourceSettings settings, ResourceCatalog catalog, Project project,
        SemanticModel semanticModel, SyntaxToken token, CancellationToken ct)
    {
        if (Match(settings, semanticModel, token, ct) is not { } site)
            return null;

        var resolved = await RootAsync(settings, catalog, project, semanticModel, site, ct);

        return new CodeMatch
        {
            Key = site.Key,
            Suffix = site.Lookup.DefaultKeySuffix,
            Span = site.Span,
            Candidates = resolved.Families,
            Confidence = resolved.Families.IsEmpty ? RootConfidence.Unknown : resolved.Confidence,
        };
    }

    /// <summary>One configured lookup matched against one call site, before any file was opened.</summary>
    private readonly record struct CallSite(
        ResourceLookup Lookup,
        string Key,
        TextSpan Span,
        ExpressionSyntax Call,
        SeparatedSyntaxList<ArgumentSyntax> Arguments);

    /// <summary>
    /// The lookup this token is the key of.
    /// </summary>
    /// <remarks>
    /// The order of the checks is the point. The invocation shape, the invoked name and the
    /// argument position are answerable from syntax alone and reject all but a handful of tokens;
    /// the bind happens last, so a file of hundreds of literals costs a name comparison each rather
    /// than a <c>GetSymbolInfo</c> each.
    /// </remarks>
    private static CallSite? Match(
        ResourceSettings settings, SemanticModel semanticModel, SyntaxToken token, CancellationToken ct)
    {
        if (settings.Lookups.IsDefaultOrEmpty
            || !token.IsKind(SyntaxKind.StringLiteralToken)
            || token.Parent is not LiteralExpressionSyntax literal
            || literal.Parent is not ArgumentSyntax argument
            || argument.Parent is not BaseArgumentListSyntax list)
        {
            return null;
        }

        // A lookup addresses its key positionally, and a named argument's slot in the list says
        // nothing about which parameter it fills.
        if (argument.NameColon is not null)
            return null;

        ExpressionSyntax call;
        string invoked;

        switch (list.Parent)
        {
            case InvocationExpressionSyntax invocation when list is ArgumentListSyntax:
                if (InvokedName(invocation.Expression) is not { } name)
                    return null;

                call = invocation;
                invoked = name;
                break;

            case ElementAccessExpressionSyntax access when list is BracketedArgumentListSyntax:
                call = access;
                invoked = IndexerName;
                break;

            default:
                return null;
        }

        int index = list.Arguments.IndexOf(argument);
        var candidates = new List<ResourceLookup>();

        foreach (var lookup in settings.Lookups)
        {
            if (lookup.KeyIndex == index
                && lookup.MethodName.Equals(invoked, StringComparison.Ordinal)
                && (lookup.RootSource != RootSource.Argument || lookup.RootIndex < list.Arguments.Count))
            {
                candidates.Add(lookup);
            }
        }

        if (candidates.Count == 0 || semanticModel.GetSymbolInfo(call, ct).Symbol is not { } member)
            return null;

        foreach (var lookup in candidates)
        {
            if (Binds(lookup, member))
            {
                return new CallSite(
                    lookup, EffectiveKey(token.ValueText, lookup.DefaultKeySuffix),
                    LiteralSpan(token), call, list.Arguments);
            }
        }

        return null;
    }

    /// <summary>The simple name an invocation calls, from syntax alone.</summary>
    private static string? InvokedName(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        SimpleNameSyntax name => name.Identifier.ValueText,
        _ => null,
    };

    private static bool Binds(ResourceLookup lookup, ISymbol member)
    {
        var definition = member.OriginalDefinition;

        if (definition.ContainingType is not { } containing || !DeclaredBy(containing, lookup.ContainingType))
            return false;

        return lookup.ParameterTypes is not { } expected || Signature(definition, expected);
    }

    /// <summary>
    /// Whether the member's declaring type is the configured one, or derives from it.
    /// </summary>
    /// <remarks>
    /// The declaring type and not the receiver's: <c>this.LocalizeText(key)</c> in a module binds to
    /// <c>PortalModuleBase.LocalizeText</c>, which is the type the configuration names. Interfaces
    /// are walked too, since a call through <c>IStringLocalizer&lt;T&gt;</c> reaches a member
    /// declared on the non-generic one.
    /// </remarks>
    private static bool DeclaredBy(INamedTypeSymbol type, string name)
    {
        for (var candidate = type; candidate is not null; candidate = candidate.BaseType)
        {
            if (candidate.ToDisplayString(s_declarationName).Equals(name, StringComparison.Ordinal))
                return true;
        }

        foreach (var contract in type.AllInterfaces)
        {
            if (contract.ToDisplayString(s_declarationName).Equals(name, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// A positional signature, which is what tells <c>GetString(key, root)</c> apart from
    /// <c>GetString(key, control)</c> and <c>GetString(key, portalSettings)</c> — three overloads of
    /// one name, of which only the first has a root at index 1.
    /// </summary>
    private static bool Signature(ISymbol member, ImmutableArray<string> expected)
    {
        var parameters = member switch
        {
            IMethodSymbol method => method.Parameters,
            IPropertySymbol property => property.Parameters,
            _ => [],
        };

        if (parameters.Length != expected.Length)
            return false;

        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i].Equals(Wildcard, StringComparison.Ordinal))
                continue;

            if (!parameters[i].Type.ToDisplayString(s_typeName).Equals(expected[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    /// <summary>
    /// The literal without its quotes — the key is the content, not the syntax around it. Falls
    /// back to the whole token for a shape the prefix walk does not recognise.
    /// </summary>
    private static TextSpan LiteralSpan(SyntaxToken token)
    {
        string text = token.Text;
        int start = 0;

        while (start < text.Length && (text[start] == '@' || text[start] == '$'))
            start++;

        int quotes = 0;
        while (start + quotes < text.Length && text[start + quotes] == '"')
            quotes++;

        return quotes == 0 || text.Length < start + (2 * quotes)
            ? token.Span
            : TextSpan.FromBounds(token.SpanStart + start + quotes, token.Span.End - quotes);
    }

    /// <summary>Fully qualified with the C# keyword for the built-ins, so a configured signature
    /// reads <c>string</c> rather than <c>System.String</c>.</summary>
    private static readonly SymbolDisplayFormat s_typeName = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>Fully qualified with the type arguments dropped, so a call through
    /// <c>IStringLocalizer&lt;Home&gt;</c> matches a lookup written against
    /// <c>IStringLocalizer</c>.</summary>
    private static readonly SymbolDisplayFormat s_declarationName = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces);

    // ---- Roots -------------------------------------------------------------------------------------

    /// <summary>What a root resolved to: how sure we are of it, the file the fallback conventions
    /// are measured from, and the families it named.</summary>
    private readonly record struct ResolvedRoot(
        RootConfidence Confidence, string? Anchor, ImmutableArray<ResourceFamily> Families);

    /// <summary>
    /// The three rules in order: what the call says outright, what a single assignment says, and
    /// what the files around the call site suggest.
    /// </summary>
    /// <remarks>
    /// The unresolved root is the majority case rather than a degradation. DNN's dominant call
    /// shapes are <c>LocalizeText(key)</c>, <c>GetString(key, this.LocalResourceFile)</c> and
    /// <c>GetString(key, ctrl)</c>, and not one of them carries a root anybody can read.
    /// </remarks>
    private static async Task<ResolvedRoot> RootAsync(
        ResourceSettings settings, ResourceCatalog catalog, Project project,
        SemanticModel semanticModel, CallSite site, CancellationToken ct)
    {
        if (semanticModel.SyntaxTree.FilePath is not { Length: > 0 } file
            || Path.GetDirectoryName(project.FilePath) is not { Length: > 0 } directory)
        {
            return new ResolvedRoot(RootConfidence.Unknown, null, []);
        }

        string projectRoot = PathHelper.NormalizePath(directory);
        var read = await ReadAsync(settings, catalog, project, semanticModel, site, file, projectRoot, ct);

        // A root that resolved to nothing on disk is no better than one that never resolved:
        // proximity at least offers files that exist, and Ambiguous is what keeps the missing-key
        // diagnostic from firing on a family this server simply failed to find.
        var resolved = read is { Families.IsEmpty: false } found
            ? found
            : Proximity(settings, catalog, read?.Anchor ?? file, projectRoot);

        string anchor = resolved.Anchor ?? file;
        var families = ImmutableArray.CreateBuilder<ResourceFamily>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var family in resolved.Families)
            Add(families, seen, family);

        // The cascade DNN itself walks when the key misses: the shared file beside the page, then
        // the application-wide one. Only when it misses — a key the page's own file answers never
        // reaches them, so listing them unconditionally would name files the runtime never opens.
        if (!Defines(families, site.Key))
        {
            foreach (string id in site.Lookup.Fallbacks)
            {
                if (settings.Convention(id) is { } convention
                    && Apply(catalog, convention, anchor, projectRoot) is { } family)
                {
                    Add(families, seen, family);
                }
            }
        }

        return resolved with { Families = families.ToImmutable() };
    }

    private static async Task<ResolvedRoot?> ReadAsync(
        ResourceSettings settings, ResourceCatalog catalog, Project project,
        SemanticModel semanticModel, CallSite site, string file, string projectRoot, CancellationToken ct)
    {
        var lookup = site.Lookup;

        switch (lookup.RootSource)
        {
            case RootSource.Constant when lookup.RootConstant is { Length: > 0 } constant:
                return Named(settings, catalog, lookup, constant, file, projectRoot);

            case RootSource.ContainingFile:
                return Local(settings, catalog, file, RootConfidence.Exact);

            case RootSource.TypeArgument when TypeArgument(site.Call, semanticModel, ct) is { } argument:
                return Named(settings, catalog, lookup, argument, file, projectRoot);

            case RootSource.ContainingType:
                return await ContainingTypeAsync(
                    settings, catalog, project, semanticModel, site, file, projectRoot, ct);

            case RootSource.Argument when Argument(site, lookup.RootIndex) is { } expression:
                return await ArgumentAsync(
                    settings, catalog, project, semanticModel, site, expression, file, projectRoot, ct);
        }

        return null;
    }

    private static async Task<ResolvedRoot?> ContainingTypeAsync(
        ResourceSettings settings, ResourceCatalog catalog, Project project,
        SemanticModel semanticModel, CallSite site, string file, string projectRoot, CancellationToken ct)
    {
        var type = EnclosingType(site.Call, semanticModel, ct);

        if (site.Lookup.RootInterpretation is not (RootInterpretation.VirtualPath or RootInterpretation.RelativePath))
        {
            return type is null
                ? null
                : Named(
                    settings, catalog, site.Lookup, type.ToDisplayString(s_declarationName),
                    file, projectRoot);
        }

        return await MarkupOfAsync(type, project, ct) is { } markup
            ? Local(settings, catalog, markup, RootConfidence.Exact)
            : null;
    }

    private static async Task<ResolvedRoot?> ArgumentAsync(
        ResourceSettings settings, ResourceCatalog catalog, Project project,
        SemanticModel semanticModel, CallSite site, ExpressionSyntax expression,
        string file, string projectRoot, CancellationToken ct)
    {
        if (semanticModel.GetConstantValue(expression, ct) is { HasValue: true, Value: string constant })
            return Value(settings, catalog, site.Lookup, constant, RootConfidence.Exact, file, projectRoot);

        // Convention as a primary path, not a fallback. DNN sets LocalResourceFile from the
        // control's ControlSrc, which is the markup file's own path, so following the class to its
        // markup does what the runtime does instead of guessing at what it would have computed.
        if (IsLocalResourceFile(expression, semanticModel, ct))
        {
            var type = EnclosingType(site.Call, semanticModel, ct);

            return await MarkupOfAsync(type, project, ct) is { } markup
                ? Local(settings, catalog, markup, RootConfidence.Exact)
                : null;
        }

        return SingleAssignment(expression, semanticModel, ct) is { } inferred
            ? Value(settings, catalog, site.Lookup, inferred, RootConfidence.Inferred, file, projectRoot)
            : null;
    }

    /// <summary>A root value read from the call, interpreted the way its lookup says to.</summary>
    private static ResolvedRoot Value(
        ResourceSettings settings, ResourceCatalog catalog, ResourceLookup lookup,
        string value, RootConfidence confidence, string file, string projectRoot)
    {
        var interpretation = lookup.RootInterpretation;

        if (interpretation is not (RootInterpretation.VirtualPath or RootInterpretation.RelativePath))
            return Named(settings, catalog, lookup, value, file, projectRoot) with { Confidence = confidence };

        return FilePath(value, interpretation, file, projectRoot) is { } path
            ? Local(settings, catalog, path, confidence)
            : new ResolvedRoot(confidence, null, []);
    }

    /// <summary>
    /// The families a file's own resources live in: the sibling-folder conventions that derive
    /// their name from the file, which is the <c>local</c> convention DNN and ASP.NET both apply.
    /// </summary>
    private static ResolvedRoot Local(
        ResourceSettings settings, ResourceCatalog catalog, string path, RootConfidence confidence)
    {
        string directory = Path.GetDirectoryName(path) ?? path;
        string name = Path.GetFileName(path);
        var families = ImmutableArray.CreateBuilder<ResourceFamily>();

        foreach (var convention in settings.Conventions)
        {
            if (convention is { FixedName: null, SiblingFolder: { Length: > 0 } sibling }
                && catalog.Find(Combine(directory, sibling), name) is { } family)
            {
                families.Add(family);
            }
        }

        return new ResolvedRoot(confidence, path, families.ToImmutable());
    }

    /// <summary>
    /// The families a root <em>name</em> points at — a global resource class, a localizer's type
    /// argument, a base name written as such.
    /// </summary>
    /// <remarks>
    /// The root-folder conventions first, because a name that resolves under
    /// <c>App_GlobalResources</c> means that file and not the dozen <c>SharedResources.resx</c>
    /// scattered through a site's <c>App_LocalResources</c> folders. The bare name is the last
    /// resort, for a layout nobody configured.
    /// </remarks>
    private static ResolvedRoot Named(
        ResourceSettings settings, ResourceCatalog catalog, ResourceLookup lookup,
        string value, string file, string projectRoot)
    {
        var families = ImmutableArray.CreateBuilder<ResourceFamily>();

        foreach (string name in BaseNames(value, lookup.RootInterpretation))
        {
            foreach (var convention in settings.Conventions)
            {
                if (convention.RootFolder is { Length: > 0 } folder
                    && catalog.Find(Combine(projectRoot, folder), name) is { } family)
                {
                    families.Add(family);
                }
            }

            if (families.Count == 0)
                families.AddRange(catalog.Named(name).Take(ProximityLimit));

            if (families.Count > 0)
                break;
        }

        return new ResolvedRoot(RootConfidence.Exact, file, families.ToImmutable());
    }

    /// <summary>The base names a root value could be written as. A type name resolves under its full
    /// name and under the bare one, because whether a project mirrors its namespaces in folders is a
    /// habit rather than a rule.</summary>
    private static IEnumerable<string> BaseNames(string value, RootInterpretation interpretation)
    {
        yield return value;

        if (interpretation == RootInterpretation.TypeName && value.LastIndexOf('.') is > 0 and var dot)
            yield return value[(dot + 1)..];
    }

    /// <summary>
    /// Candidates from the call site outwards when nothing named the root: the anchor's own
    /// conventions, then the same set per ancestor directory up to the project root, then the
    /// root-folder ones.
    /// </summary>
    /// <remarks>
    /// Ranked, capped and all returned. Everything here is a guess, which is why the confidence it
    /// carries switches the missing-key diagnostic off entirely and refuses a rename outright.
    /// </remarks>
    private static ResolvedRoot Proximity(
        ResourceSettings settings, ResourceCatalog catalog, string anchor, string projectRoot)
    {
        string name = Path.GetFileName(anchor);
        var families = ImmutableArray.CreateBuilder<ResourceFamily>();

        for (string? directory = Path.GetDirectoryName(anchor);
             directory is { Length: > 0 } && families.Count < ProximityLimit;
             directory = Parent(directory, projectRoot))
        {
            foreach (var convention in settings.Conventions)
            {
                if (convention.RootFolder is { Length: > 0 })
                    continue;

                if (Apply(catalog, convention, Path.Combine(directory, name), projectRoot) is { } family)
                    families.Add(family);
            }
        }

        foreach (var convention in settings.Conventions)
        {
            if (convention.RootFolder is { Length: > 0 }
                && Apply(catalog, convention, anchor, projectRoot) is { } family)
            {
                families.Add(family);
            }
        }

        var capped = families.Take(ProximityLimit).ToImmutableArray();

        return new ResolvedRoot(
            capped.IsEmpty ? RootConfidence.Unknown : RootConfidence.Ambiguous, anchor, capped);
    }

    /// <summary>One named convention against one file.</summary>
    private static ResourceFamily? Apply(
        ResourceCatalog catalog, ResourceRootConvention convention, string anchor, string projectRoot)
    {
        string directory = Path.GetDirectoryName(anchor) ?? projectRoot;

        string folder = convention.RootFolder is { Length: > 0 } root
            ? Combine(projectRoot, root)
            : convention.SiblingFolder is { Length: > 0 } sibling
                ? Combine(directory, sibling)
                : PathHelper.NormalizePath(directory);

        return catalog.Find(folder, convention.FixedName ?? Path.GetFileName(anchor));
    }

    /// <summary>The next directory up, stopping at the project root: a walk that leaves the project
    /// is reading somebody else's resources.</summary>
    private static string? Parent(string directory, string projectRoot) =>
        directory.Equals(projectRoot, StringComparison.OrdinalIgnoreCase)
        || !directory.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase)
            ? null
            : Path.GetDirectoryName(directory);

    /// <summary>
    /// The file a path-shaped root value names. For a virtual path <c>~/</c> and a leading slash are
    /// both the application root, which for a project-based workspace is the directory holding the
    /// project file; everything else is relative to the call site.
    /// </summary>
    private static string? FilePath(
        string value, RootInterpretation interpretation, string file, string projectRoot)
    {
        string relative = value.Replace('/', Path.DirectorySeparatorChar).Trim();
        if (relative.Length == 0)
            return null;

        string directory = Path.GetDirectoryName(file) ?? projectRoot;

        if (interpretation == RootInterpretation.VirtualPath)
        {
            if (relative.StartsWith("~" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                directory = projectRoot;
                relative = relative[2..];
            }
            else if (relative[0] == Path.DirectorySeparatorChar)
            {
                directory = projectRoot;
                relative = relative[1..];
            }
        }

        if (relative.Length == 0)
            return null;

        try
        {
            return Path.GetFullPath(Path.Combine(directory, relative));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The markup file whose <c>Inherits</c> names this class — the mapping
    /// <c>ModuleControlFactory</c> itself walks, in the other direction.</summary>
    private static async Task<string?> MarkupOfAsync(
        INamedTypeSymbol? type, Project project, CancellationToken ct)
    {
        if (type is null)
            return null;

        string name = type.ToDisplayString(s_declarationName);

        foreach (var index in await WebFormsIndex.ForProjectAsync(project, ct))
        {
            if (index.Inherits is { Length: > 0 } inherits
                && inherits.Equals(name, StringComparison.Ordinal))
            {
                return index.FilePath;
            }
        }

        return null;
    }

    /// <summary>A constant assigned exactly once, at the declaration. Nothing beyond that: data flow
    /// through branches is where inference stops being inference.</summary>
    private static string? SingleAssignment(
        ExpressionSyntax expression, SemanticModel semanticModel, CancellationToken ct)
    {
        var symbol = semanticModel.GetSymbolInfo(expression, ct).Symbol;

        if (symbol is not (IFieldSymbol or ILocalSymbol or IPropertySymbol))
            return null;

        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            var value = reference.GetSyntax(ct) switch
            {
                VariableDeclaratorSyntax variable => variable.Initializer?.Value,
                PropertyDeclarationSyntax property =>
                    property.Initializer?.Value ?? property.ExpressionBody?.Expression,
                _ => null,
            };

            if (value is null || !semanticModel.Compilation.ContainsSyntaxTree(value.SyntaxTree))
                continue;

            var declaring = semanticModel.SyntaxTree == value.SyntaxTree
                ? semanticModel
                : semanticModel.Compilation.GetSemanticModel(value.SyntaxTree);

            if (declaring.GetConstantValue(value, ct) is { HasValue: true, Value: string constant })
                return constant;
        }

        return null;
    }

    private static bool IsLocalResourceFile(
        ExpressionSyntax expression, SemanticModel semanticModel, CancellationToken ct) =>
        semanticModel.GetSymbolInfo(expression, ct).Symbol is IPropertySymbol property
        && property.Name.Equals(LocalResourceFile, StringComparison.Ordinal);

    /// <summary>The positional argument a lookup reads its root from, if the call passes it
    /// positionally.</summary>
    private static ExpressionSyntax? Argument(CallSite site, int index) =>
        index >= 0 && index < site.Arguments.Count && site.Arguments[index] is { NameColon: null } argument
            ? argument.Expression
            : null;

    private static INamedTypeSymbol? EnclosingType(
        ExpressionSyntax call, SemanticModel semanticModel, CancellationToken ct) =>
        call.FirstAncestorOrSelf<TypeDeclarationSyntax>() is { } declaration
            ? semanticModel.GetDeclaredSymbol(declaration, ct)
            : null;

    /// <summary>The <c>T</c> of the receiver's <c>IStringLocalizer&lt;T&gt;</c>.</summary>
    private static string? TypeArgument(
        ExpressionSyntax call, SemanticModel semanticModel, CancellationToken ct)
    {
        var receiver = call switch
        {
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member } => member.Expression,
            ElementAccessExpressionSyntax access => access.Expression,
            _ => null,
        };

        if (receiver is null || semanticModel.GetTypeInfo(receiver, ct).Type is not INamedTypeSymbol type)
            return null;

        if (type.TypeArguments is [var direct, ..])
            return direct.ToDisplayString(s_declarationName);

        // The member may be declared on a non-generic interface the receiver reaches through a
        // generic one, which is where IStringLocalizer<T> keeps its type argument.
        foreach (var contract in type.AllInterfaces)
        {
            if (contract.TypeArguments is [var argument, ..])
                return argument.ToDisplayString(s_declarationName);
        }

        return null;
    }

    private static void Add(
        ImmutableArray<ResourceFamily>.Builder families, HashSet<string> seen, ResourceFamily family)
    {
        if (seen.Add(family.Directory + Path.DirectorySeparatorChar + family.BaseName))
            families.Add(ResourceCatalogService.Load(family));
    }

    private static bool Defines(ImmutableArray<ResourceFamily>.Builder families, string key)
    {
        foreach (var family in families)
        {
            foreach (var file in family.Files)
            {
                if (file.Entries.ContainsKey(key))
                    return true;
            }
        }

        return false;
    }

    private static string Combine(string directory, string folder) =>
        PathHelper.NormalizePath(Path.Combine(directory, folder));

    // ---- Locating a caret ---------------------------------------------------------------------------

    /// <summary>
    /// The key at a position in any of the three file kinds that can carry one, narrowed to the
    /// families that declare it — or null when the position is not on a key.
    /// </summary>
    /// <remarks>
    /// Narrowed, unlike <see cref="KeyAtAsync"/>: hover and the missing-key diagnostic exist to
    /// report a key nothing declares, where a rename of one would be a rename of nothing and a
    /// find-references would report the caret back to itself.
    /// </remarks>
    public static async Task<ResourceKeyTarget?> LocateAsync(
        ResourceSettings settings, string filePath, int offset, Project? project, CancellationToken ct)
    {
        if (filePath.EndsWith(".resx", StringComparison.OrdinalIgnoreCase))
            return DeclaredKey(settings, filePath, offset, project);

        if (AspxDocumentService.IsAspxFile(filePath))
            return await MarkupKeyAsync(settings, filePath, offset, ct);

        if (filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return await CodeKeyAsync(settings, filePath, offset, project, ct);

        return null;
    }

    /// <summary>A caret on a <c>name=</c> attribute in the <c>.resx</c> itself.</summary>
    private static ResourceKeyTarget? DeclaredKey(
        ResourceSettings settings, string filePath, int offset, Project? project)
    {
        if (ResourceCatalogService.Text(filePath) is not { } text)
            return null;

        // An entry the reader could not span carries an entity reference in its name. It has no
        // range to offer prepareRename and none a rename could replace, so the caret finds nothing
        // rather than something approximate.
        var entry = ResourceCatalogService.ReadContents(filePath, text).Entries.Values
            .FirstOrDefault(e => !e.KeySpan.IsEmpty && Touches(e.KeySpan, offset));

        if (entry.Key is not { Length: > 0 } key
            || ResourceDocuments.FamilyOf(filePath, settings.Discovery.Overrides) is not { } family)
        {
            return null;
        }

        return new ResourceKeyTarget
        {
            Key = key,
            Group = false,
            Families = [ResourceCatalogService.Load(family)],
            Confidence = RootConfidence.Exact,
            Project = project,
            FilePath = filePath,
            Text = text,
            Span = entry.KeySpan,
            KeySuffix = null,
        };
    }

    /// <summary>A caret on an expression-builder argument or an implicit-localization attribute.</summary>
    private static async Task<ResourceKeyTarget?> MarkupKeyAsync(
        ResourceSettings settings, string filePath, int offset, CancellationToken ct)
    {
        var document = await AspxDocumentService.GetAsync(filePath, ct);
        if (document is null || AspxSymbolResolver.ResolveAt(document, offset) is not { } hit)
            return null;

        var catalog = await ProjectIndexCacheService.GetResourceCatalogAsync(
            document.Project, settings.Discovery, ct);

        if (AspxResourceService.Reference(document, catalog, hit) is not { HasKey: true } reference
            || reference.Form is not (AspxResourceForm.Key or AspxResourceForm.ImplicitKey))
        {
            return null;
        }

        bool group = reference.Form is AspxResourceForm.ImplicitKey;
        var families = Declaring(reference.Families, reference.Key, group);

        if (families.IsEmpty)
            return null;

        return new ResourceKeyTarget
        {
            Key = reference.Key,
            Group = group,
            Families = families,
            Confidence = RootConfidence.Exact,
            Project = document.Project,
            FilePath = document.FilePath,
            Text = document.SourceText,
            Span = KeySpan(hit.Span, document.SourceText, reference),
            KeySuffix = null,
        };
    }

    /// <summary>A caret in a string literal a configured lookup reads as its key.</summary>
    private static async Task<ResourceKeyTarget?> CodeKeyAsync(
        ResourceSettings settings, string filePath, int offset, Project? project, CancellationToken ct)
    {
        var document = project?.Documents.FirstOrDefault(
                d => string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            ?? await LspDocumentResolver.ResolveAsync(filePath, ct);

        if (document is null || await document.GetSyntaxRootAsync(ct) is not { } root)
            return null;

        var token = root.FindToken(offset);
        if (!token.IsKind(SyntaxKind.StringLiteralToken) || !Touches(token.Span, offset))
            return null;

        // Whether the literal is a key at all is answered before the catalog is asked for, because
        // asking for one walks the project's directories and every prepareRename on any string in
        // any file would otherwise pay for that walk.
        if (await document.GetSemanticModelAsync(ct) is not { } semanticModel
            || !IsKeyLiteral(settings, semanticModel, token, ct))
        {
            return null;
        }

        var catalog = await ProjectIndexCacheService.GetResourceCatalogAsync(
            document.Project, settings.Discovery, ct);

        if (await KeyAtAsync(settings, catalog, document.Project, semanticModel, token, ct)
            is not { } match)
        {
            return null;
        }

        var families = Declaring(match.Candidates, match.Key, group: false);
        if (families.IsEmpty)
            return null;

        return new ResourceKeyTarget
        {
            Key = match.Key,
            Group = false,
            Families = families,
            Confidence = match.Confidence,
            Project = document.Project,
            FilePath = document.FilePath!,
            Text = await document.GetTextAsync(ct),
            Span = match.Span,
            KeySuffix = match.Suffix,
        };
    }

    // ---- Every site of a key ------------------------------------------------------------------------

    /// <summary>
    /// Every place the target is written, declarations included, and whether all of them could be
    /// spanned exactly.
    /// </summary>
    /// <remarks>
    /// The completeness flag is what a rename gates on. Rewriting the call sites of a key whose
    /// declaration could not be moved leaves every one of them naming an entry that no longer
    /// exists, which is worse than declining the gesture.
    /// </remarks>
    public static async Task<(ImmutableArray<ResourceKeySite> Sites, bool Complete)> CollectAsync(
        ResourceSettings settings, ResourceKeyTarget target, CancellationToken ct)
    {
        var sites = new List<ResourceKeySite>();
        var seen = new HashSet<(string, int, int)>();

        void Add(ResourceKeySite site)
        {
            if (seen.Add((site.FilePath, site.Span.Start, site.Span.Length)))
                sites.Add(site);
        }

        bool complete = Declarations(target, Add);

        foreach (var project in Scope(target))
        {
            ct.ThrowIfCancellationRequested();
            complete &= await CodeSitesAsync(settings, target, project, Add, ct);
            await MarkupSitesAsync(settings, target, project, Add, ct);
        }

        return ([.. sites], complete);
    }

    /// <summary>The <c>name=</c> attribute in every file of every family that declares the key.</summary>
    private static bool Declarations(ResourceKeyTarget target, Action<ResourceKeySite> add)
    {
        bool complete = true;

        foreach (var family in target.Families)
        {
            foreach (var file in family.Files)
            {
                if (ResourceCatalogService.Text(file.FilePath) is not { } text)
                {
                    complete = false;
                    continue;
                }

                foreach (var entry in file.Entries.Values)
                {
                    if (!Covers(target, entry.Key))
                        continue;

                    if (entry.KeySpan.Length != entry.Key.Length)
                    {
                        complete = false;
                        continue;
                    }

                    add(new ResourceKeySite(
                        file.FilePath, text,
                        new TextSpan(entry.KeySpan.Start, target.Key.Length),
                        KeySuffix: null));
                }
            }
        }

        return complete;
    }

    private static async Task<bool> CodeSitesAsync(
        ResourceSettings settings, ResourceKeyTarget target, Project project,
        Action<ResourceKeySite> add, CancellationToken ct)
    {
        if (settings.Lookups.IsDefaultOrEmpty)
            return true;

        var written = WrittenForms(settings, target);
        var catalog = await ProjectIndexCacheService.GetResourceCatalogAsync(
            project, settings.Discovery, ct);
        bool complete = true;

        foreach (var document in project.Documents)
        {
            ct.ThrowIfCancellationRequested();

            if (document.FilePath is not { Length: > 0 } path)
                continue;

            // The bind is the expensive half, so a file the key does not appear in at all never
            // reaches the syntax walk, let alone the semantic model.
            var text = await document.GetTextAsync(ct);
            if (!Mentions(text, written) || await document.GetSyntaxRootAsync(ct) is not { } root)
                continue;

            SemanticModel? semanticModel = null;

            foreach (var token in root.DescendantTokens())
            {
                if (!token.IsKind(SyntaxKind.StringLiteralToken) || !Plausible(token.ValueText, target))
                    continue;

                semanticModel ??= await document.GetSemanticModelAsync(ct);
                if (semanticModel is null)
                    break;

                if (await KeyAtAsync(settings, catalog, project, semanticModel, token, ct)
                        is not { } match
                    || !Covers(target, match.Key)
                    || !Intersects(match.Candidates, target.Families))
                {
                    continue;
                }

                if (match.Span.Length < target.Key.Length)
                {
                    complete = false;
                    continue;
                }

                add(new ResourceKeySite(
                    path, text,
                    target.Group ? new TextSpan(match.Span.Start, target.Key.Length) : match.Span,
                    target.Group ? null : match.Suffix));
            }
        }

        return complete;
    }

    private static async Task MarkupSitesAsync(
        ResourceSettings settings, ResourceKeyTarget target, Project project,
        Action<ResourceKeySite> add, CancellationToken ct)
    {
        if (!await AspxReferenceService.HostsWebFormsAsync(project, ct))
            return;

        var written = WrittenForms(settings, target);
        var catalog = await ProjectIndexCacheService.GetResourceCatalogAsync(
            project, settings.Discovery, ct);

        foreach (string file in AspxReferenceService.EnumerateFiles(project))
        {
            ct.ThrowIfCancellationRequested();

            if (ResourceCatalogService.Text(file) is not { } text || !Mentions(text, written))
                continue;

            var document = await AspxDocumentService.GetAsync(file, ct);
            if (document?.Tree is not { } root)
                continue;

            foreach (var (prefix, argument, _) in AspxResourceService.Builders(root))
            {
                if (AspxResourceService.Builder(
                        document, catalog, prefix.Value.Trim(), argument.Value.Trim())
                    is { HasKey: true, Form: AspxResourceForm.Key } reference
                    && Covers(target, reference.Key)
                    && Intersects(reference.Families, target.Families))
                {
                    add(new ResourceKeySite(
                        document.FilePath, document.SourceText,
                        KeySpan(AspxSymbolResolver.Span(argument.Range), document.SourceText, reference),
                        KeySuffix: null));
                }
            }

            // An implicit-localization attribute names a group, so it is a mention of one only when
            // the caret named a group too — renaming the single entry `btnSave.Text` leaves the
            // `meta:resourcekey="btnSave"` that reaches it alone.
            if (!target.Group)
                continue;

            foreach (var element in AspxSymbolResolver.EnumerateElements(root))
            {
                foreach (var (name, value) in element.RawAttributes)
                {
                    if (!AspxSymbolResolver.IsImplicitKeyAttribute(name.Value))
                        continue;

                    var reference = AspxResourceService.Implicit(
                        document, catalog, name.Value, value.Value, element);

                    if (reference.HasKey
                        && reference.Key.Equals(target.Key, StringComparison.Ordinal)
                        && Intersects(reference.Families, target.Families))
                    {
                        add(new ResourceKeySite(
                            document.FilePath, document.SourceText,
                            KeySpan(AspxSymbolResolver.Span(value.Range), document.SourceText, reference),
                            KeySuffix: null));
                    }
                }
            }
        }
    }

    /// <summary>
    /// The projects worth searching: the caret's own, plus any whose directory contains a family the
    /// key lives in. A key in one web project's <c>App_LocalResources</c> is not written in a
    /// sibling class library, and proving that by walking every project of a large solution is the
    /// difference between a rename that returns and one that does not.
    /// </summary>
    private static IEnumerable<Project> Scope(ResourceKeyTarget target)
    {
        if (target.Project is not { } project)
            yield break;

        yield return project;

        foreach (var other in project.Solution.Projects)
        {
            if (other.Id == project.Id
                || Path.GetDirectoryName(other.FilePath) is not { Length: > 0 } directory)
            {
                continue;
            }

            if (target.Families.Any(family => IsUnder(family.Directory, directory)))
                yield return other;
        }
    }

    // ---- Keys ---------------------------------------------------------------------------------------

    /// <summary>
    /// The entry a written key resolves to. DNN appends its suffix when
    /// <c>key.IndexOf('.') &lt; 1</c>, so a key that starts with a dot gets it too.
    /// </summary>
    public static string EffectiveKey(string written, string? suffix) =>
        suffix is { Length: > 0 } && written.IndexOf('.') < 1 ? written + suffix : written;

    /// <summary>
    /// The inverse: how a site whose lookup appends <paramref name="suffix"/> has to write
    /// <paramref name="key"/> for it to resolve back to exactly that entry.
    /// </summary>
    public static string WrittenForm(string key, string? suffix)
    {
        if (suffix is not { Length: > 0 } || !key.EndsWith(suffix, StringComparison.Ordinal))
            return key;

        string trimmed = key[..^suffix.Length];
        return trimmed.Length > 0 && trimmed.IndexOf('.') < 1 ? trimmed : key;
    }

    /// <summary>
    /// The key half of a markup argument. For the two-argument <c>Resources</c> form the class name
    /// and the comma belong to neither half; everywhere else the surrounding whitespace does.
    /// </summary>
    private static TextSpan KeySpan(TextSpan argument, SourceText text, AspxResourceReference reference)
    {
        string raw = text.ToString(argument);
        int from = reference.GlobalClass is null ? 0 : raw.IndexOf(',') + 1;

        // The written form rather than the key: `<%$ dnnLoc:Save %>` reads the entry `Save.Text`,
        // and the characters on screen are `Save`.
        string needle = reference.GlobalClass is null ? reference.Written : reference.Key;
        int index = from < 0 || needle.Length == 0 || from > raw.Length
            ? -1
            : raw.IndexOf(needle, from, StringComparison.Ordinal);

        return index < 0 ? argument : new TextSpan(argument.Start + index, needle.Length);
    }

    /// <summary>The families that declare the key, in the order the runtime would reach them.</summary>
    private static ImmutableArray<ResourceFamily> Declaring(
        ImmutableArray<ResourceFamily> families, string key, bool group)
    {
        if (families.IsDefaultOrEmpty)
            return [];

        var declaring = ImmutableArray.CreateBuilder<ResourceFamily>();

        foreach (var family in families)
        {
            var loaded = ResourceCatalogService.Load(family);

            if (loaded.AllKeys.Any(candidate => group ? InGroup(candidate, key) : candidate == key))
                declaring.Add(loaded);
        }

        return declaring.ToImmutable();
    }

    /// <summary>Whether an entry name is the one the caret named, or one its group covers.</summary>
    public static bool Covers(ResourceKeyTarget target, string key) =>
        target.Group ? InGroup(key, target.Key) : key.Equals(target.Key, StringComparison.Ordinal);

    public static bool InGroup(string key, string prefix) =>
        key.StartsWith(prefix, StringComparison.Ordinal)
        && (key.Length == prefix.Length || key[prefix.Length] == '.');

    /// <summary>Whether a literal could become the target under some lookup's suffix rule, which is
    /// what decides if it is worth binding.</summary>
    private static bool Plausible(string value, ResourceKeyTarget target) =>
        target.Group
            ? InGroup(value, target.Key)
            : value.Equals(target.Key, StringComparison.Ordinal)
                || target.Key.StartsWith(value, StringComparison.Ordinal);

    /// <summary>Every way the key can appear in source, for the filter that decides whether a file is
    /// worth opening at all.</summary>
    private static ImmutableArray<string> WrittenForms(
        ResourceSettings settings, ResourceKeyTarget target)
    {
        if (target.Group)
            return [target.Key];

        var forms = ImmutableArray.CreateBuilder<string>();
        forms.Add(target.Key);

        foreach (var lookup in settings.Lookups)
        {
            string written = WrittenForm(target.Key, lookup.DefaultKeySuffix);

            if (written != target.Key && !forms.Contains(written))
                forms.Add(written);
        }

        return forms.ToImmutable();
    }

    /// <summary>How much of a file is searched at a time in <see cref="Mentions"/>.</summary>
    private const int MentionsChunk = 16 * 1024;

    /// <summary>Whether any written form appears in the text at all — the filter that decides if a
    /// file is worth opening. Searched in pooled chunks rather than <c>text.ToString()</c>: this
    /// runs against every document in scope per find-references, and a full-string copy of each
    /// file was the dominant allocation. Chunks overlap by a candidate length so a mention
    /// straddling a boundary is still seen.</summary>
    private static bool Mentions(SourceText text, ImmutableArray<string> candidates)
    {
        int length = text.Length;
        if (length == 0 || candidates.IsDefaultOrEmpty)
            return false;

        int longest = 0;
        foreach (string candidate in candidates)
            longest = Math.Max(longest, candidate.Length);

        if (longest == 0 || longest > length)
            return false;

        char[] buffer = ArrayPool<char>.Shared.Rent(Math.Min(length, MentionsChunk + longest));
        try
        {
            int chunk = Math.Min(buffer.Length, length);
            for (int start = 0; ; start += chunk - (longest - 1))
            {
                int count = Math.Min(chunk, length - start);
                text.CopyTo(start, buffer, 0, count);
                var window = buffer.AsSpan(0, count);

                foreach (string candidate in candidates)
                {
                    if (window.IndexOf(candidate.AsSpan(), StringComparison.Ordinal) >= 0)
                        return true;
                }

                if (start + count >= length)
                    return false;
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    private static bool Intersects(
        ImmutableArray<ResourceFamily> candidates, ImmutableArray<ResourceFamily> families) =>
        !candidates.IsDefaultOrEmpty
        && candidates.Any(candidate => families.Any(family => Same(candidate, family)));

    /// <summary>Identity is the directory and the base name: the materialized family and the shell
    /// the catalog handed out are the same family, and reference equality says otherwise.</summary>
    private static bool Same(ResourceFamily first, ResourceFamily second) =>
        first.BaseName.Equals(second.BaseName, StringComparison.OrdinalIgnoreCase)
        && first.Directory.Equals(second.Directory, StringComparison.OrdinalIgnoreCase);

    /// <summary>Segment-aware, so a family under <c>Web.Tests</c> does not pull in <c>Web</c>.</summary>
    private static bool IsUnder(string path, string directory) =>
        path.Length > directory.Length
        && path.StartsWith(directory, StringComparison.OrdinalIgnoreCase)
        && (path[directory.Length] == Path.DirectorySeparatorChar
            || path[directory.Length] == Path.AltDirectorySeparatorChar);

    /// <summary>End-inclusive, because the caret sits between characters: just past the last
    /// character of a key the user is still on that key.</summary>
    private static bool Touches(TextSpan span, int offset) =>
        offset >= span.Start && offset <= span.End;
}
