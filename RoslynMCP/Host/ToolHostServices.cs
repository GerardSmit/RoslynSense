using Microsoft.Extensions.DependencyInjection;
using RoslynMCP.Config;
using RoslynMCP.Services;
using RoslynMCP.Services.Database;
using RoslynMCP.Services.Designers;
using RoslynMCP.Services.Run;
using RoslynMCP.Tools;
using RoslynMCP.Languages;

namespace RoslynMCP.Daemon;

/// <summary>
/// Builds the dependency-injection container that backs tool invocation in the shared-host
/// daemon. Mirrors the singleton registrations in <c>Program.Main</c> (minus the MCP server
/// itself), so a tool invoked in the daemon sees the same services it would in-process.
/// </summary>
internal static class ToolHostServices
{
    public static ServiceProvider Build(EffectiveSettings settings, IOutputFormatter formatter, string workingDir) =>
        Build(settings, formatter, workingDir, carryFrom: null);

    /// <summary>
    /// Builds the container, optionally carrying the stateful stores over from a previous one.
    /// </summary>
    /// <remarks>
    /// <paramref name="carryFrom"/> is what makes a live configuration reload safe: the stores
    /// hold state the user can see — running apps, background tasks, profiling sessions, open
    /// designer watches — and rebuilding them because a feature toggle changed would kill all of
    /// it. Everything the settings actually shape (language packs, the registry, database
    /// connections) is built fresh. The old provider must NOT be disposed while the new one
    /// lives: disposing it disposes the carried stores, which the new container only borrows.
    /// </remarks>
    public static ServiceProvider Build(
        EffectiveSettings settings, IOutputFormatter formatter, string workingDir, ServiceProvider? carryFrom)
    {
        var dbProviders = ResolveDbProviders(settings, workingDir);

        var services = new ServiceCollection();
        services.AddSingleton(settings);
        services.AddSingleton(formatter);
        Carry<ProfilingSessionStore>(services, carryFrom);
        Carry<ProfileRecordingStore>(services, carryFrom);
        Carry<RoslynMCP.Services.Memory.MemorySnapshotStore>(services, carryFrom);
        Carry<BackgroundTaskStore>(services, carryFrom);
        Carry<BuildWarningsStore>(services, carryFrom);
        services.AddSingleton(new DbConnectionRegistry(dbProviders));
        Carry<ExecutionPlanStore>(services, carryFrom);

        services.AddSingleton<DesignerRegenerationService>();
        // Carried WITH its old DesignerRegenerationService reference: it holds the live designer
        // watches, and dropping those to honour a generator-set change trades visible state for a
        // toggle that can wait until the host restarts.
        Carry<SolutionSessionService>(services, carryFrom);
        Carry<AppSessionStore>(services, carryFrom);
        Carry<AppRunService>(services, carryFrom);
        services.AddSingleton<IDesignerGenerator, DbmlDesignerGenerator>();

        if (settings.WebForms)
            services.AddSingleton<IDesignerGenerator, AspxDesignerGenerator>();

        services.AddLanguagePacks(settings);

        var provider = services.BuildServiceProvider();

        // Resolved here rather than left to the first caller that wants it. LanguageRegistry
        // publishes itself as it is constructed, and the only other thing that asks the container
        // for one is the LSP server's initialize — so a daemon serving nothing but MCP tools would
        // leave LanguageRegistry.Current empty, and every static that reads it would answer as
        // though no pack were registered at all.
        provider.GetRequiredService<LanguageRegistry>();

        return provider;
    }

    /// <summary>The previous container's instance when there is one, a fresh registration
    /// otherwise. Instance registrations are not disposed by the container that borrows them,
    /// which is exactly the ownership a carried store needs.</summary>
    private static void Carry<T>(IServiceCollection services, ServiceProvider? carryFrom) where T : class
    {
        if (carryFrom is null)
            services.AddSingleton<T>();
        else
            services.AddSingleton(carryFrom.GetRequiredService<T>());
    }

    private static IReadOnlyList<IDbProvider> ResolveDbProviders(EffectiveSettings settings, string workingDir)
    {
        if (!settings.Database)
            return Array.Empty<IDbProvider>();
        if (!settings.ShouldRunAutoDiscovery())
            return settings.ExplicitDbProviders;

        var auto = AutoConnectionStringDiscovery.Discover(workingDir, out _);
        var existing = new HashSet<string>(settings.ExplicitDbProviders.Select(p => p.Alias), StringComparer.OrdinalIgnoreCase);
        var merged = new List<IDbProvider>(settings.ExplicitDbProviders);
        foreach (var p in auto)
            if (existing.Add(p.Alias))
                merged.Add(p);
        return merged;
    }
}
