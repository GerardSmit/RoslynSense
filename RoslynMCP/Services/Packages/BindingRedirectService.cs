using System.Xml.Linq;
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
public sealed record BindingRedirectFinding(
    BindingRedirectProblem Problem,
    string AssemblyName,
    string? PublicKeyToken,
    string Culture,
    string? ConfiguredVersion,
    string RequiredVersion,
    string Message,
    int Line);

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
    private static readonly XNamespace s_asm = "urn:schemas-microsoft-com:asm.v1";

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
    public static async Task<BindingRedirectReport> AnalyzeAsync(
        string projectPath, CancellationToken ct)
    {
        var evaluation = await ProjectEvaluationService.EvaluateAsync(projectPath, ct);
        if (evaluation is null || !IsFullFramework(evaluation))
            return new BindingRedirectReport(projectPath, null, []);

        string? configPath = ConfigPathFor(projectPath);
        if (configPath is null)
            return new BindingRedirectReport(projectPath, null, []);

        var shipped = Shipped(projectPath, evaluation);
        if (shipped.Count == 0)
            return new BindingRedirectReport(projectPath, configPath, []);

        var configured = Read(configPath);

        return new BindingRedirectReport(projectPath, configPath, Compare(shipped, configured));
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
        // An orphan is not repaired: nothing is broken by a redirect for an assembly that is no
        // longer shipped, and removing one is a judgement about intent — the reference may be
        // coming back, or be loaded reflectively from a path this never sees.
        var applicable = findings
            .Where(f => f.Problem is BindingRedirectProblem.Stale
                or BindingRedirectProblem.Missing
                or BindingRedirectProblem.Narrow)
            .Where(f => f.PublicKeyToken is { Length: > 0 })
            .ToList();

        if (applicable.Count == 0)
            return (null, []);

        XDocument document;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"Could not rewrite binding redirects: {ex.Message}", key: "binding-write");
            return (null, []);
        }

        var binding = EnsureAssemblyBinding(document);

        foreach (var finding in applicable)
        {
            var element = binding
                .Elements(s_asm + "dependentAssembly")
                .FirstOrDefault(e => Matches(e, finding));

            if (element is null)
            {
                element = new XElement(
                    s_asm + "dependentAssembly",
                    new XElement(
                        s_asm + "assemblyIdentity",
                        new XAttribute("name", finding.AssemblyName),
                        new XAttribute("publicKeyToken", finding.PublicKeyToken!),
                        new XAttribute("culture", finding.Culture)),
                    new XElement(s_asm + "bindingRedirect"));

                binding.Add(element);
            }

            var redirect = element.Element(s_asm + "bindingRedirect");
            if (redirect is null)
            {
                redirect = new XElement(s_asm + "bindingRedirect");
                element.Add(redirect);
            }

            // From zero rather than from the version that happened to be found: a redirect exists
            // to catch every older binding, and narrowing it to the one this analysis saw is how a
            // redirect that worked stops working after an unrelated package moves.
            redirect.SetAttributeValue("oldVersion", $"0.0.0.0-{finding.RequiredVersion}");
            redirect.SetAttributeValue("newVersion", finding.RequiredVersion);
        }

        using var writer = new StringWriter();
        document.Save(writer, SaveOptions.DisableFormatting);
        return (writer.ToString(), applicable);
    }

    private static bool Matches(XElement dependentAssembly, BindingRedirectFinding finding)
    {
        var identity = dependentAssembly.Element(s_asm + "assemblyIdentity");
        if (identity is null)
            return false;

        return string.Equals(
                identity.Attribute("name")?.Value, finding.AssemblyName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                identity.Attribute("publicKeyToken")?.Value ?? "",
                finding.PublicKeyToken ?? "",
                StringComparison.OrdinalIgnoreCase);
    }

    private static XElement EnsureAssemblyBinding(XDocument document)
    {
        var configuration = document.Root
            ?? throw new InvalidOperationException("The configuration file has no root element.");

        var runtime = configuration.Element("runtime");
        if (runtime is null)
        {
            runtime = new XElement("runtime");
            configuration.Add(runtime);
        }

        var binding = runtime.Element(s_asm + "assemblyBinding");
        if (binding is null)
        {
            binding = new XElement(s_asm + "assemblyBinding");
            runtime.Add(binding);
        }

        return binding;
    }

    /// <summary>Every <c>dependentAssembly</c> the file declares, with the line it sits on.</summary>
    internal static IReadOnlyList<ConfiguredRedirect> Read(string configPath)
    {
        try
        {
            var document = XDocument.Load(configPath, LoadOptions.SetLineInfo);

            return document.Root?
                .Elements("runtime")
                .Elements(s_asm + "assemblyBinding")
                .Elements(s_asm + "dependentAssembly")
                .Select(Parse)
                .OfType<ConfiguredRedirect>()
                .ToList() ?? [];
        }
        catch (Exception ex)
        {
            ServiceLog.Warn(
                $"Could not read binding redirects from '{Path.GetFileName(configPath)}': {ex.Message}",
                key: $"binding-read:{configPath}");
            return [];
        }
    }

    private static ConfiguredRedirect? Parse(XElement dependentAssembly)
    {
        var identity = dependentAssembly.Element(s_asm + "assemblyIdentity");
        if (identity?.Attribute("name")?.Value is not { Length: > 0 } name)
            return null;

        var redirect = dependentAssembly.Element(s_asm + "bindingRedirect");
        var range = ParseRange(redirect?.Attribute("oldVersion")?.Value);

        return new ConfiguredRedirect(
            name,
            Nullify(identity.Attribute("publicKeyToken")?.Value),
            identity.Attribute("culture")?.Value is { Length: > 0 } culture ? culture : "neutral",
            range.Low,
            range.High,
            ParseVersion(redirect?.Attribute("newVersion")?.Value),
            (dependentAssembly as System.Xml.IXmlLineInfo).LineNumber - 1);
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

            if (AssemblyIdentityReader.Read(path) is { } info)
                files.Add(info);
        }

        return files;
    }

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
    private static IReadOnlyList<BindingRedirectFinding> Compare(
        IReadOnlyList<AssemblyFileInfo> shipped,
        IReadOnlyList<ConfiguredRedirect> configured)
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
                        -1));
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
                    redirect.Line));
            }
            else if (conflicted && (redirect.OldLow is null || redirect.OldLow > wanted[key]))
            {
                findings.Add(new BindingRedirectFinding(
                    BindingRedirectProblem.Narrow,
                    actual.Name, actual.PublicKeyToken, actual.Culture,
                    redirect.NewVersion?.ToString(), actual.Version.ToString(),
                    $"{actual.Name} is referenced at {wanted[key]}, below the redirect's " +
                    $"oldVersion range starting at {redirect.OldLow?.ToString() ?? "nothing"}.",
                    redirect.Line));
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
                    redirect.Line));
            }
            else if (!present.ContainsKey(redirect.Key))
            {
                findings.Add(new BindingRedirectFinding(
                    BindingRedirectProblem.Orphan,
                    redirect.Name, redirect.PublicKeyToken, redirect.Culture,
                    redirect.NewVersion?.ToString(), "",
                    $"Nothing this project ships is {redirect.Name}, so its redirect has no effect.",
                    redirect.Line));
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
        int Line)
    {
        public string Key => $"{Name}|{PublicKeyToken ?? ""}|{Culture}";
    }
}
