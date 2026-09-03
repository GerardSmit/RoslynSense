using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services;
using RoslynMCP.Services.MetadataConfiguration;

namespace RoslynMCP.Languages.WebConfig.Core;

/// <summary>One place C# names a <c>.config</c> setting.</summary>
/// <param name="Span">The string literal's content, quotes excluded — what a peek window should
/// highlight.</param>
internal sealed record ConfigSettingUsage(
    string Name, WebConfigSection Section, string FilePath, TextSpan Span, LinePositionSpan LineSpan);

/// <summary>
/// One method of the solution's own that reads whatever setting it is handed —
/// <c>Config.GetSetting(name)</c> over <c>ConfigurationManager.AppSettings</c>, the wrapper a
/// codebase writes once and then calls everywhere.
/// </summary>
/// <param name="Id">Declaring type, method name and parameter, which is what a call in another
/// project can be matched against.</param>
internal sealed record ConfigSettingForwarder(
    string Id, string MethodName, int ParameterIndex, WebConfigSection Section);

/// <summary>
/// Every <c>appSettings</c> and <c>connectionStrings</c> name the C# of a project closure reads.
/// </summary>
/// <remarks>
/// <para>
/// The .NET Framework counterpart of <c>ConfigurationUsageIndex</c>, and a much smaller problem
/// than that one: <c>ConfigurationManager.AppSettings</c> is a flat <c>NameValueCollection</c>, so
/// there are no nested sections to accumulate a path through and no <c>Configure&lt;T&gt;</c>
/// binding to resolve a name into a property. A name is a name.
/// </para>
/// <para>
/// Built from the config file's own project plus the projects it references, transitively — the
/// assemblies composed into the running application, which are the ones whose reads are answered
/// from this file at runtime. Each project's scan is cached against its dependent semantic
/// version, so a cache hit never forces a compilation.
/// </para>
/// <para>
/// The scan is text-contains → syntax → semantics, in that order: a document that never mentions
/// either collection is dismissed for the cost of a string search, and a semantic model is only
/// built for documents whose syntax carries a candidate.
/// </para>
/// </remarks>
internal sealed class ConfigurationManagerUsageIndex
{
    public static readonly ConfigurationManagerUsageIndex Empty = new([], []);

    public ImmutableArray<ConfigSettingUsage> Usages { get; }

    /// <summary>The reading methods the solution wrote for itself, whose callers are usages too.</summary>
    public ImmutableArray<ConfigSettingForwarder> Forwarders { get; }

    public bool IsEmpty => Usages.IsEmpty;

    private ConfigurationManagerUsageIndex(
        ImmutableArray<ConfigSettingUsage> usages, ImmutableArray<ConfigSettingForwarder> forwarders)
    {
        Usages = usages;
        Forwarders = forwarders;
    }

    /// <summary>Sites naming exactly this setting — comparisons are case-insensitive, as the
    /// runtime's <c>NameValueCollection</c> lookup is.</summary>
    public IEnumerable<ConfigSettingUsage> UsagesFor(WebConfigSection section, string name) =>
        Usages.Where(usage =>
            usage.Section == section
            && string.Equals(usage.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The usage covering an offset in a C# file, or null — how a caret inside a literal
    /// finds out which setting it is naming.</summary>
    public ConfigSettingUsage? At(string filePath, int offset) =>
        Usages.FirstOrDefault(usage =>
            string.Equals(usage.FilePath, filePath, StringComparison.OrdinalIgnoreCase)
            && usage.Span.Contains(offset));

    // ---- Building ----------------------------------------------------------------------------

    private sealed record Cached(VersionStamp Version, ConfigurationManagerUsageIndex Index);

    private static readonly ConcurrentDictionary<ProjectId, Cached> s_cache = new();

    private static readonly ConcurrentDictionary<(ProjectId, string), Cached> s_forwardedCache = new();

    /// <summary>The merged index over a project and everything it references.</summary>
    /// <remarks>
    /// Two passes, because the second one needs the first one's answer from every project at once.
    /// The first finds the reads <c>ConfigurationManager</c> makes visible, and with them the
    /// wrapper methods that hand a parameter to one; the second finds the calls to those wrappers
    /// anywhere in the closure, and the wrappers <em>those</em> calls reveal in turn.
    /// </remarks>
    public static async Task<ConfigurationManagerUsageIndex> GetAsync(
        Project project, CancellationToken ct)
    {
        var usages = ImmutableArray.CreateBuilder<ConfigSettingUsage>();
        var forwarders = new Dictionary<string, ConfigSettingForwarder>(StringComparer.Ordinal);

        var closure = ApplicationClosure.Of(project).ToList();

        foreach (var member in closure)
        {
            var index = await ForProjectAsync(member, ct);
            usages.AddRange(index.Usages);

            foreach (var forwarder in index.Forwarders)
                forwarders.TryAdd(forwarder.Id, forwarder);
        }

        // The wrappers the referenced assemblies declare, which the source scan cannot find: it
        // recognises a wrapper by reading its body, and a wrapper compiled into a package has no
        // body in the workspace. Seeding them here is what lets Config.GetSetting("Key") in the
        // solution's own code count as a read of Key — the framework's wrapper is the only shape a
        // site built on it ever writes.
        foreach (var wrapper in (await MetadataConfigurationIndex.GetAsync(project, ct)).Wrappers)
        {
            if (Section(wrapper.Kind) is not { } section)
                continue;

            string id = Id(wrapper.TypeName + "." + wrapper.MethodName, wrapper.ParameterIndex);
            forwarders.TryAdd(
                id, new ConfigSettingForwarder(id, wrapper.MethodName, wrapper.ParameterIndex, section));
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

        return usages.Count == 0 ? Empty : new ConfigurationManagerUsageIndex(usages.ToImmutable(), []);
    }

    /// <summary>One project's calls to a known set of wrapper methods.</summary>
    /// <remarks>
    /// Cached against the project's semantic version <em>and</em> the set asked about, since the
    /// same project answers differently once another project's wrapper is known.
    /// </remarks>
    private static async Task<ConfigurationManagerUsageIndex> ForwardedAsync(
        Project project, ImmutableArray<ConfigSettingForwarder> forwarders, CancellationToken ct)
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

    private static async Task<ConfigurationManagerUsageIndex> ForProjectAsync(
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
    private static readonly string[] s_needles = ["AppSettings", "ConnectionStrings"];

    private static async Task<ConfigurationManagerUsageIndex> BuildAsync(
        Project project, CancellationToken ct)
    {
        var usages = ImmutableArray.CreateBuilder<ConfigSettingUsage>();
        var forwarders = ImmutableArray.CreateBuilder<ConfigSettingForwarder>();

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

            // Syntax first, semantics once: collect the shapes worth asking about, and only build
            // the model when the document has at least one.
            var candidates = new List<(MemberAccessExpressionSyntax Collection, LiteralExpressionSyntax Literal)>();

            // The same two shapes with a parameter for a key: no name is read there, but every
            // caller of the method holding it reads one.
            var passthroughs = new List<(MemberAccessExpressionSyntax Collection, ExpressionSyntax Key)>();

            foreach (var node in root.DescendantNodes())
            {
                switch (node)
                {
                    // ConfigurationManager.AppSettings["Key"]
                    case ElementAccessExpressionSyntax
                    {
                        Expression: MemberAccessExpressionSyntax collection,
                        ArgumentList.Arguments: [{ Expression: { } argument }],
                    }:
                        if (argument is LiteralExpressionSyntax literal
                            && literal.IsKind(SyntaxKind.StringLiteralExpression))
                        {
                            candidates.Add((collection, literal));
                        }
                        else if (argument is IdentifierNameSyntax)
                        {
                            passthroughs.Add((collection, argument));
                        }

                        break;

                    // ConfigurationManager.AppSettings.Get("Key"), .GetValues("Key")
                    case InvocationExpressionSyntax
                    {
                        Expression: MemberAccessExpressionSyntax
                        {
                            Name.Identifier.Text: "Get" or "GetValues",
                            Expression: MemberAccessExpressionSyntax collection,
                        },
                        ArgumentList.Arguments: [{ Expression: { } argument }],
                    }:
                        if (argument is LiteralExpressionSyntax call
                            && call.IsKind(SyntaxKind.StringLiteralExpression))
                        {
                            candidates.Add((collection, call));
                        }
                        else if (argument is IdentifierNameSyntax)
                        {
                            passthroughs.Add((collection, argument));
                        }

                        break;
                }
            }

            if (candidates.Count == 0 && passthroughs.Count == 0)
                continue;

            if (await document.GetSemanticModelAsync(ct) is not { } model)
                continue;

            foreach (var (collection, literal) in candidates)
            {
                ct.ThrowIfCancellationRequested();

                if (SectionOf(collection, model) is not { } section)
                    continue;

                string name = literal.Token.ValueText;
                if (name.Length == 0)
                    continue;

                var span = ContentSpan(literal);
                usages.Add(new ConfigSettingUsage(
                    name, section, filePath, span, literal.SyntaxTree.GetLineSpan(span).Span));
            }

            foreach (var (collection, key) in passthroughs)
            {
                if (SectionOf(collection, model) is { } section)
                    AddForwarder(forwarders, key, model, section);
            }
        }

        return usages.Count == 0 && forwarders.Count == 0
            ? Empty
            : new ConfigurationManagerUsageIndex(usages.ToImmutable(), forwarders.ToImmutable());
    }

    /// <summary>
    /// Every call in this project to one of the wrapper methods, counted as a read of the setting
    /// it names — and every method that hands its own parameter to one, which is a wrapper too.
    /// </summary>
    /// <remarks>
    /// The wrapper's name is the needle here, where the first pass used the framework's. It is the
    /// only cheap gate available: nothing at a call site says a configuration read is happening,
    /// which is exactly why the wrapper had to be found first.
    /// </remarks>
    private static async Task<ConfigurationManagerUsageIndex> BuildForwardedAsync(
        Project project, ImmutableArray<ConfigSettingForwarder> forwarders, CancellationToken ct)
    {
        var byId = forwarders.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var names = new HashSet<string>(forwarders.Select(f => f.MethodName), StringComparer.Ordinal);

        var usages = ImmutableArray.CreateBuilder<ConfigSettingUsage>();
        var found = ImmutableArray.CreateBuilder<ConfigSettingForwarder>();

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
                        && literal.IsKind(SyntaxKind.StringLiteralExpression)
                        && literal.Token.ValueText is { Length: > 0 } name)
                    {
                        var span = ContentSpan(literal);
                        usages.Add(new ConfigSettingUsage(
                            name, forwarder.Section, filePath, span,
                            literal.SyntaxTree.GetLineSpan(span).Span));
                    }
                    else
                    {
                        // A method handing its own parameter to a wrapper is one itself, and the
                        // next round goes looking for its callers.
                        AddForwarder(found, argument.Expression, model, forwarder.Section);
                    }
                }
            }
        }

        return usages.Count == 0 && found.Count == 0
            ? Empty
            : new ConfigurationManagerUsageIndex(usages.ToImmutable(), found.ToImmutable());
    }

    /// <summary>Records the method a key expression belongs to as a wrapper, when the expression
    /// is one of that method's own parameters.</summary>
    private static void AddForwarder(
        ImmutableArray<ConfigSettingForwarder>.Builder forwarders, ExpressionSyntax key,
        SemanticModel model, WebConfigSection section)
    {
        if (ConfigForwarding.ForwardedParameter(key, model) is not { } forwarded)
            return;

        var (method, index) = forwarded;
        string id = Id(ConfigForwarding.Key(method), index);

        if (!forwarders.Any(f => f.Id == id))
            forwarders.Add(new ConfigSettingForwarder(id, method.Name, index, section));
    }

    private static string Id(string key, int parameterIndex) => key + "#" + parameterIndex;

    /// <summary>
    /// The <c>.config</c> section a metadata wrapper reads, or null for the ones that belong to
    /// the other keyspace — an <c>IConfiguration</c> path is the appsettings pack's business.
    /// </summary>
    private static WebConfigSection? Section(MetadataConfigurationKind kind) =>
        kind switch
        {
            MetadataConfigurationKind.AppSetting => WebConfigSection.AppSettings,
            MetadataConfigurationKind.ConnectionString => WebConfigSection.ConnectionStrings,
            _ => null,
        };

    /// <summary>
    /// The section a receiver reads, or null when it is not one of the configuration collections.
    /// </summary>
    /// <remarks>
    /// Bound rather than matched by name: <c>AppSettings</c> is also a property on
    /// <c>HttpContext</c>-shaped helpers people write themselves, and a name-only match would
    /// count their keys as this file's. The three declaring types are the ones the framework
    /// exposes — <c>ConfigurationManager</c>, ASP.NET's <c>WebConfigurationManager</c>, and the
    /// long-obsolete <c>ConfigurationSettings</c> that legacy code still carries.
    /// </remarks>
    private static WebConfigSection? SectionOf(MemberAccessExpressionSyntax collection, SemanticModel model)
    {
        if (model.GetSymbolInfo(collection).Symbol is not IPropertySymbol
            {
                IsStatic: true, ContainingType: { } declaring,
            } property)
        {
            return null;
        }

        if (declaring.Name is not ("ConfigurationManager" or "WebConfigurationManager"
            or "ConfigurationSettings"))
        {
            return null;
        }

        string containing = declaring.ContainingNamespace.ToDisplayString();

        if (containing is not ("System.Configuration" or "System.Web.Configuration"))
            return null;

        return property.Name switch
        {
            "AppSettings" => WebConfigSection.AppSettings,
            "ConnectionStrings" => WebConfigSection.ConnectionStrings,
            _ => null,
        };
    }

    /// <summary>
    /// The section a literal is read from, or null when it is not a configuration read at all —
    /// the question an editor gesture on one literal asks, where the index above asks it of a
    /// whole project.
    /// </summary>
    /// <remarks>
    /// The two shapes the index recognizes, walked from the literal outwards:
    /// <c>…AppSettings["Key"]</c> and <c>…AppSettings.Get("Key")</c>. Cheap to decline — the
    /// syntax rules out everything but an argument in those two positions before anything binds.
    /// </remarks>
    public static WebConfigSection? SectionOfRead(
        LiteralExpressionSyntax literal, SemanticModel model)
    {
        if (literal.Parent is not ArgumentSyntax { Parent: BracketedArgumentListSyntax or ArgumentListSyntax } argument)
            return null;

        var collection = argument.Parent!.Parent switch
        {
            ElementAccessExpressionSyntax { Expression: MemberAccessExpressionSyntax access } => access,
            InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax
                {
                    Name.Identifier.Text: "Get" or "GetValues",
                    Expression: MemberAccessExpressionSyntax access,
                },
            } => access,
            _ => null,
        };

        return collection is null ? null : SectionOf(collection, model);
    }

    /// <summary>
    /// The section a literal is read from, the wrapper methods of the solution included:
    /// <c>Config.GetSetting("Test")</c> answers <c>appSettings</c> when <c>GetSetting</c> hands its
    /// parameter to <c>ConfigurationManager.AppSettings</c>.
    /// </summary>
    /// <remarks>
    /// The direct question is asked first and answers without touching another document, which is
    /// the overwhelmingly common case. Only a literal that is nobody's configuration read goes on
    /// to bind the method it is passed to.
    /// </remarks>
    public static async Task<WebConfigSection?> SectionOfReadAsync(
        LiteralExpressionSyntax literal, SemanticModel model, Solution solution, CancellationToken ct)
    {
        if (SectionOfRead(literal, model) is { } direct)
            return direct;

        return literal.IsKind(SyntaxKind.StringLiteralExpression)
            ? await ForwardedSectionAsync(literal, model, solution, depth: 0, ct)
            : null;
    }

    /// <summary>The section the read at the far end of a wrapper reaches, or null when the
    /// expression is not passed to a wrapper at all.</summary>
    private static async Task<WebConfigSection?> ForwardedSectionAsync(
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
        var memo = s_wrapperSections.GetOrCreateValue(declaration.SyntaxTree);
        string id = ConfigForwarding.Key(callee.Method) + "#" + callee.Index;

        if (memo.TryGetValue(id, out var remembered))
            return remembered.Section;

        WebConfigSection? found = null;

        foreach (var read in ConfigForwarding.ParameterReads(declaration, inner, callee.Index))
        {
            ct.ThrowIfCancellationRequested();

            if (SectionOfKey(read, inner) is { } section)
            {
                found = section;
                break;
            }

            // A wrapper over a wrapper: the parameter is handed on rather than read here.
            if (await ForwardedSectionAsync(read, inner, solution, depth + 1, ct) is { } deeper)
            {
                found = deeper;
                break;
            }
        }

        memo[id] = new Remembered(found);
        return found;
    }

    /// <summary>A section or the absence of one, so a null answer is remembered as an answer.</summary>
    private readonly record struct Remembered(WebConfigSection? Section);

    private static readonly ConditionalWeakTable<SyntaxTree, ConcurrentDictionary<string, Remembered>>
        s_wrapperSections = new();

    /// <summary>
    /// The section a read whose key is <paramref name="key"/> addresses, for the shapes
    /// <see cref="SectionOfRead"/> recognises — asked of an expression that is not a literal,
    /// which is what the inside of a wrapper looks like.
    /// </summary>
    private static WebConfigSection? SectionOfKey(ExpressionSyntax key, SemanticModel model)
    {
        if (key.Parent is not ArgumentSyntax argument || argument.Parent is not { } list)
            return null;

        var collection = list.Parent switch
        {
            ElementAccessExpressionSyntax { Expression: MemberAccessExpressionSyntax access } => access,
            InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax
                {
                    Name.Identifier.Text: "Get" or "GetValues",
                    Expression: MemberAccessExpressionSyntax access,
                },
            } => access,
            _ => null,
        };

        return collection is null ? null : SectionOf(collection, model);
    }

    /// <summary>The literal's content without its quotes — what a peek should highlight.</summary>
    public static TextSpan ContentSpan(LiteralExpressionSyntax literal) => ContentSpan(literal.Token);

    /// <inheritdoc cref="ContentSpan(LiteralExpressionSyntax)"/>
    public static TextSpan ContentSpan(SyntaxToken token)
    {
        string text = token.Text;

        // Verbatim and raw strings shift the content differently; the token text says how far.
        int open = text.IndexOf('"') + 1;
        return text.Length >= open + 1 && text.EndsWith("\"", StringComparison.Ordinal)
            ? new TextSpan(token.SpanStart + open, Math.Max(0, token.Span.Length - open - 1))
            : token.Span;
    }

    internal static void Clear()
    {
        s_cache.Clear();
        s_forwardedCache.Clear();
    }
}
