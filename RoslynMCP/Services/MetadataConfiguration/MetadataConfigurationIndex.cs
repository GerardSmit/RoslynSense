using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace RoslynMCP.Services.MetadataConfiguration;

/// <summary>Which keyspace a read belongs to.</summary>
internal enum MetadataConfigurationKind
{
    /// <summary>An <c>IConfiguration</c> path — <c>Logging:LogLevel:Default</c>.</summary>
    Path,

    /// <summary>A Framework <c>&lt;appSettings&gt;</c> key.</summary>
    AppSetting,

    /// <summary>A Framework <c>&lt;connectionStrings&gt;</c> name.</summary>
    ConnectionString,
}

/// <summary>One configuration read, confirmed, in an assembly with no source in the solution.</summary>
/// <param name="Name">The configuration path or key, as the reading side names it.</param>
/// <param name="Literal">The string as it appears in the code, which is what the decompiled
/// source will show — the same as <paramref name="Name"/> except where the API implies a section,
/// as <c>GetConnectionString</c> does.</param>
/// <param name="TypeName">The type the read was compiled into — what a click decompiles.</param>
/// <param name="MethodName">The method it sits in, so the click lands on the call.</param>
internal readonly record struct MetadataConfigurationRead(
    MetadataConfigurationKind Kind,
    string Name,
    string Literal,
    string AssemblyName,
    string AssemblyPath,
    string TypeName,
    string MethodName);

/// <summary>
/// The configuration a project's referenced assemblies read, confirmed against the type system.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MetadataConfigurationScanner"/> answers "which call sites pass a literal to
/// something plausibly named"; this answers "and is that thing really a configuration API". The
/// division matters both ways. Names alone are wrong — <c>RegistryKey.GetValue</c> and
/// <c>ConfigurationManager.GetSection</c> both matched a name-only filter, and neither reads
/// <c>IConfiguration</c>. Symbols alone are unaffordable — resolving every call site in every
/// referenced assembly is the work the name filter exists to avoid. So the scanner cuts millions
/// of sites to dozens on names, and every survivor is resolved properly here.
/// </para>
/// <para>
/// Assemblies that a solution project builds are skipped: their source is in the workspace, the
/// usage indexes already read it, and counting the compiled copy too would report every read
/// twice.
/// </para>
/// </remarks>
internal sealed class MetadataConfigurationIndex
{
    public static readonly MetadataConfigurationIndex Empty = new([]);

    private readonly ImmutableArray<MetadataConfigurationRead> _reads;

    private MetadataConfigurationIndex(ImmutableArray<MetadataConfigurationRead> reads) =>
        _reads = reads;

    public bool IsEmpty => _reads.IsEmpty;

    public ImmutableArray<MetadataConfigurationRead> Reads => _reads;

    /// <summary>Every external read of one name.</summary>
    public IEnumerable<MetadataConfigurationRead> ReadsFor(MetadataConfigurationKind kind, string name) =>
        _reads.Where(read =>
            read.Kind == kind && string.Equals(read.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every external read in one keyspace.</summary>
    public IEnumerable<MetadataConfigurationRead> ReadsOf(MetadataConfigurationKind kind) =>
        _reads.Where(read => read.Kind == kind);

    /// <summary>Every name read from outside, for offering keys a file does not declare yet.</summary>
    public IEnumerable<string> Names(MetadataConfigurationKind kind) =>
        _reads.Where(read => read.Kind == kind)
            .Select(read => read.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    // ---- Building ------------------------------------------------------------------------------

    private static readonly ConcurrentDictionary<ProjectId, Cached> s_cache = new();

    private sealed record Cached(string Key, MetadataConfigurationIndex Index, long CheckedAt);

    /// <summary>
    /// How long a validated cache entry is trusted without re-reading timestamps. CodeLens
    /// re-fires on every edit, and stat-ing several hundred reference paths per keystroke costs
    /// more than the staleness it prevents — a rebuilt dependency shows up a moment later.
    /// </summary>
    private const long RevalidateAfterMs = 2000;

    public static void Clear() => s_cache.Clear();

    /// <summary>
    /// The index for a project, rebuilt when its reference set changes. Keyed on the reference
    /// paths and their timestamps rather than on the semantic version: nothing here reads the
    /// project's own source, so an edit to it changes no answer.
    /// </summary>
    public static async Task<MetadataConfigurationIndex> GetAsync(
        Project project, CancellationToken ct)
    {
        long now = Environment.TickCount64;

        if (s_cache.TryGetValue(project.Id, out var fresh) && now - fresh.CheckedAt < RevalidateAfterMs)
            return fresh.Index;

        var references = References(project).ToList();

        if (references.Count == 0)
            return Empty;

        string key = string.Join("|", references.Select(r => r.Path + ":" + r.Stamp.Ticks));

        if (s_cache.TryGetValue(project.Id, out var cached) && cached.Key == key)
        {
            s_cache[project.Id] = cached with { CheckedAt = now };
            return cached.Index;
        }

        if (await project.GetCompilationAsync(ct) is not { } compilation)
            return Empty;

        var index = Build(compilation, references, ct);
        s_cache[project.Id] = new Cached(key, index, now);
        return index;
    }

    /// <summary>
    /// The referenced assemblies worth scanning: on disk, and not the build output of a project
    /// the solution already has the source for.
    /// </summary>
    private static IEnumerable<(string Path, DateTime Stamp)> References(Project project)
    {
        var inSolution = new HashSet<string>(
            project.Solution.Projects
                .Select(p => p.AssemblyName)
                .Where(name => name is { Length: > 0 }),
            StringComparer.OrdinalIgnoreCase);

        foreach (var reference in project.MetadataReferences)
        {
            if (reference is not PortableExecutableReference { FilePath: { Length: > 0 } path })
                continue;

            if (inSolution.Contains(Path.GetFileNameWithoutExtension(path)))
                continue;

            DateTime stamp;

            try
            {
                stamp = File.GetLastWriteTimeUtc(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            yield return (path, stamp);
        }
    }

    private static MetadataConfigurationIndex Build(
        Compilation compilation,
        IReadOnlyList<(string Path, DateTime Stamp)> references,
        CancellationToken ct)
    {
        var configuration = compilation.GetTypeByMetadataName(
            "Microsoft.Extensions.Configuration.IConfiguration");

        var reads = ImmutableArray.CreateBuilder<MetadataConfigurationRead>();
        var resolved = new Dictionary<(string Type, string Member, string? Receiver), MetadataConfigurationKind?>();

        foreach (var (path, _) in references)
        {
            ct.ThrowIfCancellationRequested();

            var candidates = MetadataConfigurationScanner.Candidates(path);

            if (candidates.IsEmpty)
                continue;

            string assembly = Path.GetFileNameWithoutExtension(path);

            foreach (var candidate in candidates)
            {
                var lookup = (candidate.DeclaringTypeName, candidate.MemberName, candidate.ReceiverMemberName);

                if (!resolved.TryGetValue(lookup, out var kind))
                {
                    kind = Classify(compilation, configuration, candidate);
                    resolved[lookup] = kind;
                }

                if (kind is not { } confirmed)
                    continue;

                if (Name(confirmed, candidate.MemberName, candidate.Literal) is { Length: > 0 } name)
                {
                    reads.Add(new MetadataConfigurationRead(
                        confirmed, name, candidate.Literal, assembly, path,
                        candidate.ContainingTypeName, candidate.ContainingMethodName));
                }
            }
        }

        return reads.Count == 0 ? Empty : new MetadataConfigurationIndex(reads.ToImmutable());
    }

    /// <summary>
    /// What a candidate really is, decided by the type system. Null for the ones a name matched
    /// and a type did not.
    /// </summary>
    private static MetadataConfigurationKind? Classify(
        Compilation compilation, INamedTypeSymbol? configuration, MetadataConfigurationCandidate candidate)
    {
        // The Framework shape: a name looked up in the collection a static property returned.
        // Which section it is comes from that property, and it counts only when the property
        // belongs to one of the configuration managers rather than to anything else carrying an
        // AppSettings. The collection's own type is not consulted — NameValueCollection reaches
        // an application through whichever facade its target framework ships, and requiring that
        // to resolve would answer differently for the same code on two frameworks.
        if (candidate.ReceiverMemberName is "get_AppSettings" or "get_ConnectionStrings"
            && candidate.ReceiverTypeName is { Length: > 0 } receiverName)
        {
            if (compilation.GetTypeByMetadataName(receiverName) is not { } receiver
                || !IsConfigurationManager(receiver))
            {
                return null;
            }

            return candidate.ReceiverMemberName == "get_AppSettings"
                ? MetadataConfigurationKind.AppSetting
                : MetadataConfigurationKind.ConnectionString;
        }

        if (configuration is null
            || compilation.GetTypeByMetadataName(candidate.DeclaringTypeName) is not { } declaring)
        {
            return null;
        }

        // The modern shape: either the call is on IConfiguration itself, or it is an extension
        // whose first parameter is. BindConfiguration is the exception that proves the rule — it
        // hangs off OptionsBuilder<T> — and its whole purpose is to name a section path.
        bool reads = Implements(declaring, configuration)
            || declaring.GetMembers(candidate.MemberName)
                .OfType<IMethodSymbol>()
                .Any(method => method.IsStatic
                    && method.Parameters.Length > 0
                    && Implements(method.Parameters[0].Type, configuration))
            || (candidate.MemberName == "BindConfiguration"
                && declaring.ContainingNamespace?.ToDisplayString()
                    .StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) == true);

        return reads ? MetadataConfigurationKind.Path : null;
    }

    private static bool Implements(ITypeSymbol type, INamedTypeSymbol configuration) =>
        SymbolEqualityComparer.Default.Equals(type, configuration)
        || type.AllInterfaces.Contains(configuration, SymbolEqualityComparer.Default);

    /// <summary>
    /// The Framework's three spellings of the same thing. Matched by name because they share no
    /// base type and implement no common interface — <c>ConfigurationManager.AppSettings</c> is a
    /// static property on a static class, and there is nothing else to ask about it.
    /// </summary>
    private static bool IsConfigurationManager(INamedTypeSymbol type) =>
        type.Name is "ConfigurationManager" or "WebConfigurationManager" or "ConfigurationSettings"
        && type.ContainingNamespace?.ToDisplayString()
            is "System.Configuration" or "System.Web.Configuration";

    /// <summary>
    /// The configuration name a literal stands for. A connection string read by name lives under
    /// the <c>ConnectionStrings</c> section on the modern side, and in its own section on the
    /// Framework one.
    /// </summary>
    private static string Name(MetadataConfigurationKind kind, string member, string literal) =>
        kind == MetadataConfigurationKind.Path && member == "GetConnectionString"
            ? "ConnectionStrings:" + literal
            : literal;
}
