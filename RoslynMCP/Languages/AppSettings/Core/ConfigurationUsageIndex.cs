using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services;

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
/// One method of the solution's own that reads whatever key it is handed — <c>GetSetting(name)</c>
/// over <c>IConfiguration</c>, the wrapper a codebase writes once and then calls everywhere.
/// </summary>
/// <param name="Id">Declaring type, method name and parameter, which is what a call in another
/// project can be matched against.</param>
/// <param name="Prefix">The path the wrapper's own read is rooted at, so a wrapper over
/// <c>GetSection("Widget")</c> resolves its callers' keys inside that section.</param>
internal sealed record ConfigurationForwarder(
    string Id, string MethodName, int ParameterIndex, string Prefix);

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
    public static readonly ConfigurationUsageIndex Empty = new([], [], []);

    public ImmutableArray<ConfigurationUsage> Usages { get; }

    public ImmutableArray<ConfigurationBinding> Bindings { get; }

    /// <summary>The reading methods the solution wrote for itself, whose callers are usages too.</summary>
    public ImmutableArray<ConfigurationForwarder> Forwarders { get; }

    public bool IsEmpty => Usages.IsEmpty && Bindings.IsEmpty;

    private ConfigurationUsageIndex(
        ImmutableArray<ConfigurationUsage> usages, ImmutableArray<ConfigurationBinding> bindings,
        ImmutableArray<ConfigurationForwarder> forwarders)
    {
        Usages = usages;
        Bindings = bindings;
        Forwarders = forwarders;
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

    private static readonly ConcurrentDictionary<(ProjectId, string), Cached> s_forwardedCache = new();

    /// <summary>The merged index over a project and everything it references.</summary>
    /// <remarks>
    /// Two passes, because the second one needs the first one's answer from every project at once.
    /// The first finds the reads the framework's own shapes make visible, and with them the
    /// wrapper methods that hand a parameter to one. The second finds the calls to those wrappers
    /// — which may be anywhere in the closure, including in projects scanned before the wrapper
    /// was known — and the wrappers <em>those</em> calls reveal in turn, until a round adds
    /// nothing or <see cref="ConfigForwarding.MaxDepth"/> rounds have run.
    /// </remarks>
    public static async Task<ConfigurationUsageIndex> GetAsync(Project project, CancellationToken ct)
    {
        var usages = ImmutableArray.CreateBuilder<ConfigurationUsage>();
        var bindings = ImmutableArray.CreateBuilder<ConfigurationBinding>();
        var forwarders = new Dictionary<string, ConfigurationForwarder>(StringComparer.Ordinal);

        var closure = ApplicationClosure.Of(project).ToList();

        foreach (var member in closure)
        {
            var index = await ForProjectAsync(member, ct);
            usages.AddRange(index.Usages);
            bindings.AddRange(index.Bindings);

            foreach (var forwarder in index.Forwarders)
                forwarders.TryAdd(forwarder.Id, forwarder);
        }

        var scanned = new HashSet<string>(StringComparer.Ordinal);

        for (int round = 0; round < ConfigForwarding.MaxDepth && forwarders.Count > scanned.Count; round++)
        {
            var pending = forwarders.Values.Where(f => scanned.Add(f.Id)).ToImmutableArray();

            foreach (var member in closure)
            {
                var index = await ForwardedAsync(member, pending, ct);
                usages.AddRange(index.Usages);

                foreach (var forwarder in index.Forwarders)
                    forwarders.TryAdd(forwarder.Id, forwarder);
            }
        }

        return usages.Count == 0 && bindings.Count == 0
            ? Empty
            : new ConfigurationUsageIndex(usages.ToImmutable(), bindings.ToImmutable(), []);
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

    /// <summary>One project's calls to a known set of wrapper methods.</summary>
    /// <remarks>
    /// Cached against the project's semantic version <em>and</em> the set asked about, since the
    /// same project answers differently once another project's wrapper is known.
    /// </remarks>
    private static async Task<ConfigurationUsageIndex> ForwardedAsync(
        Project project, ImmutableArray<ConfigurationForwarder> forwarders, CancellationToken ct)
    {
        if (project.Language != LanguageNames.CSharp || forwarders.IsEmpty)
            return Empty;

        var version = await project.GetDependentSemanticVersionAsync(ct);
        string key = string.Join("|", forwarders.Select(f => f.Id).OrderBy(id => id, StringComparer.Ordinal));

        if (s_forwardedCache.TryGetValue((project.Id, key), out var cached)
            && cached.Version.Equals(version))
        {
            return cached.Index;
        }

        var index = await BuildForwardedAsync(project, forwarders, ct);
        s_forwardedCache[(project.Id, key)] = new Cached(version, index);
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
        var forwarders = ImmutableArray.CreateBuilder<ConfigurationForwarder>();

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
            var passthroughs = new List<(ExpressionSyntax Receiver, ExpressionSyntax Key)>();

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

                    // config["Key"], and config[key] where key is the enclosing method's own
                    // parameter — the second reads nothing nameable itself, it is the wrapper
                    // whose callers do.
                    case ElementAccessExpressionSyntax
                    {
                        ArgumentList.Arguments: [{ Expression: { } argument }],
                    } access:
                        if (argument is LiteralExpressionSyntax literal
                            && literal.IsKind(SyntaxKind.StringLiteralExpression))
                        {
                            indexers.Add((access, literal));
                        }
                        else if (argument is IdentifierNameSyntax)
                        {
                            passthroughs.Add((access.Expression, argument));
                        }

                        break;
                }
            }

            if (invocations.Count == 0 && indexers.Count == 0 && passthroughs.Count == 0)
                continue;

            if (await document.GetSemanticModelAsync(ct) is not { } model)
                continue;

            foreach (var (invocation, name) in invocations)
            {
                ct.ThrowIfCancellationRequested();
                Scan(invocation, name, model, filePath, usages, bindings, forwarders);
            }

            foreach (var (access, literal) in indexers)
            {
                if (SectionPrefix(access.Expression, model) is { } prefix)
                    AddUsage(usages, Combine(prefix, literal.Token.ValueText), literal, filePath);
            }

            foreach (var (receiver, key) in passthroughs)
            {
                if (SectionPrefix(receiver, model) is { } prefix)
                    AddForwarder(forwarders, key, model, prefix);
            }
        }

        return usages.Count == 0 && bindings.Count == 0 && forwarders.Count == 0
            ? Empty
            : new ConfigurationUsageIndex(
                usages.ToImmutable(), bindings.ToImmutable(), forwarders.ToImmutable());
    }

    /// <summary>
    /// Every call in this project to one of the wrapper methods, counted as a read of the key it
    /// passes — and every method that hands its own parameter to one, which is a wrapper too.
    /// </summary>
    /// <remarks>
    /// The wrapper's name is the needle here, where the first pass used the framework's. It is the
    /// only cheap gate available: nothing at a call site says a configuration read is happening,
    /// which is exactly why the wrapper had to be found first.
    /// </remarks>
    private static async Task<ConfigurationUsageIndex> BuildForwardedAsync(
        Project project, ImmutableArray<ConfigurationForwarder> forwarders, CancellationToken ct)
    {
        var byId = forwarders.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var names = new HashSet<string>(forwarders.Select(f => f.MethodName), StringComparer.Ordinal);

        var usages = ImmutableArray.CreateBuilder<ConfigurationUsage>();
        var found = ImmutableArray.CreateBuilder<ConfigurationForwarder>();

        foreach (var document in project.Documents)
        {
            ct.ThrowIfCancellationRequested();

            if (document.FilePath is not { Length: > 0 } filePath)
                continue;

            var text = await document.GetTextAsync(ct);
            string content = text.ToString();

            if (!names.Any(name => content.Contains(name, StringComparison.Ordinal)))
                continue;

            if (await document.GetSyntaxRootAsync(ct) is not { } root)
                continue;

            var candidates = root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(invocation =>
                    ConfigForwarding.InvokedName(invocation) is { } name && names.Contains(name))
                .ToList();

            if (candidates.Count == 0)
                continue;

            if (await document.GetSemanticModelAsync(ct) is not { } model)
                continue;

            foreach (var invocation in candidates)
            {
                ct.ThrowIfCancellationRequested();

                if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method)
                    continue;

                string key = ConfigForwarding.Key(method);

                foreach (var argument in invocation.ArgumentList.Arguments)
                {
                    if (ConfigForwarding.Callee(argument.Expression, model) is not { } callee
                        || !byId.TryGetValue(Id(key, callee.Index), out var forwarder))
                    {
                        continue;
                    }

                    if (argument.Expression is LiteralExpressionSyntax literal
                        && literal.IsKind(SyntaxKind.StringLiteralExpression))
                    {
                        AddUsage(
                            usages, Combine(forwarder.Prefix, literal.Token.ValueText),
                            literal, filePath);
                    }
                    else
                    {
                        // A method handing its own parameter to a wrapper is one itself, and the
                        // next round goes looking for its callers.
                        AddForwarder(found, argument.Expression, model, forwarder.Prefix);
                    }
                }
            }
        }

        return usages.Count == 0 && found.Count == 0
            ? Empty
            : new ConfigurationUsageIndex(usages.ToImmutable(), [], found.ToImmutable());
    }

    /// <summary>Records the method a key expression belongs to as a wrapper, when the expression
    /// is one of that method's own parameters.</summary>
    private static void AddForwarder(
        ImmutableArray<ConfigurationForwarder>.Builder forwarders, ExpressionSyntax key,
        SemanticModel model, string prefix)
    {
        if (ConfigForwarding.ForwardedParameter(key, model) is not { } forwarded)
            return;

        var (method, index) = forwarded;
        string id = Id(ConfigForwarding.Key(method), index);

        if (!forwarders.Any(f => f.Id == id))
            forwarders.Add(new ConfigurationForwarder(id, method.Name, index, prefix));
    }

    private static string Id(string key, int parameterIndex) => key + "#" + parameterIndex;

    /// <summary>
    /// The configuration path a literal addresses, or null when it is not a configuration read at
    /// all — the question an editor gesture on one literal asks, where the index above asks it of
    /// a whole project.
    /// </summary>
    /// <remarks>
    /// The same shapes <see cref="BuildAsync"/> collects, walked from the literal outwards instead
    /// of from the call inwards: the string argument of a <c>GetSection</c>-family call, and the
    /// argument of an indexer on a configuration object. Chained sections still contribute their
    /// prefix, so <c>config.GetSection("A")["B"]</c> answers <c>A:B</c>.
    /// </remarks>
    public static string? PathOfRead(LiteralExpressionSyntax literal, SemanticModel model)
    {
        if (!literal.IsKind(SyntaxKind.StringLiteralExpression)
            || literal.Parent is not ArgumentSyntax argument)
        {
            return null;
        }

        switch (argument.Parent?.Parent)
        {
            case ElementAccessExpressionSyntax access:
            {
                return SectionPrefix(access.Expression, model) is { } prefix
                    ? Combine(prefix, literal.Token.ValueText)
                    : null;
            }

            case InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Expression: { } receiver, Name: { } name },
            }:
            {
                switch (name.Identifier.Text)
                {
                    case "GetSection" or "GetRequiredSection" or "GetValue":
                        return SectionPrefix(receiver, model) is { } prefix
                            ? Combine(prefix, literal.Token.ValueText)
                            : null;

                    case "GetConnectionString":
                        return SectionPrefix(receiver, model) is not null
                            ? "ConnectionStrings:" + literal.Token.ValueText
                            : null;

                    // AddOptions<T>().BindConfiguration("Section") — the literal is the path.
                    case "BindConfiguration":
                        return OptionsBuilderType(receiver, model) is not null
                            ? literal.Token.ValueText
                            : null;

                    default:
                        return null;
                }
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// The path a literal addresses, the wrapper methods of the solution included:
    /// <c>Config.GetSetting("Test")</c> answers <c>Test</c> when <c>GetSetting</c> hands its
    /// parameter to a configuration read.
    /// </summary>
    /// <remarks>
    /// The direct question is asked first and answers without touching another document, which is
    /// the overwhelmingly common case. Only a literal that is nobody's configuration read goes on
    /// to bind the method it is passed to.
    /// </remarks>
    public static async Task<string?> PathOfReadAsync(
        LiteralExpressionSyntax literal, SemanticModel model, Solution solution, CancellationToken ct)
    {
        if (PathOfRead(literal, model) is { Length: > 0 } direct)
            return direct;

        if (!literal.IsKind(SyntaxKind.StringLiteralExpression))
            return null;

        return await ForwardedPrefixAsync(literal, model, solution, depth: 0, ct) is { } prefix
            ? Combine(prefix, literal.Token.ValueText)
            : null;
    }

    /// <summary>
    /// The path prefix the read at the far end of a wrapper is rooted at — empty for a wrapper
    /// over the configuration root, <c>Widget</c> for one over <c>GetSection("Widget")</c>, null
    /// when the expression is not passed to a wrapper at all.
    /// </summary>
    private static async Task<string?> ForwardedPrefixAsync(
        ExpressionSyntax expression, SemanticModel model, Solution solution, int depth,
        CancellationToken ct)
    {
        if (depth >= ConfigForwarding.MaxDepth
            || ConfigForwarding.Callee(expression, model) is not { } callee
            || await ConfigForwarding.DeclarationAsync(callee.Method, solution, ct) is not { } declared)
        {
            return null;
        }

        var (declaration, inner) = declared;

        // Remembered against the tree the method is declared in, which the workspace replaces on
        // every edit to that file — so an answer is never older than the body it was read from.
        // Worth remembering at all because the diagnostics pass asks this of every literal in a
        // document, and a file that calls one wrapper a hundred times would otherwise walk the
        // same method body a hundred times.
        var memo = s_wrapperPrefixes.GetOrCreateValue(declaration.SyntaxTree);
        string id = ConfigForwarding.Key(callee.Method) + "#" + callee.Index;

        if (memo.TryGetValue(id, out var remembered))
            return remembered.Prefix;

        string? found = null;

        foreach (var read in ConfigForwarding.ParameterReads(declaration, inner, callee.Index))
        {
            ct.ThrowIfCancellationRequested();

            if (PrefixOfRead(read, inner) is { } prefix)
            {
                found = prefix;
                break;
            }

            // A wrapper over a wrapper: the parameter is handed on rather than read here.
            if (await ForwardedPrefixAsync(read, inner, solution, depth + 1, ct) is { } deeper)
            {
                found = deeper;
                break;
            }
        }

        memo[id] = new Remembered(found);
        return found;
    }

    /// <summary>A prefix or the absence of one, so a null answer is remembered as an answer.</summary>
    private readonly record struct Remembered(string? Prefix);

    private static readonly ConditionalWeakTable<SyntaxTree, ConcurrentDictionary<string, Remembered>>
        s_wrapperPrefixes = new();

    /// <summary>
    /// The prefix a read whose key is <paramref name="key"/> is rooted at, for the shapes
    /// <see cref="PathOfRead"/> recognises — asked of an expression that is not a literal, which
    /// is what the inside of a wrapper looks like.
    /// </summary>
    private static string? PrefixOfRead(ExpressionSyntax key, SemanticModel model)
    {
        if (key.Parent is not ArgumentSyntax argument || argument.Parent is not { } list)
            return null;

        // Only the first argument names anything; the rest are default values and comparers.
        if (list is BaseArgumentListSyntax arguments && arguments.Arguments.IndexOf(argument) != 0)
            return null;

        switch (list.Parent)
        {
            case ElementAccessExpressionSyntax access:
                return SectionPrefix(access.Expression, model);

            case InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Expression: { } receiver, Name: { } name },
            }:
                return name.Identifier.Text switch
                {
                    "GetSection" or "GetRequiredSection" or "GetValue" =>
                        SectionPrefix(receiver, model),
                    "GetConnectionString" =>
                        SectionPrefix(receiver, model) is { } prefix
                            ? Combine(prefix, "ConnectionStrings")
                            : null,
                    _ => null,
                };

            default:
                return null;
        }
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
        ImmutableArray<ConfigurationBinding>.Builder bindings,
        ImmutableArray<ConfigurationForwarder>.Builder forwarders)
    {
        var receiver = ((MemberAccessExpressionSyntax)invocation.Expression).Expression;

        switch (name)
        {
            case "GetSection" or "GetRequiredSection" or "GetValue" or "GetConnectionString":
            {
                if (SectionPrefix(receiver, model) is not { } sectionOf)
                    return;

                // GetValue<T>(key) with the enclosing method's parameter for a key: no name is
                // read here, but every caller of that method reads one.
                if (invocation.ArgumentList.Arguments is [{ Expression: IdentifierNameSyntax passed }, ..])
                {
                    AddForwarder(
                        forwarders, passed, model,
                        name is "GetConnectionString"
                            ? Combine(sectionOf, "ConnectionStrings")
                            : sectionOf);
                }

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

    internal static void Clear()
    {
        s_cache.Clear();
        s_forwardedCache.Clear();
    }
}
