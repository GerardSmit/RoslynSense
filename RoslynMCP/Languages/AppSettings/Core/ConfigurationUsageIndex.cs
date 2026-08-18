using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMCP.Languages.AppSettings.Core;

/// <summary>One place C# names a configuration key.</summary>
/// <param name="Path">The full configuration path the site addresses, prefixes from chained
/// <c>GetSection</c> calls included.</param>
/// <param name="Span">The string literal's content, quotes excluded — what a peek window should
/// highlight.</param>
internal sealed record ConfigurationUsage(
    string Path, string FilePath, TextSpan Span, LinePositionSpan LineSpan);

/// <summary>One place C# binds a section to a type — <c>Configure&lt;T&gt;</c>,
/// <c>OptionsBuilder.Bind</c>/<c>BindConfiguration</c>, <c>section.Bind(instance)</c> or
/// <c>section.Get&lt;T&gt;()</c>.</summary>
internal sealed record ConfigurationBinding(
    string SectionPath, INamedTypeSymbol Type,
    string FilePath, TextSpan Span, LinePositionSpan LineSpan);

/// <summary>
/// Every configuration key the C# of a project closure names, and every options type it binds.
/// </summary>
/// <remarks>
/// <para>
/// Built from the settings file's own project plus the projects it references, transitively —
/// the assemblies composed into the running application, which are the ones whose
/// <c>GetSection</c> calls will be answered from this file at runtime. Each project's scan is
/// cached against its dependent semantic version, the <c>DbmlGeneratedIndex</c> pattern: the
/// version is asked for before the compilation, so a cache hit never forces one.
/// </para>
/// <para>
/// The scan is text-contains → syntax → semantics, in that order (the
/// <c>ResourceKeySearch</c> contract): a document that never mentions a configuration API is
/// dismissed for the cost of a string search, and a semantic model is only built for documents
/// whose syntax actually carries a candidate call.
/// </para>
/// </remarks>
internal sealed class ConfigurationUsageIndex
{
    public static readonly ConfigurationUsageIndex Empty = new([], []);

    public ImmutableArray<ConfigurationUsage> Usages { get; }

    public ImmutableArray<ConfigurationBinding> Bindings { get; }

    public bool IsEmpty => Usages.IsEmpty && Bindings.IsEmpty;

    private ConfigurationUsageIndex(
        ImmutableArray<ConfigurationUsage> usages, ImmutableArray<ConfigurationBinding> bindings)
    {
        Usages = usages;
        Bindings = bindings;
    }

    /// <summary>Sites naming exactly this path — key comparisons are case-insensitive, as the
    /// runtime's are.</summary>
    public IEnumerable<ConfigurationUsage> UsagesFor(string path) =>
        Usages.Where(u => string.Equals(u.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Binding sites whose section is exactly this path.</summary>
    public IEnumerable<ConfigurationBinding> BindingsFor(string path) =>
        Bindings.Where(b => string.Equals(b.SectionPath, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The property a key under a bound section maps to: for a binding of <c>Example</c> to
    /// <c>ExampleOptions</c>, the key <c>Example:Retries:Count</c> resolves through
    /// <c>ExampleOptions.Retries</c> to its type's <c>Count</c>. Null when no binding covers the
    /// path or the property chain breaks.
    /// </summary>
    public IPropertySymbol? BoundProperty(string keyPath)
    {
        foreach (var binding in Bindings)
        {
            if (!keyPath.StartsWith(binding.SectionPath + ":", StringComparison.OrdinalIgnoreCase))
                continue;

            if (ResolveProperty(binding.Type, keyPath[(binding.SectionPath.Length + 1)..]) is { } property)
                return property;
        }

        return null;
    }

    /// <summary>The type an object at this path binds to — the binding's own type for the section
    /// itself, or a nested property's type below it. What completion offers properties of.</summary>
    public INamedTypeSymbol? BoundType(string sectionPath)
    {
        foreach (var binding in Bindings)
        {
            if (string.Equals(binding.SectionPath, sectionPath, StringComparison.OrdinalIgnoreCase))
                return binding.Type;

            if (sectionPath.StartsWith(binding.SectionPath + ":", StringComparison.OrdinalIgnoreCase)
                && ResolveProperty(binding.Type, sectionPath[(binding.SectionPath.Length + 1)..])
                    is { Type: INamedTypeSymbol nested })
            {
                return nested;
            }
        }

        return null;
    }

    private static IPropertySymbol? ResolveProperty(INamedTypeSymbol type, string relativePath)
    {
        IPropertySymbol? property = null;
        var current = (ITypeSymbol)type;

        foreach (string segment in relativePath.Split(':'))
        {
            property = Property(current, segment);
            if (property is null)
                return null;

            current = property.Type;
        }

        return property;
    }

    private static IPropertySymbol? Property(ITypeSymbol type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                if (member is IPropertySymbol { DeclaredAccessibility: Accessibility.Public } property
                    && property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return property;
                }
            }
        }

        return null;
    }

    // ---- Building ----------------------------------------------------------------------------

    private sealed record Cached(VersionStamp Version, ConfigurationUsageIndex Index);

    private static readonly ConcurrentDictionary<ProjectId, Cached> s_cache = new();

    /// <summary>The merged index over a project and everything it references.</summary>
    public static async Task<ConfigurationUsageIndex> GetAsync(Project project, CancellationToken ct)
    {
        var usages = ImmutableArray.CreateBuilder<ConfigurationUsage>();
        var bindings = ImmutableArray.CreateBuilder<ConfigurationBinding>();

        foreach (var member in Closure(project))
        {
            var index = await ForProjectAsync(member, ct);
            usages.AddRange(index.Usages);
            bindings.AddRange(index.Bindings);
        }

        return usages.Count == 0 && bindings.Count == 0
            ? Empty
            : new ConfigurationUsageIndex(usages.ToImmutable(), bindings.ToImmutable());
    }

    private static IEnumerable<Project> Closure(Project project)
    {
        var seen = new HashSet<ProjectId>();
        var queue = new Queue<Project>();
        queue.Enqueue(project);
        seen.Add(project.Id);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            yield return current;

            foreach (var reference in current.ProjectReferences)
            {
                if (seen.Add(reference.ProjectId)
                    && current.Solution.GetProject(reference.ProjectId) is { } referenced)
                {
                    queue.Enqueue(referenced);
                }
            }
        }
    }

    private static async Task<ConfigurationUsageIndex> ForProjectAsync(
        Project project, CancellationToken ct)
    {
        if (project.Language != LanguageNames.CSharp)
            return Empty;

        var version = await project.GetDependentSemanticVersionAsync(ct);

        if (s_cache.TryGetValue(project.Id, out var cached) && cached.Version.Equals(version))
            return cached.Index;

        var index = await BuildAsync(project, ct);
        s_cache[project.Id] = new Cached(version, index);
        return index;
    }

    /// <summary>What a document must mention before its syntax is worth walking.</summary>
    private static readonly string[] s_needles =
    [
        "GetSection", "GetRequiredSection", "GetValue", "GetConnectionString",
        "BindConfiguration", "AddOptions", "Configure<", ".Bind(", ".Get<", "Configuration[",
    ];

    private static async Task<ConfigurationUsageIndex> BuildAsync(Project project, CancellationToken ct)
    {
        var usages = ImmutableArray.CreateBuilder<ConfigurationUsage>();
        var bindings = ImmutableArray.CreateBuilder<ConfigurationBinding>();

        foreach (var document in project.Documents)
        {
            ct.ThrowIfCancellationRequested();

            if (document.FilePath is not { Length: > 0 } filePath)
                continue;

            var text = await document.GetTextAsync(ct);
            string content = text.ToString();

            if (!s_needles.Any(needle => content.Contains(needle, StringComparison.Ordinal)))
                continue;

            if (await document.GetSyntaxRootAsync(ct) is not { } root)
                continue;

            // Syntax first, semantics once: collect the shapes worth asking about, and only
            // build the model when the document has at least one.
            var invocations = new List<(InvocationExpressionSyntax Invocation, string Name)>();
            var indexers = new List<(ElementAccessExpressionSyntax Access, LiteralExpressionSyntax Literal)>();

            foreach (var node in root.DescendantNodes())
            {
                switch (node)
                {
                    case InvocationExpressionSyntax invocation:
                        if (MethodName(invocation) is { } name
                            && name is "GetSection" or "GetRequiredSection" or "GetValue"
                                or "GetConnectionString" or "Configure" or "Bind"
                                or "BindConfiguration" or "Get")
                        {
                            invocations.Add((invocation, name));
                        }
                        break;

                    case ElementAccessExpressionSyntax
                    {
                        ArgumentList.Arguments: [{ Expression: LiteralExpressionSyntax literal }],
                    } access when literal.IsKind(SyntaxKind.StringLiteralExpression):
                        indexers.Add((access, literal));
                        break;
                }
            }

            if (invocations.Count == 0 && indexers.Count == 0)
                continue;

            if (await document.GetSemanticModelAsync(ct) is not { } model)
                continue;

            foreach (var (invocation, name) in invocations)
            {
                ct.ThrowIfCancellationRequested();
                Scan(invocation, name, model, filePath, usages, bindings);
            }

            foreach (var (access, literal) in indexers)
            {
                if (SectionPrefix(access.Expression, model) is { } prefix)
                    AddUsage(usages, Combine(prefix, literal.Token.ValueText), literal, filePath);
            }
        }

        return usages.Count == 0 && bindings.Count == 0
            ? Empty
            : new ConfigurationUsageIndex(usages.ToImmutable(), bindings.ToImmutable());
    }

    private static string? MethodName(InvocationExpressionSyntax invocation) =>
        invocation.Expression switch
        {
            MemberAccessExpressionSyntax { Name: { } name } => name.Identifier.Text,
            _ => null,
        };

    private static void Scan(
        InvocationExpressionSyntax invocation, string name, SemanticModel model, string filePath,
        ImmutableArray<ConfigurationUsage>.Builder usages,
        ImmutableArray<ConfigurationBinding>.Builder bindings)
    {
        var receiver = ((MemberAccessExpressionSyntax)invocation.Expression).Expression;

        switch (name)
        {
            case "GetSection" or "GetRequiredSection" or "GetValue" or "GetConnectionString":
            {
                if (FirstStringLiteral(invocation) is not { } literal
                    || SectionPrefix(receiver, model) is not { } prefix)
                {
                    return;
                }

                string key = name is "GetConnectionString"
                    ? "ConnectionStrings:" + literal.Token.ValueText
                    : literal.Token.ValueText;

                AddUsage(usages, Combine(prefix, key), literal, filePath);
                return;
            }

            case "Configure":
            {
                // services.Configure<TOptions>(section) and the named-options overload with the
                // name first. The lambda overloads carry no section argument and fall out here.
                if (BoundTypeArgument(invocation, model) is not { } type)
                    return;

                foreach (var argument in invocation.ArgumentList.Arguments)
                {
                    if (SectionArgumentPath(argument.Expression, model) is { Length: > 0 } path)
                    {
                        AddBinding(bindings, path, type, argument.Expression, filePath);
                        return;
                    }
                }

                return;
            }

            case "BindConfiguration":
            {
                // AddOptions<TOptions>().BindConfiguration("Section") — the path is the literal
                // and the type is the receiver's OptionsBuilder<T> argument.
                if (FirstStringLiteral(invocation) is not { } literal
                    || OptionsBuilderType(receiver, model) is not { } type)
                {
                    return;
                }

                AddBinding(bindings, literal.Token.ValueText, type, literal, filePath);
                AddUsage(usages, literal.Token.ValueText, literal, filePath);
                return;
            }

            case "Bind":
            {
                // Two shapes share the name: OptionsBuilder<T>.Bind(section), and
                // ConfigurationBinder's section.Bind(instance).
                if (OptionsBuilderType(receiver, model) is { } optionsType)
                {
                    foreach (var argument in invocation.ArgumentList.Arguments)
                    {
                        if (SectionArgumentPath(argument.Expression, model) is { Length: > 0 } path)
                        {
                            AddBinding(bindings, path, optionsType, argument.Expression, filePath);
                            return;
                        }
                    }

                    return;
                }

                if (SectionPrefix(receiver, model) is not { Length: > 0 } sectionPath
                    || invocation.ArgumentList.Arguments.Count == 0)
                {
                    return;
                }

                if (model.GetTypeInfo(invocation.ArgumentList.Arguments[0].Expression).Type
                    is INamedTypeSymbol instanceType && instanceType.SpecialType == SpecialType.None)
                {
                    AddBinding(
                        bindings, sectionPath, instanceType,
                        invocation.ArgumentList.Arguments[0].Expression, filePath);
                }

                return;
            }

            case "Get":
            {
                // section.Get<TOptions>() — a one-shot bind.
                if (BoundTypeArgument(invocation, model) is not { } type
                    || SectionPrefix(receiver, model) is not { Length: > 0 } sectionPath)
                {
                    return;
                }

                AddBinding(bindings, sectionPath, type, invocation, filePath);
                return;
            }
        }
    }

    private static void AddUsage(
        ImmutableArray<ConfigurationUsage>.Builder usages, string path,
        LiteralExpressionSyntax literal, string filePath)
    {
        if (path.Length == 0)
            return;

        var span = ContentSpan(literal);
        usages.Add(new ConfigurationUsage(
            path, filePath, span, literal.SyntaxTree.GetLineSpan(span).Span));
    }

    private static void AddBinding(
        ImmutableArray<ConfigurationBinding>.Builder bindings, string path, INamedTypeSymbol type,
        SyntaxNode site, string filePath)
    {
        bindings.Add(new ConfigurationBinding(
            path, type, filePath, site.Span, site.SyntaxTree.GetLineSpan(site.Span).Span));
    }

    /// <summary>The literal's content without its quotes — what a peek should highlight.</summary>
    private static TextSpan ContentSpan(LiteralExpressionSyntax literal)
    {
        var token = literal.Token;
        string text = token.Text;

        // Verbatim and raw strings shift the content differently; the token text says how far.
        int open = text.IndexOf('"') + 1;
        return token.Text.Length >= open + 1 && token.Text.EndsWith("\"", StringComparison.Ordinal)
            ? new TextSpan(token.SpanStart + open, Math.Max(0, token.Span.Length - open - 1))
            : token.Span;
    }

    private static LiteralExpressionSyntax? FirstStringLiteral(InvocationExpressionSyntax invocation)
    {
        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            if (argument.Expression is LiteralExpressionSyntax literal
                && literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                return literal;
            }
        }

        return null;
    }

    /// <summary>The single type argument of a generic call, when it is a bindable named type.</summary>
    private static INamedTypeSymbol? BoundTypeArgument(
        InvocationExpressionSyntax invocation, SemanticModel model)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax
            {
                Name: GenericNameSyntax { TypeArgumentList.Arguments: [{ } typeSyntax] },
            })
        {
            return null;
        }

        return model.GetTypeInfo(typeSyntax).Type is INamedTypeSymbol
        {
            SpecialType: SpecialType.None, TypeKind: TypeKind.Class or TypeKind.Struct,
        } type
            ? type
            : null;
    }

    /// <summary>The <c>T</c> of an <c>OptionsBuilder&lt;T&gt;</c>-typed expression, or null.</summary>
    private static INamedTypeSymbol? OptionsBuilderType(ExpressionSyntax expression, SemanticModel model)
    {
        return model.GetTypeInfo(expression).Type is INamedTypeSymbol
        {
            Name: "OptionsBuilder", TypeArguments: [INamedTypeSymbol argument],
        } builder && builder.ContainingNamespace.ToDisplayString() == "Microsoft.Extensions.Options"
            ? argument
            : null;
    }

    /// <summary>The configuration path an argument expression addresses, for binding calls that
    /// take a section: <c>config.GetSection("X")</c> inline, or a variable holding one.</summary>
    private static string? SectionArgumentPath(ExpressionSyntax expression, SemanticModel model) =>
        ResolvePath(expression, model, depth: 0) is { Length: > 0 } path ? path : null;

    /// <summary>
    /// The path prefix a receiver contributes: empty for a configuration root, the accumulated
    /// section path for chained <c>GetSection</c> calls, and null when the expression is not
    /// configuration at all — or is a section whose path cannot be seen from here.
    /// </summary>
    private static string? SectionPrefix(ExpressionSyntax expression, SemanticModel model) =>
        ResolvePath(expression, model, depth: 0);

    private static string? ResolvePath(ExpressionSyntax expression, SemanticModel model, int depth)
    {
        if (depth > 8)
            return null;

        switch (expression)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return ResolvePath(parenthesized.Expression, model, depth + 1);

            case InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax
                {
                    Name.Identifier.Text: "GetSection" or "GetRequiredSection",
                    Expression: { } inner,
                },
            } chained when FirstStringLiteral(chained) is { } literal:
            {
                return ResolvePath(inner, model, depth + 1) is { } prefix
                    ? Combine(prefix, literal.Token.ValueText)
                    : null;
            }

            case IdentifierNameSyntax or MemberAccessExpressionSyntax:
            {
                // A local that holds a section still has a knowable path when its initializer
                // does: var section = config.GetSection("A"); section.Bind(options);
                if (model.GetSymbolInfo(expression).Symbol is ILocalSymbol
                    {
                        DeclaringSyntaxReferences: [{ } reference]
                    }
                    && reference.GetSyntax() is VariableDeclaratorSyntax
                    {
                        Initializer.Value: { } initializer,
                    }
                    && initializer.SyntaxTree == expression.SyntaxTree
                    && ResolvePath(initializer, model, depth + 1) is { } fromInitializer)
                {
                    return fromInitializer;
                }

                // An unreadable initializer does not disqualify the local; its declared type
                // still says whether it is a root.
                return RootOrUnknown(expression, model);
            }

            default:
                return RootOrUnknown(expression, model);
        }
    }

    /// <summary>Empty for a configuration root, null for a section reached some way this scan
    /// cannot see through — and null for everything that is not configuration.</summary>
    private static string? RootOrUnknown(ExpressionSyntax expression, SemanticModel model)
    {
        if (model.GetTypeInfo(expression).Type is not { } type || !IsConfiguration(type))
            return null;

        // A bare IConfigurationSection has a path this scan cannot recover; a root has none.
        return IsSection(type) ? null : "";
    }

    private static bool IsConfiguration(ITypeSymbol type) =>
        IsConfigurationInterface(type) || type.AllInterfaces.Any(IsConfigurationInterface);

    private static bool IsConfigurationInterface(ITypeSymbol type) =>
        type is { Name: "IConfiguration" or "IConfigurationRoot" or "IConfigurationSection" }
        && type.ContainingNamespace.ToDisplayString() == "Microsoft.Extensions.Configuration";

    private static bool IsSection(ITypeSymbol type) =>
        type.Name == "IConfigurationSection"
        || type.AllInterfaces.Any(i => i.Name == "IConfigurationSection"
            && i.ContainingNamespace.ToDisplayString() == "Microsoft.Extensions.Configuration");

    private static string Combine(string prefix, string key) =>
        prefix.Length == 0 ? key : prefix + ":" + key;

    internal static void Clear() => s_cache.Clear();
}
