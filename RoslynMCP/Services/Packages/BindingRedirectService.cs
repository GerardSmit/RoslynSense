using System.Collections.Concurrent;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Language.Xml;
using RoslynMCP.Languages;
using RoslynMCP.Services.ProjectModel;

namespace RoslynMCP.Services.Packages;

/// <summary>What is wrong with a redirect, or with its absence.</summary>
public enum BindingRedirectProblem
{
    /// <summary>The redirect names a version that is not the one shipping.</summary>
    Stale,

    /// <summary>Something binds to a version that is not shipping, and no redirect covers it.</summary>
    Missing,

    /// <summary>A redirect exists, but its <c>oldVersion</c> range does not reach what binds.</summary>
    Narrow,

    /// <summary>A redirect for an assembly nothing ships any more.</summary>
    Orphan,

    /// <summary>A redirect for an assembly with no public key token, which the runtime ignores.</summary>
    NoOp,
}

/// <param name="Line">0-based line of the <c>dependentAssembly</c>, or -1 when there is no element yet.</param>
/// <param name="Span">
/// The text this is actually about — the <c>newVersion</c> that names the wrong version, the
/// <c>oldVersion</c> whose range falls short, the <c>name</c> of an assembly nothing ships. It
/// is <see langword="null"/> only when the document could not be located that precisely, and
/// the reader then gets the line.
/// </param>
public sealed record BindingRedirectFinding(
    BindingRedirectProblem Problem,
    string AssemblyName,
    string? PublicKeyToken,
    string Culture,
    string? ConfiguredVersion,
    string RequiredVersion,
    string Message,
    int Line,
    ConfigSpan? Span = null);

public sealed record BindingRedirectReport(
    string ProjectPath,
    string? ConfigPath,
    IReadOnlyList<BindingRedirectFinding> Findings);

/// <summary>
/// Whether a .NET Framework project's <c>assemblyBinding</c> section matches the assemblies it
/// actually ships.
/// </summary>
/// <remarks>
/// <para>
/// Updating a package rewrites the reference and the <c>packages.config</c> entry; the redirect in
/// <c>web.config</c> or <c>app.config</c> keeps naming the version that was there before. Nothing
/// fails at build — the assembly is found, it is simply the wrong one — and the first symptom is a
/// <c>FileLoadException</c> from a code path nobody exercised before shipping.
/// </para>
/// <para>
/// The comparison is against metadata, not against the project's package list: what needs
/// redirecting is what the shipped assemblies bind to, which includes every version a dependency
/// was compiled against. That is the same question <c>AutoGenerateBindingRedirects</c> answers at
/// build for SDK-style executables and does not answer at all for web projects.
/// </para>
/// </remarks>
public static class BindingRedirectService
{

    /// <summary>
    /// The config file whose redirects apply to this project, or <c>null</c> when it has none.
    /// </summary>
    /// <remarks>
    /// <c>web.config</c> wins where both exist: a web project has both often enough — an
    /// <c>app.config</c> left over from a conversion — and only one of them is read at runtime.
    /// </remarks>
    public static string? ConfigPathFor(string projectPath)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(projectPath));
        if (directory is null)
            return null;

        foreach (string name in new[] { "web.config", "Web.config", "app.config", "App.config" })
        {
            string candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Redirects this project needs but does not have, or has wrong.
    /// </summary>
    /// <remarks>
    /// Only .NET Framework projects are examined. On .NET Core the whole mechanism is gone — the
    /// runtime rolls forward to the highest version it finds — so reporting on a redirect there
    /// would be reporting on a section that has no effect.
    /// </remarks>
    public static Task<BindingRedirectReport> AnalyzeAsync(string projectPath, CancellationToken ct) =>
        AnalyzeAsync(projectPath, waitForEvaluation: true, ct);

    /// <param name="waitForEvaluation">
    /// Whether it is acceptable to evaluate the project if no evaluation is cached yet.
    /// <see langword="false"/> reports nothing rather than waiting — for the background sweep,
    /// which must never queue an MSBuild evaluation behind a solution load. The sweep runs again
    /// on a timer, so the findings arrive once the evaluation the rest of the server needs anyway
    /// has landed.
    /// </param>
    /// <inheritdoc cref="AnalyzeAsync(string, CancellationToken)"/>
    internal static async Task<BindingRedirectReport> AnalyzeAsync(
        string projectPath, bool waitForEvaluation, CancellationToken ct)
    {
        var evaluation = waitForEvaluation
            ? await ProjectEvaluationService.EvaluateAsync(projectPath, ct)
            : ProjectEvaluationService.TryGetCached(projectPath);

        if (evaluation is null || !IsFullFramework(evaluation))
            return new BindingRedirectReport(projectPath, null, []);

        string? configPath = ConfigPathFor(projectPath);
        if (configPath is null)
            return new BindingRedirectReport(projectPath, null, []);

        var shipped = Shipped(projectPath, evaluation);
        if (shipped.Count == 0)
            return new BindingRedirectReport(projectPath, configPath, []);

        string text;
        try
        {
            text = File.ReadAllText(configPath);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn(
                $"Could not read binding redirects from '{Path.GetFileName(configPath)}': {ex.Message}",
                key: $"binding-read:{configPath}");
            return new BindingRedirectReport(projectPath, configPath, []);
        }

        var configured = ReadText(text, out var section);

        return new BindingRedirectReport(
            projectPath, configPath, Compare(shipped, configured, section));
    }

    /// <summary>
    /// Rewrites the config so every finding is resolved, leaving the rest of the document alone.
    /// </summary>
    /// <returns>The findings that were applied. Empty when there was nothing to do.</returns>
    public static IReadOnlyList<BindingRedirectFinding> Apply(
        string configPath, IReadOnlyList<BindingRedirectFinding> findings)
    {
        string original;
        try
        {
            original = File.ReadAllText(configPath);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn(
                $"Could not read '{Path.GetFileName(configPath)}': {ex.Message}",
                key: $"binding-read:{configPath}");
            return [];
        }

        var (text, applied) = Rewrite(original, findings);
        if (text is null)
            return [];

        File.WriteAllText(configPath, text);
        return applied;
    }

    /// <summary>
    /// The same report, reused while neither the config nor the clock has moved.
    /// </summary>
    /// <remarks>
    /// For the code lens, which the client re-requests on every scroll and every keystroke in the
    /// file — where <see cref="AnalyzeAsync"/> is a directory walk over <c>bin</c> and
    /// <c>packages</c> per request. Invalidated by the config file's own write time, so a fix or an
    /// edit is reflected at once; the timer is what eventually catches a rebuild, which changes
    /// what ships without touching the config at all.
    /// </remarks>
    internal static async Task<BindingRedirectReport> CachedAnalyzeAsync(
        string projectPath, CancellationToken ct) =>
        await CachedAnalyzeAsync(projectPath, waitForEvaluation: true, ct);

    /// <inheritdoc cref="CachedAnalyzeAsync(string, CancellationToken)"/>
    /// <param name="waitForEvaluation">
    /// <inheritdoc cref="AnalyzeAsync(string, bool, CancellationToken)" path="/param[@name='waitForEvaluation']"/>
    /// </param>
    internal static async Task<BindingRedirectReport> CachedAnalyzeAsync(
        string projectPath, bool waitForEvaluation, CancellationToken ct)
    {
        var stamp = ConfigStamp(ConfigPathFor(projectPath));

        if (s_reports.TryGetValue(projectPath, out var cached) &&
            cached.Stamp == stamp &&
            DateTime.UtcNow - cached.ReadAtUtc <= TimeSpan.FromSeconds(15))
        {
            return cached.Report;
        }

        var report = await AnalyzeAsync(projectPath, waitForEvaluation, ct);

        // An answer that only says "no evaluation yet" must not be stored: it would hold for the
        // whole 15-second window and stop the next sweep from noticing the evaluation arriving.
        if (waitForEvaluation || report.ConfigPath is not null)
            s_reports[projectPath] = (stamp, DateTime.UtcNow, report);

        return report;
    }

    private static (DateTime Write, long Length) ConfigStamp(string? configPath)
    {
        try
        {
            return configPath is { Length: > 0 } && new FileInfo(configPath) is { Exists: true } info
                ? (info.LastWriteTimeUtc, info.Length)
                : default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    /// <inheritdoc cref="CachedAnalyzeAsync"/>
    private static readonly ConcurrentDictionary<
        string, ((DateTime Write, long Length) Stamp, DateTime ReadAtUtc, BindingRedirectReport Report)>
        s_reports = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The findings a rewrite can actually resolve.
    /// </summary>
    /// <remarks>
    /// An orphan is not repaired: nothing is broken by a redirect for an assembly that is no
    /// longer shipped, and removing one is a judgement about intent — the reference may be coming
    /// back, or be loaded reflectively from a path this never sees. An unsigned assembly is not
    /// repaired either, because there is no identity to redirect.
    /// </remarks>
    internal static IEnumerable<BindingRedirectFinding> Fixable(
        IEnumerable<BindingRedirectFinding> findings) =>
        findings
            .Where(f => f.Problem is BindingRedirectProblem.Stale
                or BindingRedirectProblem.Missing
                or BindingRedirectProblem.Narrow)
            .Where(f => f.PublicKeyToken is { Length: > 0 });

    /// <summary>
    /// What the project ships, by simple assembly name — the version a redirect in its config
    /// ought to be naming.
    /// </summary>
    /// <remarks>
    /// By simple name rather than by full identity because the caller is a hover over an
    /// <c>assemblyIdentity</c>'s <c>name</c>, which is all the reader has pointed at. Where a
    /// folder holds two of the same name the highest version wins, matching what the comparison
    /// treats as shipping.
    /// </remarks>
    public static async Task<IReadOnlyDictionary<string, AssemblyFileInfo>> InstalledAsync(
        string projectPath, CancellationToken ct)
    {
        if (s_installed.TryGetValue(projectPath, out var cached) && !cached.IsStale)
            return cached.Assemblies;

        var evaluation = await ProjectEvaluationService.EvaluateAsync(projectPath, ct);

        var byName = new Dictionary<string, AssemblyFileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in evaluation is null ? [] : Shipped(projectPath, evaluation))
        {
            if (!byName.TryGetValue(file.Identity.Name, out var existing) ||
                file.Identity.Version > existing.Identity.Version)
            {
                byName[file.Identity.Name] = file;
            }
        }

        s_installed[projectPath] = new InstalledCache(byName, DateTime.UtcNow);
        return byName;
    }

    /// <summary>
    /// Hover asks this on every mouse rest, and answering means a metadata read of every assembly
    /// in <c>bin</c> and <c>packages</c>. The window is short enough that a build finishing during
    /// it is not a case worth designing for.
    /// </summary>
    /// <remarks>
    /// The diagnostics path used to be exempted from this so the squiggles would answer from what
    /// is on disk right now. That exemption applied to the <em>document pull</em>, which runs when
    /// the user has the config file in front of them; the background sweep inherited it by
    /// accident and re-walked <c>bin</c> and every <c>packages</c> lib folder every two seconds for
    /// every full-framework project in the solution. It also made the two disagree — the lens over
    /// a line read from the cache and said "3 redirects out of date" while the squiggles on the
    /// same line were computed fresh and said nothing.
    /// </remarks>
    private static readonly ConcurrentDictionary<string, InstalledCache> s_installed =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record InstalledCache(
        IReadOnlyDictionary<string, AssemblyFileInfo> Assemblies, DateTime ReadAtUtc)
    {
        public bool IsStale => DateTime.UtcNow - ReadAtUtc > TimeSpan.FromSeconds(15);
    }

    /// <summary>
    /// The config with every fixable finding resolved.
    /// </summary>
    /// <remarks>
    /// Text in, text out, so the same rewrite serves the editor — where the result has to be a
    /// workspace edit the user can undo — and the post-update fix, which writes the file.
    /// </remarks>
    /// <returns>The new text, or <c>null</c> when nothing was fixable.</returns>
    internal static (string? Text, IReadOnlyList<BindingRedirectFinding> Applied) Rewrite(
        string xml, IReadOnlyList<BindingRedirectFinding> findings)
    {
        var applicable = Fixable(findings).ToList();

        return applicable.Count == 0
            ? (null, [])
            : BindingRedirectRewriter.Rewrite(xml, applicable);
    }

    /// <summary>Every <c>dependentAssembly</c> the file declares, with the line it sits on.</summary>
    internal static IReadOnlyList<ConfiguredRedirect> Read(string configPath)
    {
        try
        {
            return ReadText(File.ReadAllText(configPath), out _);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn(
                $"Could not read binding redirects from '{Path.GetFileName(configPath)}': {ex.Message}",
                key: $"binding-read:{configPath}");
            return [];
        }
    }

    /// <summary>
    /// The same, from text the caller already has — an editor buffer, which is what a hover has
    /// to answer about.
    /// </summary>
    /// <remarks>
    /// The parse is error-tolerant, so a config in the middle of being typed into still reports
    /// the redirects around the caret rather than nothing at all from the first malformation
    /// onwards.
    /// </remarks>
    internal static IReadOnlyList<ConfiguredRedirect> ReadText(string xml) => ReadText(xml, out _);

    /// <inheritdoc cref="ReadText(string)"/>
    /// <param name="section">
    /// Where the <c>assemblyBinding</c> element is written, which is the only place a redirect
    /// that was never added is missing from. Nothing else in the file has anything to do with it,
    /// so a finding about one has nowhere else to point.
    /// </param>
    internal static IReadOnlyList<ConfiguredRedirect> ReadText(string xml, out ConfigSpan? section)
    {
        var text = SourceText.From(xml);
        var binding = ConfigXml.Section(Parser.ParseText(xml));

        if (binding is null)
        {
            section = null;
            return [];
        }

        section = ConfigSpan.From(text, binding.NameSpan.ToRoslynSpan());

        return binding.GetElementsByLocalName("dependentAssembly")
            .Select(element => Parse(element, text))
            .OfType<ConfiguredRedirect>()
            .ToList();
    }

    /// <summary>
    /// One <c>dependentAssembly</c>, with a span for each part of it a finding can be about.
    /// </summary>
    /// <remarks>
    /// Values are decoded and spans are not: the decoded string is what a version parses from,
    /// and the span is where the characters are. An entity reference makes the two different
    /// lengths, which is exactly why the span is taken from the tree rather than measured off the
    /// value.
    /// </remarks>
    private static ConfiguredRedirect? Parse(XmlElementBaseSyntax dependentAssembly, SourceText text)
    {
        if (dependentAssembly.GetElementByLocalName("assemblyIdentity") is not { } identity)
            return null;

        var name = identity.GetAttributeByLocalName("name");
        if (name?.Value is not { Length: > 0 } assembly)
            return null;

        var token = identity.GetAttributeByLocalName("publicKeyToken");

        var redirect = dependentAssembly.GetElementByLocalName("bindingRedirect");
        var oldVersion = redirect?.GetAttributeByLocalName("oldVersion");
        var newVersion = redirect?.GetAttributeByLocalName("newVersion");

        var range = ParseRange(oldVersion?.Value);

        return new ConfiguredRedirect(
            assembly,
            Nullify(token?.Value),
            identity.GetAttributeByLocalName("culture")?.Value is { Length: > 0 } culture
                ? culture
                : "neutral",
            range.Low,
            range.High,
            ParseVersion(newVersion?.Value),
            text.Lines.GetLinePosition(dependentAssembly.NameSpan.ToRoslynSpan().Start).Line,
            ConfigSpan.From(text, name.ValueSpan.ToRoslynSpan()),
            ConfigSpan.From(text, token?.ValueSpan.ToRoslynSpan() ?? default),
            ConfigSpan.From(text, oldVersion?.ValueSpan.ToRoslynSpan() ?? default),
            ConfigSpan.From(text, newVersion?.ValueSpan.ToRoslynSpan() ?? default));
    }

    /// <summary>
    /// <c>oldVersion</c> is either a single version or a <c>low-high</c> range.
    /// </summary>
    private static (Version? Low, Version? High) ParseRange(string? raw)
    {
        if (raw is not { Length: > 0 })
            return (null, null);

        int dash = raw.IndexOf('-');
        if (dash < 0)
        {
            var single = ParseVersion(raw);
            return (single, single);
        }

        return (ParseVersion(raw[..dash]), ParseVersion(raw[(dash + 1)..]));
    }

    private static Version? ParseVersion(string? raw) =>
        Version.TryParse(raw?.Trim(), out var parsed) ? parsed : null;

    private static string? Nullify(string? value) =>
        value is { Length: > 0 } && !value.Equals("null", StringComparison.OrdinalIgnoreCase) ? value : null;

    /// <summary>
    /// What the project ships, keyed by identity. Read from the output folder when there is one,
    /// and from the extracted packages otherwise.
    /// </summary>
    /// <remarks>
    /// The packages fallback matters more than it looks: the analysis is most useful right after a
    /// package update, which is exactly when the previous build's output is stale. Reading the
    /// packages folder describes what the *next* build will ship, which is the thing being asked
    /// about.
    /// </remarks>
    private static IReadOnlyList<AssemblyFileInfo> Shipped(string projectPath, ProjectEvaluation evaluation)
    {
        var files = new List<AssemblyFileInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string path in ProbePaths(projectPath, evaluation))
        {
            if (!seen.Add(Path.GetFileName(path)))
                continue;

            if (Identity(path) is { } info)
                files.Add(info);
        }

        return files;
    }

    /// <summary>
    /// One assembly's identity, read once per version of the file on disk.
    /// </summary>
    /// <remarks>
    /// A <c>packages</c> folder holds thousands of assemblies and none of them changes, but every
    /// pass over them used to be a full metadata read each — paid again on every diagnostics pull
    /// and every code lens request. Keyed on write time and length rather than on a timer, so a
    /// rebuilt output assembly is re-read the moment it changes and an untouched package is never
    /// read twice.
    /// </remarks>
    internal static AssemblyFileInfo? Identity(string path)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists)
                return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var stamp = (info.LastWriteTimeUtc, info.Length);

        if (s_identities.TryGetValue(info.FullName, out var cached) && cached.Stamp == stamp)
            return cached.Info;

        var read = AssemblyIdentityReader.Read(path);
        s_identities[info.FullName] = (stamp, read);

        return read;
    }

    /// <inheritdoc cref="Identity"/>
    private static readonly ConcurrentDictionary<
        string, ((DateTime Write, long Length) Stamp, AssemblyFileInfo? Info)> s_identities =
        new(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> ProbePaths(string projectPath, ProjectEvaluation evaluation)
    {
        string projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;

        if (evaluation.Properties.TryGetValue("OutputPath", out string? outputPath) &&
            outputPath is { Length: > 0 })
        {
            string output = Path.GetFullPath(Path.Combine(projectDirectory, outputPath));
            if (Directory.Exists(output))
            {
                foreach (string file in Directory.EnumerateFiles(output, "*.dll"))
                    yield return file;
            }
        }

        // packages.config only: a PackageReference project on .NET Framework resolves out of the
        // global packages folder, and walking that would mean walking every version of everything.
        string packagesRoot = PackagesConfigService.PackagesRootFor(projectPath);
        foreach (var entry in PackagesConfigService.Read(projectPath))
        {
            string libRoot = Path.Combine(packagesRoot, $"{entry.Id}.{entry.Version}", "lib");
            if (!Directory.Exists(libRoot))
                continue;

            foreach (string file in Directory.EnumerateFiles(libRoot, "*.dll", SearchOption.AllDirectories))
                yield return file;
        }
    }

    /// <summary>
    /// Everything the shipped assemblies bind to, against everything that is actually there.
    /// </summary>
    /// <param name="section">
    /// <inheritdoc cref="ReadText(string, out ConfigSpan)" path="/param[@name='section']"/>
    /// </param>
    private static IReadOnlyList<BindingRedirectFinding> Compare(
        IReadOnlyList<AssemblyFileInfo> shipped,
        IReadOnlyList<ConfiguredRedirect> configured,
        ConfigSpan? section = null)
    {
        var present = new Dictionary<string, AssemblyIdentityInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in shipped)
        {
            // Strong-named only: without a public key token there is no identity to redirect, and
            // the runtime binds a weak name by simple name alone.
            if (file.Identity.PublicKeyToken is { Length: > 0 } &&
                (!present.TryGetValue(file.Identity.Key, out var existing) ||
                 file.Identity.Version > existing.Version))
            {
                present[file.Identity.Key] = file.Identity;
            }
        }

        // The lowest version anything still binds to, per identity. That is what oldVersion has to
        // reach down to, and reporting the highest instead would produce a redirect that leaves the
        // oldest consumer failing.
        var wanted = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in shipped.SelectMany(file => file.References))
        {
            if (!present.TryGetValue(reference.Key, out var actual) || reference.Version == actual.Version)
                continue;

            if (!wanted.TryGetValue(reference.Key, out var lowest) || reference.Version < lowest)
                wanted[reference.Key] = reference.Version;
        }

        var findings = new List<BindingRedirectFinding>();

        foreach (var (key, actual) in present)
        {
            var redirect = configured.FirstOrDefault(
                r => r.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

            bool conflicted = wanted.ContainsKey(key);

            if (redirect is null)
            {
                if (conflicted)
                {
                    findings.Add(new BindingRedirectFinding(
                        BindingRedirectProblem.Missing,
                        actual.Name, actual.PublicKeyToken, actual.Culture,
                        null, actual.Version.ToString(),
                        $"{actual.Name} is referenced at {wanted[key]} but {actual.Version} ships; " +
                        "without a binding redirect this fails at runtime.",
                        -1,
                        section));
                }
                continue;
            }

            if (redirect.NewVersion != actual.Version)
            {
                findings.Add(new BindingRedirectFinding(
                    BindingRedirectProblem.Stale,
                    actual.Name, actual.PublicKeyToken, actual.Culture,
                    redirect.NewVersion?.ToString(), actual.Version.ToString(),
                    $"The binding redirect for {actual.Name} names " +
                    $"{redirect.NewVersion?.ToString() ?? "no version"}, but {actual.Version} ships.",
                    redirect.Line,
                    redirect.NewVersionSpan ?? redirect.NameSpan));
            }
            else if (conflicted && (redirect.OldLow is null || redirect.OldLow > wanted[key]))
            {
                findings.Add(new BindingRedirectFinding(
                    BindingRedirectProblem.Narrow,
                    actual.Name, actual.PublicKeyToken, actual.Culture,
                    redirect.NewVersion?.ToString(), actual.Version.ToString(),
                    $"{actual.Name} is referenced at {wanted[key]}, below the redirect's " +
                    $"oldVersion range starting at {redirect.OldLow?.ToString() ?? "nothing"}.",
                    redirect.Line,
                    redirect.OldVersionSpan ?? redirect.NameSpan));
            }
        }

        foreach (var redirect in configured)
        {
            if (redirect.PublicKeyToken is null)
            {
                findings.Add(new BindingRedirectFinding(
                    BindingRedirectProblem.NoOp,
                    redirect.Name, null, redirect.Culture,
                    redirect.NewVersion?.ToString(), "",
                    $"The redirect for {redirect.Name} declares no publicKeyToken, so the runtime " +
                    "ignores it — only strong-named assemblies can be redirected.",
                    redirect.Line,
                    redirect.TokenSpan ?? redirect.NameSpan));
            }
            else if (!present.ContainsKey(redirect.Key))
            {
                findings.Add(new BindingRedirectFinding(
                    BindingRedirectProblem.Orphan,
                    redirect.Name, redirect.PublicKeyToken, redirect.Culture,
                    redirect.NewVersion?.ToString(), "",
                    $"Nothing this project ships is {redirect.Name}, so its redirect has no effect.",
                    redirect.Line,
                    redirect.NameSpan));
            }
        }

        return findings
            .OrderBy(f => f.AssemblyName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Binding redirects only exist on .NET Framework. <c>TargetFrameworkVersion</c> is the legacy
    /// project's property; an SDK-style project on .NET Framework says so in its moniker.
    /// </summary>
    private static bool IsFullFramework(ProjectEvaluation evaluation)
    {
        if (evaluation.Properties.ContainsKey("TargetFrameworkVersion"))
            return true;

        return evaluation.TargetFrameworks.Any(
            moniker => moniker.StartsWith("net4", StringComparison.OrdinalIgnoreCase) ||
                moniker.StartsWith("net3", StringComparison.OrdinalIgnoreCase) ||
                moniker.StartsWith("v", StringComparison.OrdinalIgnoreCase));
    }

    internal sealed record ConfiguredRedirect(
        string Name,
        string? PublicKeyToken,
        string Culture,
        Version? OldLow,
        Version? OldHigh,
        Version? NewVersion,
        int Line,
        ConfigSpan? NameSpan = null,
        ConfigSpan? TokenSpan = null,
        ConfigSpan? OldVersionSpan = null,
        ConfigSpan? NewVersionSpan = null)
    {
        public string Key => $"{Name}|{PublicKeyToken ?? ""}|{Culture}";
    }
}
