using System.Text.Json;
using System.Xml;

namespace RoslynMCP.Services.Database;

/// <summary>
/// Scans a directory tree for connection strings declared in <c>web.config</c>
/// and <c>appsettings*.json</c> files and registers them as <see cref="IDbProvider"/>
/// instances aliased <c>ProjectName_ConnectionStringName</c>.
/// Project name is resolved by walking up from each config file to the nearest
/// <c>*.csproj</c>; if none is found, the enclosing directory name is used.
/// Environment-specific files (<c>appsettings.Development.json</c>) override the
/// base file when both define the same alias.
/// Provider resolution order per connection string:
///   1. <c>providerName</c> attribute (web.config).
///   2. Connection string content heuristics (Host=/Port=/Integrated Security=/...).
///   3. Connection string name hint (e.g. "SiteSqlServer", "PostgresMain").
///   4. Project's referenced NuGet packages (Npgsql, Microsoft.Data.Sqlite, ...).
///   5. <c>web.config</c> default: mssql (.NET Framework ships SqlClient).
///   6. Otherwise: skipped.
/// </summary>
public static class AutoConnectionStringDiscovery
{
    private static readonly HashSet<string> s_skipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".vs", ".idea", "packages", "TestResults", ".cache"
    };

    // Filename infixes that mark a config file as a non-runtime template / sample.
    // Matches both `appsettings.template.json` and `web.template.config`.
    private static readonly HashSet<string> s_templateInfixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "template", "example", "sample", "dist"
    };

    // Production-flavored environment names. Skipped entirely so the LLM can't
    // reach a production database through this tool by accident — RoslynSense is
    // a development-time tool and registering prod credentials is asking for
    // trouble. Use --db explicitly if you really need prod access.
    private static readonly HashSet<string> s_prodEnvNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Production", "Prod", "Live", "Staging", "Stage",
        // MSBuild Configuration names. `web.Release.config` is the XDT transform
        // applied by `dotnet publish -c Release`, almost always to inject prod
        // settings. Skip it for the same reason we skip Production.
        "Release", "Publish"
    };

    // Development-flavored environment names. These have the highest priority
    // when merging connection strings — they override the base file and any
    // non-dev environment-specific file.
    private static readonly HashSet<string> s_devEnvNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Development", "Dev", "Local", "Debug"
    };

    // Cap recursion to guard against symlink loops and unbounded scans.
    private const int MaxDirectoryDepth = 32;

    public sealed record DiscoveryWarning(string File, string Message);

    private sealed record ProjectContext(string Name, string? PackageProvider);

    private sealed record RawEntry(string Name, string ConnectionString, string? ProviderNameAttr);

    public static IReadOnlyList<IDbProvider> Discover(string rootDir) =>
        Discover(rootDir, out _);

    public static IReadOnlyList<IDbProvider> Discover(
        string rootDir, out IReadOnlyList<DiscoveryWarning> warnings)
    {
        var warningList = new List<DiscoveryWarning>();
        warnings = warningList;

        if (!Directory.Exists(rootDir))
            return Array.Empty<IDbProvider>();

        // Sort so that lower-priority files apply first and dev-flavored files
        // (Development / Debug / Local) apply last, overriding other entries.
        // Tiebreak alphabetically for determinism.
        var files = EnumerateConfigFiles(rootDir)
            .OrderBy(GetFilePriority)
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var projectCache = new Dictionary<string, ProjectContext>(StringComparer.OrdinalIgnoreCase);
        var results = new Dictionary<string, IDbProvider>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var ctx = ResolveProjectContext(file, projectCache);
            var isWebConfig = file.EndsWith(".config", StringComparison.OrdinalIgnoreCase);

            IEnumerable<RawEntry> entries;
            try
            {
                entries = isWebConfig ? ParseWebConfig(file) : ParseAppSettingsJson(file);
            }
            catch (Exception ex)
            {
                warningList.Add(new DiscoveryWarning(file, $"parse failed: {ex.Message}"));
                continue;
            }

            foreach (var entry in entries)
            {
                if (IsPlaceholderValue(entry.ConnectionString))
                {
                    warningList.Add(new DiscoveryWarning(
                        file, $"connection '{entry.Name}' has an empty or placeholder value"));
                    continue;
                }

                var providerToken = ResolveProvider(entry, ctx, isWebConfig);
                if (providerToken is null)
                {
                    warningList.Add(new DiscoveryWarning(
                        file, $"could not infer provider for connection '{entry.Name}'"));
                    continue;
                }

                var alias = SanitizeAlias($"{ctx.Name}_{entry.Name}");
                try
                {
                    var provider = DbProviderFactory.Create(providerToken, alias, entry.ConnectionString);
                    results[alias] = provider;
                }
                catch (Exception ex)
                {
                    warningList.Add(new DiscoveryWarning(file, $"alias '{alias}' rejected: {ex.Message}"));
                }
            }
        }

        return results.Values
            .OrderBy(p => p.Alias, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> EnumerateConfigFiles(string root)
    {
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { continue; }

            foreach (var f in files)
            {
                if (IsConfigFile(Path.GetFileName(f)))
                    yield return f;
            }

            if (depth >= MaxDirectoryDepth) continue;

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch { continue; }

            foreach (var sub in subdirs)
            {
                var subName = Path.GetFileName(sub);
                if (s_skipDirs.Contains(subName)) continue;
                if (subName.Length > 0 && subName[0] == '.') continue;
                stack.Push((sub, depth + 1));
            }
        }
    }

    internal static bool IsConfigFile(string fileName)
    {
        var (matched, env) = ClassifyConfigFile(fileName);
        if (!matched) return false;
        if (env is null) return true;
        if (s_templateInfixes.Contains(env)) return false;
        if (s_prodEnvNames.Contains(env)) return false;
        return true;
    }

    /// <summary>
    /// Returns whether <paramref name="fileName"/> is a recognized config file and,
    /// if so, the environment qualifier (e.g. "Development" for
    /// "appsettings.Development.json"). <c>env</c> is null for the base file.
    /// </summary>
    private static (bool matched, string? env) ClassifyConfigFile(string fileName)
    {
        if (TryMatchDotNetFrameworkConfig(fileName, "web", out var fxEnv) ||
            TryMatchDotNetFrameworkConfig(fileName, "app", out fxEnv))
            return (true, fxEnv);

        if (fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) &&
            fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            const int prefixLen = 11;  // "appsettings"
            const int suffixLen = 5;   // ".json"
            var middleLen = fileName.Length - prefixLen - suffixLen;
            if (middleLen == 0) return (true, null);
            if (fileName[prefixLen] != '.') return (false, null);
            var env = fileName.Substring(prefixLen + 1, middleLen - 1);
            if (env.Length == 0) return (false, null);
            return (true, env);
        }

        return (false, null);
    }

    /// <summary>
    /// Lower-priority files apply first. Higher-priority files override them.
    /// 0: base config (<c>appsettings.json</c>, <c>web.config</c>, <c>app.config</c>).
    /// 1: any other environment-specific config.
    /// 2: development-flavored env (<c>Development</c>, <c>Dev</c>, <c>Local</c>, <c>Debug</c>).
    /// </summary>
    private static int GetFilePriority(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        var (_, env) = ClassifyConfigFile(fileName);
        if (env is null) return 0;
        if (s_devEnvNames.Contains(env)) return 2;
        return 1;
    }

    private static bool TryMatchDotNetFrameworkConfig(string fileName, string prefix, out string? env)
    {
        env = null;
        const string suffix = ".config";
        if (fileName.Length < prefix.Length + suffix.Length) return false;
        if (!fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var middleLen = fileName.Length - prefix.Length - suffix.Length;
        if (middleLen == 0) return true; // web.config / app.config
        if (fileName[prefix.Length] != '.') return false; // e.g. "webapp.config" must not match "web"
        env = fileName.Substring(prefix.Length + 1, middleLen - 1);
        return env.Length > 0;
    }

    private static ProjectContext ResolveProjectContext(
        string filePath, Dictionary<string, ProjectContext> cache)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(filePath)!);
        while (dir is not null)
        {
            FileInfo? csproj;
            try { csproj = dir.GetFiles("*.csproj").FirstOrDefault(); }
            catch { csproj = null; }
            if (csproj is not null)
            {
                if (cache.TryGetValue(csproj.FullName, out var existing))
                    return existing;
                var name = Path.GetFileNameWithoutExtension(csproj.Name);
                var pkg = DetectPackageProvider(csproj.FullName);
                var ctx = new ProjectContext(name, pkg);
                cache[csproj.FullName] = ctx;
                return ctx;
            }
            dir = dir.Parent;
        }
        var fallback = new DirectoryInfo(Path.GetDirectoryName(filePath)!).Name;
        return new ProjectContext(fallback, null);
    }

    internal static string? DetectPackageProvider(string csprojPath)
    {
        XmlDocument doc;
        try
        {
            doc = new XmlDocument();
            doc.Load(csprojPath);
        }
        catch
        {
            return null;
        }

        var refs = doc.SelectNodes("//PackageReference/@Include");
        if (refs is null) return null;

        var providers = new HashSet<string>(StringComparer.Ordinal);
        foreach (XmlAttribute attr in refs)
        {
            var name = attr.Value.ToLowerInvariant();
            // Order matters: "sqlite" check before "sqlserver"/"sqlclient" since
            // "Microsoft.EntityFrameworkCore.Sqlite" doesn't contain those.
            if (name.StartsWith("npgsql", StringComparison.Ordinal) ||
                name.Contains("postgresql"))
            {
                providers.Add("psql");
            }
            else if (name.Contains("sqlite"))
            {
                providers.Add("sqlite");
            }
            else if (name.Contains("sqlserver") || name.EndsWith(".sqlclient", StringComparison.Ordinal))
            {
                providers.Add("mssql");
            }
        }

        return providers.Count == 1 ? providers.First() : null;
    }

    internal static string SanitizeAlias(string raw)
    {
        Span<char> buffer = stackalloc char[raw.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            buffer[i] = char.IsLetterOrDigit(c) || c == '_' ? c : '_';
        }
        return new string(buffer);
    }

    private const string XdtNamespace = "http://schemas.microsoft.com/XML-Document-Transform";

    private static IEnumerable<RawEntry> ParseWebConfig(string path)
    {
        var doc = new XmlDocument();
        doc.Load(path);

        var section = doc.SelectSingleNode("/configuration/connectionStrings");
        if (section?.Attributes?["configProtectionProvider"] is not null)
            throw new InvalidOperationException(
                "<connectionStrings> is encrypted (configProtectionProvider set); skipping.");

        // XDT transform files (web.Debug.config) may declare the section with
        // xdt:Transform="Remove"/"RemoveAll" — drop the whole section in that case.
        var sectionTransform = section?.Attributes?["Transform", XdtNamespace]?.Value;
        if (IsRemoveTransform(sectionTransform))
            return Array.Empty<RawEntry>();

        var nodes = doc.SelectNodes("/configuration/connectionStrings/add");
        var list = new List<RawEntry>();
        if (nodes is null) return list;

        foreach (XmlNode node in nodes)
        {
            // Skip <add xdt:Transform="Remove"/> entries from transform files.
            var transform = node.Attributes?["Transform", XdtNamespace]?.Value;
            if (IsRemoveTransform(transform)) continue;

            var name = node.Attributes?["name"]?.Value;
            var connStr = node.Attributes?["connectionString"]?.Value;
            var providerName = node.Attributes?["providerName"]?.Value;
            if (string.IsNullOrEmpty(name)) continue;
            list.Add(new RawEntry(name, connStr ?? string.Empty, providerName));
        }
        return list;
    }

    private static bool IsRemoveTransform(string? transform) =>
        string.Equals(transform, "Remove", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(transform, "RemoveAll", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<RawEntry> ParseAppSettingsJson(string path)
    {
        var list = new List<RawEntry>();
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });

        if (!doc.RootElement.TryGetProperty("ConnectionStrings", out var cs) ||
            cs.ValueKind != JsonValueKind.Object)
            return list;

        foreach (var prop in cs.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.String) continue;
            var connStr = prop.Value.GetString();
            list.Add(new RawEntry(prop.Name, connStr ?? string.Empty, null));
        }
        return list;
    }

    private static string? ResolveProvider(RawEntry entry, ProjectContext ctx, bool isWebConfig)
    {
        // 1. Explicit providerName attribute (web.config).
        var p = MapProviderName(entry.ProviderNameAttr);
        if (p is not null) return p;

        // 2. Connection-string content heuristics (most reliable signal we have).
        p = SniffProvider(entry.ConnectionString);
        if (p is not null) return p;

        // 3. Name hint — "SiteSqlServer", "PostgresReports", "AnalyticsSqlite".
        p = GuessProviderFromName(entry.Name);
        if (p is not null) return p;

        // 4. Single DB provider package referenced by the project.
        if (ctx.PackageProvider is not null) return ctx.PackageProvider;

        // 5. .NET Framework default — SqlClient is part of the framework, so an
        //    untyped <connectionStrings> entry historically meant SQL Server.
        if (isWebConfig) return "mssql";

        return null;
    }

    internal static string? MapProviderName(string? providerName)
    {
        if (string.IsNullOrEmpty(providerName)) return null;
        var p = providerName.ToLowerInvariant();
        // Match `.sqlclient` (System.Data.SqlClient / Microsoft.Data.SqlClient) without
        // catching MySql.Data.MySqlClient.
        if (p.Contains(".sqlclient")) return "mssql";
        if (p.Contains("npgsql") || p.Contains("postgres")) return "psql";
        if (p.Contains("sqlite")) return "sqlite";
        return null;
    }

    internal static string? SniffProvider(string connStr)
    {
        var lower = connStr.ToLowerInvariant();

        if (lower.Contains(":memory:")) return "sqlite";
        if (lower.Contains("filename=") && !lower.Contains("server=")) return "sqlite";
        if ((lower.Contains("data source=") || lower.Contains("datasource=")) &&
            (lower.Contains(".db") || lower.Contains(".sqlite")) &&
            !lower.Contains("initial catalog=") &&
            !lower.Contains("integrated security="))
            return "sqlite";

        if (lower.Contains("host=") &&
            (lower.Contains("username=") || lower.Contains("user id=") || lower.Contains("port=")))
            return "psql";

        if (lower.Contains("server=") || lower.Contains("data source=") ||
            lower.Contains("initial catalog=") || lower.Contains("integrated security="))
            return "mssql";

        return null;
    }

    internal static bool IsPlaceholderValue(string? connStr)
    {
        if (string.IsNullOrWhiteSpace(connStr)) return true;
        var trimmed = connStr.Trim();
        // Common templating syntaxes left unfilled in committed configs:
        //   ${VAR}, $(VAR), {{VAR}}, #{VAR}, %VAR%
        if (trimmed.Contains("${") || trimmed.Contains("$(") ||
            trimmed.Contains("{{") || trimmed.Contains("#{"))
            return true;
        // Bare angle-bracket placeholder like `<your connection string>` —
        // distinguishable from a real connection string (real ones have `=`).
        if (trimmed.StartsWith('<') && trimmed.EndsWith('>') && !trimmed.Contains('='))
            return true;
        // %VAR% placeholders — only treat as placeholder when the entire trimmed
        // value is `%...%` (avoid false positives on URL-encoded chars).
        if (trimmed.Length >= 3 && trimmed[0] == '%' && trimmed[^1] == '%' &&
            trimmed.IndexOf('%', 1) == trimmed.Length - 1)
            return true;
        return false;
    }

    internal static string? GuessProviderFromName(string name)
    {
        var lower = name.ToLowerInvariant();
        // Order: postgres before sqlite before sqlserver, since some names contain
        // multiple substrings ("Postgresql" contains "sql"). Skip the bare "sql"
        // token — too ambiguous.
        if (lower.Contains("postgres") || lower.Contains("npgsql") || lower.Contains("psql"))
            return "psql";
        if (lower.Contains("sqlite")) return "sqlite";
        if (lower.Contains("sqlserver") || lower.Contains("mssql")) return "mssql";
        return null;
    }
}
