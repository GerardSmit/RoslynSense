using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
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

        bool noWebForms = args.Contains("--no-webforms", StringComparer.OrdinalIgnoreCase);
        bool noRazor = args.Contains("--no-razor", StringComparer.OrdinalIgnoreCase);
        bool noDebugger = args.Contains("--no-debugger", StringComparer.OrdinalIgnoreCase);
        bool noProfiling = args.Contains("--no-profiling", StringComparer.OrdinalIgnoreCase);
        bool noDb = args.Contains("--no-db", StringComparer.OrdinalIgnoreCase);
        bool noAutoDb = args.Contains("--no-auto-db", StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<IDbProvider> dbProviders;
        IReadOnlyList<AutoConnectionStringDiscovery.DiscoveryWarning> autoDbWarnings = Array.Empty<AutoConnectionStringDiscovery.DiscoveryWarning>();
        if (noDb)
        {
            dbProviders = Array.Empty<IDbProvider>();
        }
        else
        {
            var explicitProviders = DbCliParser.Parse(args);
            if (noAutoDb)
            {
                dbProviders = explicitProviders;
            }
            else
            {
                var auto = AutoConnectionStringDiscovery.Discover(Directory.GetCurrentDirectory(), out autoDbWarnings);
                var existing = new HashSet<string>(explicitProviders.Select(p => p.Alias), StringComparer.OrdinalIgnoreCase);
                var merged = new List<IDbProvider>(explicitProviders);
                foreach (var p in auto)
                    if (existing.Add(p.Alias))
                        merged.Add(p);
                dbProviders = merged;
            }
        }

        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.AddConsole(consoleLogOptions =>
        {
            // Configure all logs to go to stderr
            consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
        });
        builder.Services.AddHostedService<InfrastructureCleanupHostedService>();

        // Register output formatter (markdown default, TOON via --toon)
        bool useToon = args.Contains("--toon", StringComparer.OrdinalIgnoreCase);
        builder.Services.AddSingleton<IOutputFormatter>(useToon ? new ToonFormatter() : new MarkdownFormatter());
        builder.Services.AddSingleton<ProfilingSessionStore>();
        builder.Services.AddSingleton<BackgroundTaskStore>();
        builder.Services.AddSingleton<BuildWarningsStore>();
        builder.Services.AddSingleton(new DbConnectionRegistry(dbProviders));

        // Register non-C# file type handlers conditionally
        if (!noWebForms)
        {
            builder.Services.AddSingleton<IGoToDefinitionHandler, AspxGoToDefinition>();
            builder.Services.AddSingleton<IFindUsagesHandler, AspxFindUsages>();
            builder.Services.AddSingleton<IOutlineHandler, AspxOutline>();
            builder.Services.AddSingleton<IRenameHandler, AspxRename>();
            builder.Services.AddSingleton<IDiagnosticsHandler, AspxDiagnostics>();
        }

        if (!noRazor)
        {
            builder.Services.AddSingleton<IGoToDefinitionHandler, RazorGoToDefinition>();
            builder.Services.AddSingleton<IOutlineHandler, RazorOutline>();
            builder.Services.AddSingleton<IRenameHandler, RazorRename>();
            builder.Services.AddSingleton<IDiagnosticsHandler, RazorDiagnostics>();
        }

        var toolTypes = typeof(Program).Assembly
            .GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .Where(t => !(noDebugger && t.Name.StartsWith("Debug", StringComparison.Ordinal)))
            .Where(t => !(noProfiling && t.Name.StartsWith("Profile", StringComparison.Ordinal)))
            .Where(t => !(noDb && t.Name.StartsWith("Database", StringComparison.Ordinal)))
            .ToArray();

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools((IEnumerable<Type>)toolTypes)
            .WithResourcesFromAssembly()
            .WithPromptsFromAssembly();

        var host = builder.Build();

        if (autoDbWarnings.Count > 0)
        {
            var logger = host.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("RoslynMCP.AutoDbDiscovery");
            foreach (var w in autoDbWarnings)
                logger.LogWarning("Auto-db: {File}: {Message}", w.File, w.Message);
        }

        await host.RunAsync();
        return 0;
    }
}
