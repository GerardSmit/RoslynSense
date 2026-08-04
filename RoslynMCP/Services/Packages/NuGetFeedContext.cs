using System.Collections.Concurrent;
using System.Net;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;

namespace RoslynMCP.Services.Packages;

/// <summary>A configured feed as the panel needs to describe it, not just its name.</summary>
public sealed record PackageSourceInfo(
    string Name,
    string Source,
    bool IsEnabled,
    bool IsMachineWide,
    bool IsLocal,
    bool HasCredentials,
    string? ConfigFilePath);

/// <summary>
/// What one feed did during a fan-out.
/// </summary>
/// <remarks>
/// Carried back to the caller rather than only logged: "no results" and "the feed that has your
/// package rejected your credentials" look identical in a list of packages, and only the second
/// one is actionable.
/// </remarks>
public sealed record FeedOutcome(
    string Name,
    string Source,
    bool Ok,
    bool Unauthorized,
    string? Error);

/// <summary>Results gathered across every feed, alongside what each feed actually did.</summary>
public sealed record FeedResults<T>(IReadOnlyList<T> Results, IReadOnlyList<FeedOutcome> Feeds);

/// <summary>
/// The NuGet.config chain, resolved once and shared by every package operation.
///
/// Feed access lives behind this type so that source mapping, credentials and per-source failure
/// reporting are decided in one place. Querying feeds one after another — which is what the
/// package panel used to do — makes the slowest feed the latency of every search, and swallowing
/// a feed's exception into a log line makes a rejected credential indistinguishable from a
/// package that simply does not exist.
/// </summary>
public static class NuGetFeedContext
{
    private static readonly ConcurrentDictionary<string, SourceRepository> s_repositories =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly object s_gate = new();
    private static ISettings? s_settings;
    private static string? s_settingsRoot;
    private static PackageSourceMapping? s_mapping;

    /// <summary>
    /// Drops the resolved configuration. Called when a NuGet.config changes: a new source or a
    /// new credential must take effect without reloading the workspace.
    /// </summary>
    public static void Invalidate()
    {
        lock (s_gate)
        {
            s_settings = null;
            s_settingsRoot = null;
            s_mapping = null;
        }
        s_repositories.Clear();
    }

    // ---- Test hook (exposed via InternalsVisibleTo) ----
    // The write operations edit a real NuGet.config, so the tests that cover them have to be able
    // to point the chain at a directory of their own rather than the developer's.
    internal static string? SettingsRootOverride { get; set; }

    /// <summary>The NuGet.config chain for the loaded solution, or for the working directory.</summary>
    public static ISettings Settings()
    {
        string root = SettingsRootOverride
            ?? Path.GetDirectoryName(WorkspaceService.TryGetMostRecentSolution()?.FilePath)
            ?? Directory.GetCurrentDirectory();

        lock (s_gate)
        {
            // The solution can change under a long-lived daemon, and each one has its own chain.
            if (s_settings is { } cached && string.Equals(s_settingsRoot, root, StringComparison.OrdinalIgnoreCase))
                return cached;

            var settings = NuGet.Configuration.Settings.LoadDefaultSettings(root);
            s_settings = settings;
            s_settingsRoot = root;
            s_mapping = null;
            return settings;
        }
    }

    /// <summary>
    /// Every configured source, including disabled ones — a feed the user switched off is
    /// information the panel wants to show, not something to hide.
    /// </summary>
    public static IReadOnlyList<PackageSourceInfo> Sources()
    {
        try
        {
            var settings = Settings();
            var configPaths = ConfigPathsByName(settings);

            return new PackageSourceProvider(settings)
                .LoadPackageSources()
                .Select(source => new PackageSourceInfo(
                    source.Name,
                    source.Source,
                    source.IsEnabled,
                    source.IsMachineWide,
                    source.IsLocal,
                    source.Credentials is { } credentials && credentials.IsValid(),
                    configPaths.GetValueOrDefault(source.Name)))
                .ToList();
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"Could not read NuGet sources: {ex.Message}", key: "nuget-sources");
            return [];
        }
    }

    /// <summary>
    /// The repositories a query should reach, honoring Package Source Mapping when a package id
    /// is known — a mapped id must never be resolved from an unmapped feed, which is the whole
    /// point of the feature.
    /// </summary>
    public static IReadOnlyList<SourceRepository> Repositories(string? packageId = null)
    {
        // Credentials must be installed before the first repository builds its HTTP handler:
        // NuGet reads the credential service once, when the handler is created.
        NuGetCredentialService.Install();

        PackageSource[] sources;
        try
        {
            var settings = Settings();
            sources = SettingsUtility.GetEnabledSources(settings).ToArray();

            if (packageId is { Length: > 0 } && Mapping(settings) is { IsEnabled: true } mapping)
            {
                var mapped = mapping.GetConfiguredPackageSources(packageId);
                if (mapped is { Count: > 0 })
                {
                    sources = sources
                        .Where(source => mapped.Contains(source.Name, StringComparer.OrdinalIgnoreCase))
                        .ToArray();
                }
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"Could not load NuGet sources: {ex.Message}", key: "nuget-load");
            return [];
        }

        return sources
            .Select(source => s_repositories.GetOrAdd(source.Source, _ => Repository.Factory.GetCoreV3(source)))
            .ToList();
    }

    /// <summary>
    /// Runs a query against every relevant feed at once and reports what each one did.
    /// </summary>
    /// <remarks>
    /// A feed that throws contributes an outcome rather than an exception: one unreachable
    /// internal mirror should not empty the results of the four feeds that answered.
    /// </remarks>
    public static async Task<FeedResults<T>> FanOutAsync<T>(
        string? packageId,
        Func<SourceRepository, CancellationToken, Task<IEnumerable<T>>> work,
        CancellationToken ct)
    {
        var repositories = Repositories(packageId);
        if (repositories.Count == 0)
            return new FeedResults<T>([], []);

        var gathered = await Task.WhenAll(repositories.Select(async repository =>
        {
            var source = repository.PackageSource;
            try
            {
                var items = await work(repository, ct);
                return (Items: items.ToList(),
                        Outcome: new FeedOutcome(source.Name, source.Source, Ok: true, Unauthorized: false, Error: null));
            }
            // Only the caller giving up. A feed's own internal timeout also surfaces as an
            // OperationCanceledException, and rethrowing that would empty the results of every
            // feed that did answer — the exact opposite of what this method is for.
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                bool unauthorized = IsUnauthorized(ex);
                ServiceLog.Warn(
                    unauthorized
                        ? $"'{source.Name}' rejected the request. Sign in to the feed, or add credentials to NuGet.config."
                        : $"Feed '{source.Name}' failed: {ex.Message}",
                    // A distinct key from the ordinary failure: the five-minute repeat window
                    // must not let a timeout swallow the message that says how to fix it.
                    key: unauthorized ? $"nuget-auth:{source.Name}" : $"nuget-feed:{source.Name}");

                return (Items: new List<T>(),
                        Outcome: new FeedOutcome(source.Name, source.Source, Ok: false, unauthorized, Describe(ex)));
            }
        }));

        return new FeedResults<T>(
            gathered.SelectMany(g => g.Items).ToList(),
            gathered.Select(g => g.Outcome).ToList());
    }

    /// <summary>
    /// A cache context for one operation.
    /// </summary>
    /// <remarks>
    /// Rented rather than shared for the process lifetime, because <see cref="SourceCacheContext.MaxAge"/>
    /// is an absolute instant: a static instance quietly means "responses from before the daemon
    /// started are still fresh", which nobody chose. <paramref name="refresh"/> is what the panel's
    /// refresh button needs — a newly published version is invisible otherwise.
    /// </remarks>
    public static SourceCacheContext RentCache(bool refresh = false) =>
        refresh
            ? new SourceCacheContext { NoCache = true, DirectDownload = true }
            : new SourceCacheContext { MaxAge = DateTimeOffset.UtcNow.AddMinutes(-30) };

    /// <summary>The global packages folder, where a restored package already sits on disk.</summary>
    public static string? GlobalPackagesFolder()
    {
        try
        {
            return SettingsUtility.GetGlobalPackagesFolder(Settings());
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"Could not resolve the global packages folder: {ex.Message}", key: "nuget-gpf");
            return null;
        }
    }

    /// <summary>Adds a feed to the nearest writable NuGet.config.</summary>
    public static PackageOperationResult AddSource(string name, string source)
    {
        if (name is not { Length: > 0 })
            return new PackageOperationResult(false, "A feed needs a name.");

        if (Validate(source) is { } invalid)
            return new PackageOperationResult(false, invalid);

        return Mutate(provider =>
        {
            if (provider.GetPackageSourceByName(name) is not null)
                return $"A feed named '{name}' already exists.";

            provider.AddPackageSource(new PackageSource(source, name));
            return null;
        },
        $"Added '{name}'.");
    }

    /// <summary>Renames a feed, or points it somewhere else.</summary>
    public static PackageOperationResult UpdateSource(string name, string? newName, string? source)
    {
        if (source is { Length: > 0 } && Validate(source) is { } invalid)
            return new PackageOperationResult(false, invalid);

        // A rename moves the entry to the end of the file, so the order it had is captured here
        // and reapplied afterwards — otherwise renaming a feed silently changes which one answers
        // first.
        IReadOnlyList<string>? restoreOrder = null;

        var result = Mutate(provider =>
        {
            if (provider.GetPackageSourceByName(name) is not { } existing)
                return $"There is no feed named '{name}'.";

            if (existing.IsMachineWide)
                return MachineWide(name);

            if (newName is { Length: > 0 } && !newName.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                provider.GetPackageSourceByName(newName) is not null)
            {
                return $"A feed named '{newName}' already exists.";
            }

            var updated = new PackageSource(source ?? existing.Source, newName ?? existing.Name)
            {
                IsEnabled = existing.IsEnabled,
                ProtocolVersion = existing.ProtocolVersion,
                Credentials = existing.Credentials,
            };

            if (updated.Name.Equals(existing.Name, StringComparison.OrdinalIgnoreCase))
            {
                provider.UpdatePackageSource(updated, updateCredentials: false, updateEnabled: true);
                return null;
            }

            // UpdatePackageSource matches on name, so a rename has to be a remove plus an add.
            restoreOrder = provider.LoadPackageSources()
                .Select(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ? updated.Name : s.Name)
                .ToList();

            provider.RemovePackageSource(existing.Name);
            provider.AddPackageSource(updated);
            return null;
        },
        $"Updated '{name}'.");

        if (result.Success && restoreOrder is not null)
            ReorderSources(restoreOrder);

        return result;
    }

    public static PackageOperationResult RemoveSource(string name) =>
        Mutate(provider =>
        {
            if (provider.GetPackageSourceByName(name) is not { } existing)
                return $"There is no feed named '{name}'.";

            // NuGet can disable a machine-wide feed but not delete it: the file it lives in is
            // outside the user's config chain and needs elevation to touch.
            if (existing.IsMachineWide)
                return $"'{name}' is configured machine-wide, so it can be disabled but not removed.";

            provider.RemovePackageSource(name);
            return null;
        },
        $"Removed '{name}'.");

    /// <summary>
    /// Turns a feed on or off. The one edit that works on a machine-wide feed, because NuGet
    /// records the disabled state in the user's own config rather than the machine's.
    /// </summary>
    public static PackageOperationResult SetSourceEnabled(string name, bool enabled) =>
        Mutate(provider =>
        {
            if (provider.GetPackageSourceByName(name) is null)
                return $"There is no feed named '{name}'.";

            if (enabled)
                provider.EnablePackageSource(name);
            else
                provider.DisablePackageSource(name);

            return null;
        },
        enabled ? $"Enabled '{name}'." : $"Disabled '{name}'.");

    /// <summary>
    /// Reorders the feeds. Order is not cosmetic — it decides which feed answers first, and with
    /// it which one a package published to two feeds resolves from.
    /// </summary>
    /// <remarks>
    /// Done by rewriting the <c>packageSources</c> section rather than through
    /// <c>SavePackageSources</c>, which matches existing entries by key and updates them in place:
    /// it can add, retarget and remove a feed, but it cannot move one.
    /// </remarks>
    public static PackageOperationResult ReorderSources(IReadOnlyList<string> names) =>
        Mutate(provider =>
        {
            var settings = provider.Settings;
            var section = settings.GetSection("packageSources");
            if (section is null)
                return "There is no editable packageSources section.";

            // The section is the merged view, so it includes machine-wide feeds — and NuGet refuses
            // to edit those. Removing one throws partway through the loop, leaving the in-memory
            // chain missing every feed removed before it. Which feeds those are comes from the
            // source list rather than the item, whose Origin is not public.
            var machineWide = provider.LoadPackageSources()
                .Where(source => source.IsMachineWide)
                .Select(source => source.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var items = section.Items
                .OfType<SourceItem>()
                .Where(item => !machineWide.Contains(item.Key))
                .ToList();
            var byKey = items.ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);

            var ordered = names
                .Where(byKey.ContainsKey)
                .Select(name => byKey[name])
                .ToList();

            // Anything the caller did not mention keeps a place rather than being silently
            // dropped — the panel can be a moment out of date with the file.
            ordered.AddRange(items.Where(item => !names.Contains(item.Key, StringComparer.OrdinalIgnoreCase)));

            foreach (var item in items)
                settings.Remove("packageSources", item);

            foreach (var item in ordered)
            {
                // Rebuilt rather than re-added: a removed item cannot be handed back to the same
                // settings object. Every attribute is carried over, because dropping something
                // like allowInsecureConnections would break an internal HTTP feed on a reorder.
                settings.AddOrUpdate("packageSources", new SourceItem(
                    item.Key,
                    item.Value,
                    item.ProtocolVersion,
                    item.AllowInsecureConnections,
                    item.DisableTLSCertificateValidation));
            }

            settings.SaveToDisk();
            return null;
        },
        "Reordered the feeds.");

    /// <returns>An error message, or <c>null</c> when the source is usable.</returns>
    private static string? Validate(string source)
    {
        if (source is not { Length: > 0 })
            return "A feed needs a URL or a folder path.";

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            return null;

        return Directory.Exists(source)
            ? null
            : $"'{source}' is neither an http(s) URL nor a folder that exists.";
    }

    private static string MachineWide(string name) =>
        $"'{name}' is configured machine-wide and cannot be changed from here.";

    /// <summary>
    /// Runs an edit against the real config chain and reloads afterwards, so the next search sees
    /// the change without the workspace being reloaded.
    /// </summary>
    private static PackageOperationResult Mutate(Func<PackageSourceProvider, string?> edit, string success)
    {
        try
        {
            var provider = new PackageSourceProvider(Settings());

            if (edit(provider) is { } error)
                return new PackageOperationResult(false, error);

            Invalidate();
            return new PackageOperationResult(true, success);
        }
        catch (Exception ex)
        {
            // A partly-applied edit leaves the cached ISettings disagreeing with the file, so the
            // chain is dropped even on failure — otherwise a rejected edit would keep serving a
            // feed list that exists nowhere on disk.
            Invalidate();
            ServiceLog.Warn($"Could not update the NuGet sources: {ex.Message}", key: "nuget-sources-write");
            return new PackageOperationResult(false, ex.Message);
        }
    }

    private static PackageSourceMapping? Mapping(ISettings settings)
    {
        lock (s_gate)
        {
            return s_mapping ??= PackageSourceMapping.GetPackageSourceMapping(settings);
        }
    }

    /// <summary>Source name to the NuGet.config that declared it, for "where does this come from".</summary>
    private static Dictionary<string, string> ConfigPathsByName(ISettings settings)
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var item in settings.GetSection("packageSources")?.Items ?? [])
            {
                if (item is SourceItem { Key.Length: > 0 } source && source.ConfigPath is { Length: > 0 } path)
                    paths[source.Key] = path;
            }
        }
        catch
        {
            // A malformed section should cost the config path, not the source list.
        }
        return paths;
    }

    /// <summary>
    /// Whether a feed failure is a credential problem. NuGet wraps the transport exception, so
    /// the whole chain is walked rather than only the outermost frame.
    /// </summary>
    private static bool IsUnauthorized(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden })
                return true;

            if (current is FatalProtocolException &&
                (current.Message.Contains("401", StringComparison.Ordinal) ||
                 current.Message.Contains("403", StringComparison.Ordinal)))
                return true;
        }
        return false;
    }

    private static string Describe(Exception ex) =>
        ex.InnerException is { } inner && inner.Message.Length > 0 ? inner.Message : ex.Message;
}
