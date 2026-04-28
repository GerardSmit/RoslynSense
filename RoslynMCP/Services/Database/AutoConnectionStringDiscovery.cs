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

        var files = EnumerateConfigFiles(rootDir).ToList();
        files.Sort(StringComparer.OrdinalIgnoreCase);

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
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { continue; }

            foreach (var f in files)
            {
                var name = Path.GetFileName(f);
                if (name.Equals("web.config", StringComparison.OrdinalIgnoreCase))
                    yield return f;
                else if (name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) &&
                         name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    yield return f;
            }

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch { continue; }

            foreach (var sub in subdirs)
            {
                var subName = Path.GetFileName(sub);
                if (s_skipDirs.Contains(subName)) continue;
                if (subName.Length > 0 && subName[0] == '.') continue;
                stack.Push(sub);
            }
        }
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

    private static IEnumerable<RawEntry> ParseWebConfig(string path)
    {
        var doc = new XmlDocument();
        doc.Load(path);
        var nodes = doc.SelectNodes("/configuration/connectionStrings/add");
        var list = new List<RawEntry>();
        if (nodes is null) return list;

        foreach (XmlNode node in nodes)
        {
            var name = node.Attributes?["name"]?.Value;
            var connStr = node.Attributes?["connectionString"]?.Value;
            var providerName = node.Attributes?["providerName"]?.Value;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(connStr)) continue;
            list.Add(new RawEntry(name, connStr, providerName));
        }
        return list;
    }

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
            if (string.IsNullOrWhiteSpace(connStr)) continue;
            list.Add(new RawEntry(prop.Name, connStr!, null));
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
