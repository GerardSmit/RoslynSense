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
    public static ServiceProvider Build(EffectiveSettings settings, IOutputFormatter formatter, string workingDir)
    {
        var dbProviders = ResolveDbProviders(settings, workingDir);

        var services = new ServiceCollection();
        services.AddSingleton(settings);
        services.AddSingleton(formatter);
        services.AddSingleton<ProfilingSessionStore>();
        services.AddSingleton<ProfileRecordingStore>();
        services.AddSingleton<RoslynMCP.Services.Memory.MemorySnapshotStore>();
        services.AddSingleton<BackgroundTaskStore>();
        services.AddSingleton<BuildWarningsStore>();
        services.AddSingleton(new DbConnectionRegistry(dbProviders));
        services.AddSingleton<ExecutionPlanStore>();

        services.AddSingleton<DesignerRegenerationService>();
        services.AddSingleton<SolutionSessionService>();
        services.AddSingleton<AppSessionStore>();
        services.AddSingleton<AppRunService>();
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
