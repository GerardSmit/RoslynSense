namespace RoslynMCP.Config;

/// <summary>
/// What changed between two resolved settings, as short human-readable phrases — the "why" a
/// host logs when it applies a configuration reload. Empty means nothing this type knows how
/// to name changed; the caller decides whether that means "nothing" or "something deeper in
/// the file".
/// </summary>
internal static class SettingsDiff
{
    public static IReadOnlyList<string> Describe(EffectiveSettings old, EffectiveSettings @new)
    {
        var changes = new List<string>();

        void Toggle(string name, bool before, bool after)
        {
            if (before != after)
                changes.Add($"{name}: {(before ? "on" : "off")} → {(after ? "on" : "off")}");
        }

        Toggle("webforms", old.WebForms, @new.WebForms);
        Toggle("razor", old.Razor, @new.Razor);
        Toggle("proto", old.Proto, @new.Proto);
        Toggle("mediator", old.Mediator, @new.Mediator);
        Toggle("resources", old.Resources.Enabled, @new.Resources.Enabled);
        Toggle("msbuild", old.MsBuild, @new.MsBuild);
        Toggle("dbml", old.Dbml, @new.Dbml);
        Toggle("appsettings", old.AppSettings, @new.AppSettings);
        Toggle("debugger", old.Debugger, @new.Debugger);
        Toggle("profiling", old.Profiling, @new.Profiling);
        Toggle("database", old.Database, @new.Database);

        if (!string.Equals(old.TableFormat, @new.TableFormat, StringComparison.OrdinalIgnoreCase))
            changes.Add($"tableFormat: {old.TableFormat ?? "markdown"} → {@new.TableFormat ?? "markdown"}");

        if (old.MaxWorkspaces != @new.MaxWorkspaces)
            changes.Add($"maxWorkspaces: {old.MaxWorkspaces} → {@new.MaxWorkspaces}");

        if (old.HostIdleMinutes != @new.HostIdleMinutes)
            changes.Add($"hostIdleMinutes: {old.HostIdleMinutes} → {@new.HostIdleMinutes}");

        // Named even though a running host cannot un-share itself: the change governs how the
        // NEXT client connects, and a log line is what tells the user why nothing visibly moved.
        if (old.SharedHost != @new.SharedHost)
            changes.Add($"sharedHost: {(old.SharedHost ? "on" : "off")} → {(@new.SharedHost ? "on" : "off")} (applies to new clients)");

        if (old.AutoDiscoverDb != @new.AutoDiscoverDb)
            changes.Add("database auto-discovery changed");

        DescribeConnections(old, @new, changes);

        if (!SamePaths(old.Preload, @new.Preload))
            changes.Add("preload paths changed");

        return changes;
    }

    /// <summary>Connection aliases that appeared or went away. A connection string edited under
    /// an unchanged alias is invisible here — the caller's raw-text comparison catches it.</summary>
    private static void DescribeConnections(EffectiveSettings old, EffectiveSettings @new, List<string> changes)
    {
        var before = old.ExplicitDbProviders.Select(p => p.Alias).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var after = @new.ExplicitDbProviders.Select(p => p.Alias).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = after.Except(before, StringComparer.OrdinalIgnoreCase).OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
        var removed = before.Except(after, StringComparer.OrdinalIgnoreCase).OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();

        if (added.Count > 0 || removed.Count > 0)
        {
            var parts = added.Select(a => $"+{a}").Concat(removed.Select(r => $"-{r}"));
            changes.Add($"database connections: {string.Join(", ", parts)}");
        }
    }

    private static bool SamePaths(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        // Null and empty differ deliberately: null means auto-discover, empty means disabled.
        if (a is null || b is null) return a is null && b is null;
        return a.SequenceEqual(b, StringComparer.OrdinalIgnoreCase);
    }
}
