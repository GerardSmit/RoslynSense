using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using RoslynMCP.Config;
using RoslynMCP.Services;
using RoslynMCP.Services.Database;
using RoslynMCP.Tools;
using RoslynMCP.Tools.Razor;
using RoslynMCP.Tools.WebForms;

[ExcludeFromCodeCoverage]
class Program
{
    static async Task<int> Main(string[] args)
    {
        // CLI mode: roslyn-sense --cli [tool] [options]
        // Runs a single tool and prints the result, without starting the MCP server.
        if (args.Length > 0 && args[0].Equals("--cli", StringComparison.OrdinalIgnoreCase))
            return await RoslynMCP.CliRunner.RunAsync(args[1..]);

        var startupWarnings = new List<string>();

        var (config, configPath, configError) = RoslynSenseConfigLoader.Load(Directory.GetCurrentDirectory());
        if (configError is not null)
            startupWarnings.Add($"roslynsense.json ({configPath}): {configError}");

        var settings = EffectiveSettings.Resolve(args, config, out var settingsWarnings);
        startupWarnings.AddRange(settingsWarnings);

        IReadOnlyList<IDbProvider> dbProviders;
        IReadOnlyList<AutoConnectionStringDiscovery.DiscoveryWarning> autoDbWarnings = Array.Empty<AutoConnectionStringDiscovery.DiscoveryWarning>();
        if (!settings.Database)
        {
            dbProviders = Array.Empty<IDbProvider>();
        }
        else if (!settings.ShouldRunAutoDiscovery())
        {
            dbProviders = settings.ExplicitDbProviders;
        }
        else
        {
            var auto = AutoConnectionStringDiscovery.Discover(Directory.GetCurrentDirectory(), out autoDbWarnings);
            var existing = new HashSet<string>(settings.ExplicitDbProviders.Select(p => p.Alias), StringComparer.OrdinalIgnoreCase);
            var merged = new List<IDbProvider>(settings.ExplicitDbProviders);
            foreach (var p in auto)
                if (existing.Add(p.Alias))
                    merged.Add(p);
            dbProviders = merged;
        }

        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.AddConsole(consoleLogOptions =>
        {
            // Configure all logs to go to stderr
            consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
        });
        builder.Services.AddSingleton(settings);
        builder.Services.AddHostedService<InfrastructureCleanupHostedService>();
        builder.Services.AddHostedService<WorkspacePreloadHostedService>();

        // Register output formatter (markdown default, TOON via tableFormat=="toon")
        bool useToon = string.Equals(settings.TableFormat, "toon", StringComparison.OrdinalIgnoreCase);
        builder.Services.AddSingleton<IOutputFormatter>(useToon ? new ToonFormatter() : new MarkdownFormatter());
        builder.Services.AddSingleton<ProfilingSessionStore>();
        builder.Services.AddSingleton<BackgroundTaskStore>();
        builder.Services.AddSingleton<BuildWarningsStore>();
        builder.Services.AddSingleton(new DbConnectionRegistry(dbProviders));

        // Register non-C# file type handlers conditionally
        if (settings.WebForms)
        {
            builder.Services.AddSingleton<IGoToDefinitionHandler, AspxGoToDefinition>();
            builder.Services.AddSingleton<IFindUsagesHandler, AspxFindUsages>();
            builder.Services.AddSingleton<IOutlineHandler, AspxOutline>();
            builder.Services.AddSingleton<IRenameHandler, AspxRename>();
            builder.Services.AddSingleton<IDiagnosticsHandler, AspxDiagnostics>();
        }

        if (settings.Razor)
        {
            builder.Services.AddSingleton<IGoToDefinitionHandler, RazorGoToDefinition>();
            builder.Services.AddSingleton<IOutlineHandler, RazorOutline>();
            builder.Services.AddSingleton<IRenameHandler, RazorRename>();
            builder.Services.AddSingleton<IDiagnosticsHandler, RazorDiagnostics>();
        }

        var toolTypes = typeof(Program).Assembly
            .GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .Where(t => settings.Debugger || !t.Name.StartsWith("Debug", StringComparison.Ordinal))
            .Where(t => settings.Profiling || !t.Name.StartsWith("Profile", StringComparison.Ordinal))
            .Where(t => settings.Database || !t.Name.StartsWith("Database", StringComparison.Ordinal))
            .ToArray();

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools((IEnumerable<Type>)toolTypes)
            .WithResourcesFromAssembly()
            .WithPromptsFromAssembly();

        var host = builder.Build();

        if (startupWarnings.Count > 0 || autoDbWarnings.Count > 0)
        {
            var logger = host.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("RoslynMCP.Startup");
            foreach (var w in startupWarnings)
                logger.LogWarning("{Message}", w);
            foreach (var w in autoDbWarnings)
                logger.LogWarning("Auto-db: {File}: {Message}", w.File, w.Message);
        }

        if (configPath is not null && configError is null)
        {
            var logger = host.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("RoslynMCP.Startup");
            logger.LogInformation("Loaded roslynsense.json from {Path}", configPath);
        }

        await host.RunAsync();
        return 0;
    }
}
