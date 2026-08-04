using System.Collections.Concurrent;
using NuGet.Versioning;

namespace RoslynMCP.Services.Packages;

/// <summary>How far a version is allowed to move.</summary>
public enum VersionLock
{
    /// <summary>Anything newer.</summary>
    None,

    /// <summary>Stay on the current major.</summary>
    Major,

    /// <summary>Stay on the current major and minor.</summary>
    Minor,

    /// <summary>
    /// Stay on the .NET major the project targets, for the families that version with the
    /// platform. Unbounded for every other package.
    /// </summary>
    Framework,
}

/// <summary>Whether prereleases are candidates.</summary>
public enum PrereleaseReporting
{
    /// <summary>Match what is currently referenced — a stable reference stays stable.</summary>
    Auto,
    Always,
    Never,
}

/// <param name="Unknown">
/// No feed produced any version for the package. Distinct from "up to date" on purpose: a rejected
/// credential otherwise looks exactly like a package that needs nothing.
/// </param>
public enum UpdateSeverity
{
    None,
    Patch,
    Minor,
    Major,
    Unknown,
}

public sealed record UpdateQuery(
    bool IncludePrerelease = false,
    VersionLock Lock = VersionLock.None,
    PrereleaseReporting Prerelease = PrereleaseReporting.Auto,
    string? PrereleaseLabel = null,
    IReadOnlyList<string>? ProjectPaths = null,
    bool Refresh = false);

public sealed record PackageUpdate(
    string Id,
    string CurrentVersion,
    string LatestVersion,
    UpdateSeverity Severity,
    string ProjectPath,
    string ProjectName,
    bool IsCentrallyManaged,
    bool IsGlobalPackageReference,
    string? VersionSource);

public sealed record PackageUpdateRequest(string Id, string Version, IReadOnlyList<string> ProjectPaths);

public sealed record PackageUpdateOutcome(
    string Id, string Version, string ProjectPath, bool Success, string? Message);

public sealed record PackageUpdateResult(
    bool Success, string Message, IReadOnlyList<PackageUpdateOutcome> Results);

/// <summary>
/// Which packages have moved on, and how far — the model <c>dotnet-outdated</c> established.
/// </summary>
/// <remarks>
/// The candidate version is chosen by NuGet's own <see cref="FloatRange"/> rather than by
/// filtering a version list afterwards, which is what makes a version lock mean "the newest
/// version within this bound" instead of "hide the ones that moved too far". That distinction is
/// the entire value of the feature: locked to the current major, 13.0.1 should offer 13.0.3, not
/// nothing at all.
/// </remarks>
public static class PackageUpdateService
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<FeedResults<NuGetVersion>>>> s_versions =
        new(StringComparer.OrdinalIgnoreCase);

    public static void Invalidate() => s_versions.Clear();

    /// <summary>Installed packages with a newer version available, one row per project.</summary>
    public static async Task<FeedResults<PackageUpdate>> OutdatedAsync(
        UpdateQuery query, CancellationToken ct)
    {
        if (query.Refresh)
            Invalidate();

        var projects = await NuGetService.InstalledAsync(ct);
        if (query.ProjectPaths is { Count: > 0 } wanted)
        {
            projects = projects
                .Where(p => wanted.Contains(p.ProjectPath, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        var candidates = projects
            .SelectMany(project => project.Packages
                .Where(package => package.Version is { Length: > 0 })
                .Select(package => (Project: project, Package: package)))
            .ToList();

        if (candidates.Count == 0)
            return new FeedResults<PackageUpdate>([], []);

        // Resolved once per project rather than once per reference: evaluation is cached, but a
        // two-hundred-reference solution would still go through the cache two hundred times.
        var frameworks = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (query.Lock == VersionLock.Framework)
        {
            foreach (var project in projects)
            {
                frameworks[project.ProjectPath] =
                    await PackageFrameworkService.FrameworksOfAsync(project.ProjectPath, ct);
            }
        }

        await using var progress = await ProgressReporter.BeginAsync("Checking for package updates", ct);

        var updates = new ConcurrentBag<PackageUpdate>();
        var feeds = new ConcurrentDictionary<string, FeedOutcome>(StringComparer.OrdinalIgnoreCase);
        int done = 0;

        // Bounded: nuget.org rate-limits, and a private feed will start refusing connections
        // under a two-hundred-way fan-out.
        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount),
                CancellationToken = ct,
            },
            async (candidate, token) =>
            {
                var (project, package) = candidate;

                var lookup = await VersionsAsync(package.Id, query, token);

                // Failure wins: first-write-wins would let one package's successful lookup hide a
                // later 401 on the same feed, and reporting every feed healthy while packages come
                // back Unknown is exactly the confusion FeedOutcome exists to prevent.
                foreach (var feed in lookup.Feeds)
                    feeds.AddOrUpdate(feed.Name, feed, (_, existing) => existing.Ok ? feed : existing);

                progress.Report(package.Id, Interlocked.Increment(ref done) * 100 / candidates.Count);

                // A reference can carry a range or a floating version rather than a plain one.
                // Dropping those silently reported them as up to date.
                if (Current(package.Version) is not { } current)
                    return;

                if (lookup.Results.Count == 0)
                {
                    updates.Add(Row(project, package, package.Version, UpdateSeverity.Unknown));
                    return;
                }

                var projectFrameworks = frameworks.GetValueOrDefault(project.ProjectPath, []);

                int? cap = FrameworkVersionPolicy.TracksPlatformVersion(package.Id)
                    ? FrameworkVersionPolicy.PlatformMajor(projectFrameworks)
                    : null;

                var latest = Resolve(current, package.Version, lookup.Results, query, cap);
                if (latest is null || latest <= current)
                    return;

                if (query.Lock == VersionLock.Framework)
                {
                    latest = await CompatibleAsync(
                        package.Id, current, latest, lookup.Results, projectFrameworks, token);

                    if (latest is null || latest <= current)
                        return;
                }

                updates.Add(Row(project, package, latest.ToNormalizedString(), SeverityOf(current, latest)));
            });

        return new FeedResults<PackageUpdate>(
            updates
                .OrderBy(u => u.Id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(u => u.ProjectName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            feeds.Values.ToList());
    }

    /// <summary>
    /// Applies a set of updates in one pass.
    /// </summary>
    /// <remarks>
    /// Sequential on purpose. Under Central Package Management every project writes the same
    /// props file, so two concurrent <c>dotnet add package</c> runs lose one of the two edits.
    /// The win here is one restore and one workspace reload instead of one per package, not
    /// overlapped writes.
    /// </remarks>
    public static async Task<PackageUpdateResult> UpdateAllAsync(
        IReadOnlyList<PackageUpdateRequest> packages, bool restore, CancellationToken ct)
    {
        if (packages.Count == 0)
            return new PackageUpdateResult(false, "Nothing selected.", []);

        await using var progress = await ProgressReporter.BeginAsync(
            $"Updating {packages.Count} package(s)", ct);
        await using var scope = new PackageMutationScope(ct);

        var outcomes = new List<PackageUpdateOutcome>();
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int done = 0;

        foreach (var request in packages)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report($"{request.Id} {request.Version}", ++done * 100 / packages.Count);

            foreach (string projectPath in request.ProjectPaths)
            {
                var outcome = await ApplyAsync(request, projectPath, scope, ct);
                outcomes.Add(outcome);

                if (outcome.Success)
                {
                    scope.Touch(projectPath);
                    touched.Add(projectPath);
                }
            }
        }

        // One restore for the batch, after every project file is final. Every edit was applied
        // with --no-restore, so this is the only step that checks the chosen versions actually
        // exist for these target frameworks — reporting success without looking at it would tell
        // the user everything worked while their build is now red.
        string? restoreError = null;
        if (restore && touched.Count > 0)
        {
            progress.Report("restoring");
            restoreError = await RestoreAsync(touched, ct);
        }

        string? redirects = await FixBindingRedirectsAsync(touched, ct);

        Invalidate();
        PackageAuditService.Invalidate();

        int failed = outcomes.Count(o => !o.Success);
        string message = failed == 0
            ? $"Updated {packages.Count} package(s)."
            : $"Updated {outcomes.Count - failed} of {outcomes.Count}; {failed} failed.";

        if (redirects is { Length: > 0 })
            message += $" {redirects}";

        if (restoreError is { Length: > 0 })
            message += $" Restore failed: {restoreError}";

        return new PackageUpdateResult(failed == 0 && restoreError is null, message, outcomes);
    }

    /// <summary>
    /// Brings each touched project's binding redirects back in line with what it now ships.
    /// </summary>
    /// <remarks>
    /// Runs after restore, so the packages folder holds the versions the redirects are compared
    /// against. A stale redirect after an update is never something anyone wanted — it is the
    /// update half-applied — so it is repaired rather than reported, and the message says which
    /// ones moved. Adding a redirect that was never there is a different question and stays with
    /// the code action.
    /// </remarks>
    /// <returns>A sentence for the result message, or <c>null</c> when nothing needed fixing.</returns>
    private static async Task<string?> FixBindingRedirectsAsync(
        IReadOnlyCollection<string> projects, CancellationToken ct)
    {
        var fixedNames = new List<string>();

        foreach (string projectPath in projects)
        {
            var report = await BindingRedirectService.AnalyzeAsync(projectPath, ct);
            if (report.ConfigPath is null)
                continue;

            var stale = report.Findings
                .Where(f => f.Problem == BindingRedirectProblem.Stale)
                .ToList();

            if (stale.Count == 0)
                continue;

            foreach (var applied in BindingRedirectService.Apply(report.ConfigPath, stale))
                fixedNames.Add($"{applied.AssemblyName} → {applied.RequiredVersion}");
        }

        if (fixedNames.Count == 0)
            return null;

        return $"Updated {fixedNames.Count} binding redirect(s): {string.Join(", ", fixedNames.Distinct())}.";
    }

    private static async Task<PackageUpdateOutcome> ApplyAsync(
        PackageUpdateRequest request, string projectPath, PackageMutationScope scope, CancellationToken ct)
    {
        if (PackagesConfigService.Uses(projectPath))
        {
            // The batch's scope, not none: without it every legacy package reloads the whole
            // workspace mid-run, which is the cost the batch exists to avoid.
            var legacy = await PackagesConfigService.InstallAsync(
                projectPath, request.Id, request.Version, ct, scope);
            return new PackageUpdateOutcome(
                request.Id, request.Version, projectPath, legacy.Success,
                legacy.Success ? null : legacy.Message);
        }

        // A centrally managed version lives in the props file, and the reference in the csproj
        // carries none — editing the props file directly is both correct and one write for every
        // project that shares it.
        if (await CentralVersionSourceAsync(projectPath, request.Id, ct) is { } propsPath &&
            CentralPackageVersionWriter.TrySetVersion(propsPath, request.Id, request.Version))
        {
            return new PackageUpdateOutcome(request.Id, request.Version, projectPath, true, null);
        }

        var (exitCode, output) = await NuGetService.RunDotnetAsync(
            ["add", projectPath, "package", request.Id, "--version", request.Version, "--no-restore"], ct);

        return new PackageUpdateOutcome(
            request.Id, request.Version, projectPath,
            exitCode == 0,
            exitCode == 0 ? null : NuGetService.FirstLine(output));
    }

    private static async Task<string?> CentralVersionSourceAsync(
        string projectPath, string packageId, CancellationToken ct)
    {
        var evaluation = await ProjectModel.ProjectEvaluationService.EvaluateAsync(projectPath, ct);

        var reference = evaluation?.PackageReferences
            .FirstOrDefault(p => p.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase));

        // A VersionOverride lives in the csproj, so the props file is the wrong place to edit it.
        if (reference is not { IsCentrallyManaged: true, IsVersionOverride: false })
            return null;

        return reference.VersionSource ?? CentralPackageVersionWriter.FindNearest(projectPath);
    }

    /// <returns>The first restore failure, or <c>null</c> when everything restored.</returns>
    private static async Task<string?> RestoreAsync(
        IReadOnlyCollection<string> projects, CancellationToken ct)
    {
        // One solution-wide restore beats one per project, and it is what the CLI does anyway.
        string? solution = WorkspaceService.TryGetMostRecentSolution()?.FilePath;
        if (solution is { Length: > 0 })
        {
            var (exitCode, output) = await NuGetService.RunDotnetAsync(["restore", solution], ct);
            return exitCode == 0 ? null : NuGetService.FirstLine(output);
        }

        foreach (string project in projects)
        {
            var (exitCode, output) = await NuGetService.RunDotnetAsync(["restore", project], ct);
            if (exitCode != 0)
                return $"{Path.GetFileName(project)}: {NuGetService.FirstLine(output)}";
        }

        return null;
    }

    /// <summary>
    /// The newest version a reference is allowed to move to.
    /// </summary>
    /// <remarks>
    /// The float range is the filter, not a post-pass: <see cref="VersionRange.FindBestMatch"/>
    /// applies the lock, the prerelease policy and the release-label prefix together, the way
    /// restore itself resolves a floating version.
    /// </remarks>
    internal static NuGetVersion? Resolve(
        NuGetVersion current,
        string rawVersion,
        IReadOnlyList<NuGetVersion> available,
        UpdateQuery query,
        int? platformMajor = null)
    {
        // The cap only means anything if the package publishes that major. Applying it to a family
        // that does not version with the platform would bound the search to a band that was never
        // released, which reads as "up to date" — the one answer worse than offering too much.
        if (query.Lock == VersionLock.Framework &&
            platformMajor is { } cap &&
            available.Any(version => version.Major == cap))
        {
            available = available.Where(version => version.Major <= cap).ToList();
        }

        bool includePrerelease = query.Prerelease switch
        {
            PrereleaseReporting.Always => true,
            PrereleaseReporting.Never => false,
            _ => current.IsPrerelease || query.IncludePrerelease,
        };

        var behaviour = (query.Lock, includePrerelease) switch
        {
            (VersionLock.Major, true) => NuGetVersionFloatBehavior.PrereleaseMinor,
            (VersionLock.Major, false) => NuGetVersionFloatBehavior.Minor,
            (VersionLock.Minor, true) => NuGetVersionFloatBehavior.PrereleasePatch,
            (VersionLock.Minor, false) => NuGetVersionFloatBehavior.Patch,
            (_, true) => NuGetVersionFloatBehavior.AbsoluteLatest,
            _ => NuGetVersionFloatBehavior.Major,
        };

        string releasePrefix = "";
        if (current.IsPrerelease)
        {
            releasePrefix = query.PrereleaseLabel is { Length: > 0 } label
                ? label
                : current.ReleaseLabels.FirstOrDefault() ?? "";
        }

        // A reference can carry a range rather than a version — "[8.0.0,9.0.0)" must keep its
        // upper bound.
        var currentRange = VersionRange.TryParse(rawVersion, out var parsed)
            ? parsed
            : new VersionRange(current);

        var range = new VersionRange(currentRange, new FloatRange(behaviour, current, releasePrefix));
        var best = range.FindBestMatch(available.Where(v => includePrerelease || !v.IsPrerelease));

        // The float range does the work, but it has one hole: when the referenced version is not
        // in the feed's list at all — unlisted after a bad release, a local build, a feed
        // migration — nothing satisfies the float and NuGet falls back to the lowest version above
        // the base range, which ignores the lock entirely. "Same major only" must never hand back
        // a different major.
        if (best is null || WithinLock(current, best, query.Lock))
            return best;

        return null;
    }

    /// <summary>
    /// How many versions the probe is willing to reject before it gives up.
    /// </summary>
    /// <remarks>
    /// Each rejection costs a registration lookup. In practice the newest version is compatible or
    /// the one below it is; a package that fails four in a row has moved to a framework this
    /// project cannot use at all, which is the answer rather than a reason to keep walking back
    /// through its history.
    /// </remarks>
    private const int MaxCompatibilityProbes = 4;

    /// <summary>
    /// The newest version at or below <paramref name="candidate"/> that has something the
    /// project's target frameworks can use.
    /// </summary>
    /// <remarks>
    /// Version numbers do not carry this. <c>Microsoft.AspNetCore.*</c> 9.x is a perfectly ordinary
    /// version bump that no lock would stop, and the first sign it cannot be used from net8.0 is
    /// NU1202 out of restore — after the reference has been written.
    /// </remarks>
    /// <returns><c>null</c> when nothing in range is usable, so the package reports no update.</returns>
    private static async Task<NuGetVersion?> CompatibleAsync(
        string id,
        NuGetVersion current,
        NuGetVersion candidate,
        IReadOnlyList<NuGetVersion> available,
        IReadOnlyList<string> projectFrameworks,
        CancellationToken ct)
    {
        if (projectFrameworks.Count == 0)
            return candidate;

        var ordered = available
            .Where(version => version > current && version <= candidate)
            .Where(version => !version.IsPrerelease || current.IsPrerelease || candidate.IsPrerelease)
            .OrderByDescending(version => version)
            .Take(MaxCompatibilityProbes)
            .ToList();

        foreach (var version in ordered)
        {
            var check = await PackageFrameworkService.CheckAsync(
                id, version.ToNormalizedString(), projectFrameworks, ct);

            if (check.Compatible)
                return version;
        }

        return null;
    }

    /// <summary>
    /// The version a reference is currently on. A <c>PackageReference</c> may carry a range or a
    /// floating version rather than a plain one; its lower bound is what "currently on" means.
    /// </summary>
    internal static NuGetVersion? Current(string raw)
    {
        if (NuGetVersion.TryParse(raw, out var exact))
            return exact;

        return VersionRange.TryParse(raw, out var range) ? range.MinVersion : null;
    }

    private static bool WithinLock(NuGetVersion current, NuGetVersion candidate, VersionLock versionLock) =>
        versionLock switch
        {
            VersionLock.Major => candidate.Major == current.Major,
            VersionLock.Minor => candidate.Major == current.Major && candidate.Minor == current.Minor,
            _ => true,
        };

    /// <summary>
    /// How far the package has moved. A reference that is itself a prerelease counts as a major
    /// move regardless of the numbers: leaving a prerelease is the change worth flagging.
    /// </summary>
    internal static UpdateSeverity SeverityOf(NuGetVersion current, NuGetVersion latest)
    {
        if (latest.Major > current.Major || current.IsPrerelease)
            return UpdateSeverity.Major;
        if (latest.Minor > current.Minor)
            return UpdateSeverity.Minor;
        if (latest.Patch > current.Patch || latest.Revision > current.Revision)
            return UpdateSeverity.Patch;
        return UpdateSeverity.None;
    }

    /// <summary>
    /// Every version of a package, fetched once however many projects reference it. A solution
    /// with forty projects on the same package would otherwise issue forty identical lookups.
    /// </summary>
    internal static Task<FeedResults<NuGetVersion>> VersionsAsync(
        string id, UpdateQuery query, CancellationToken ct)
    {
        // The listing is not filtered by target framework, so the framework is deliberately not
        // part of the key — including it would fragment the cache for nothing.
        string key = $"{id}|{query.Refresh}";

        var entry = s_versions.GetOrAdd(key, _ => new Lazy<Task<FeedResults<NuGetVersion>>>(
            () => NuGetService.AllVersionsAsync(id, includePrerelease: true, query.Refresh, ct),
            LazyThreadSafetyMode.ExecutionAndPublication));

        var task = entry.Value;

        // Canceled as well as faulted: the entry captures the first caller's token, so closing the
        // panel mid-check would otherwise leave a canceled task cached under that package id and
        // fail every later update check for it.
        if (task.IsCompleted && !task.IsCompletedSuccessfully)
        {
            s_versions.TryRemove(key, out _);
            return Task.FromResult(new FeedResults<NuGetVersion>([], []));
        }

        return task;
    }

    private static PackageUpdate Row(
        ProjectPackages project, PackageSummary package, string latest, UpdateSeverity severity) =>
        new(package.Id,
            package.Version,
            latest,
            severity,
            project.ProjectPath,
            project.ProjectName,
            package.IsCentrallyManaged,
            package.IsGlobalPackageReference,
            package.VersionSource);
}
