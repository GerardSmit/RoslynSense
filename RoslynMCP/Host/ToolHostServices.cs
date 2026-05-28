using Microsoft.Extensions.DependencyInjection;
using RoslynMCP.Config;
using RoslynMCP.Services;
using RoslynMCP.Services.Database;
using RoslynMCP.Tools;
using RoslynMCP.Tools.Razor;
using RoslynMCP.Tools.WebForms;

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
        services.AddSingleton<BackgroundTaskStore>();
        services.AddSingleton<BuildWarningsStore>();
        services.AddSingleton(new DbConnectionRegistry(dbProviders));
        services.AddSingleton<ExecutionPlanStore>();

        if (settings.WebForms)
        {
            services.AddSingleton<IGoToDefinitionHandler, AspxGoToDefinition>();
            services.AddSingleton<IFindUsagesHandler, AspxFindUsages>();
            services.AddSingleton<IOutlineHandler, AspxOutline>();
            services.AddSingleton<IRenameHandler, AspxRename>();
            services.AddSingleton<IDiagnosticsHandler, AspxDiagnostics>();
        }

        if (settings.Razor)
        {
            services.AddSingleton<IGoToDefinitionHandler, RazorGoToDefinition>();
            services.AddSingleton<IOutlineHandler, RazorOutline>();
            services.AddSingleton<IRenameHandler, RazorRename>();
            services.AddSingleton<IDiagnosticsHandler, RazorDiagnostics>();
        }

        return services.BuildServiceProvider();
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
