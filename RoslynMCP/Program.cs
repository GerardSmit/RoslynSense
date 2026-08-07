using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using RoslynMCP.Config;
using RoslynMCP.Services;
using RoslynMCP.Services.Database;
using RoslynMCP.Services.Designers;
using RoslynMCP.Services.Run;
using RoslynMCP.Tools;
using RoslynMCP.Languages;

[ExcludeFromCodeCoverage]
class Program
{
    static async Task<int> Main(string[] args)
    {
        // Before anything else, including mode selection: a redirect hands the whole session over
        // as it is, whatever mode the host asked for.
        if (await RoslynMCP.DevBuildRedirect.TryRunAsync(args) is { } redirected)
            return redirected;

        // CLI mode: roslyn-sense --cli [tool] [options]
        // Runs a single tool and prints the result, without starting the MCP server.
        if (args.Length > 0 && args[0].Equals("--cli", StringComparison.OrdinalIgnoreCase))
            return await RoslynMCP.CliRunner.RunAsync(args[1..]);

        // LSP mode: roslyn-sense --lsp [--solution <path>]
        // Spawned by an editor as its C# language server; proxies LSP to the shared daemon so
        // the editor and MCP clients share one loaded solution (in-process fallback otherwise).
        if (args.Length > 0 && args[0].Equals("--lsp", StringComparison.OrdinalIgnoreCase))
            return await RoslynMCP.Lsp.LspProxy.RunAsync(args[1..]);

        // DAP mode: roslyn-sense --dap
        // A debug adapter for .NET Framework targets, backed by ICorDebug. netcoredbg speaks DAP
        // natively but only debugs CoreCLR, so this is what gives the editor F5 on Framework.
        if (args.Length > 0 && args[0].Equals("--dap", StringComparison.OrdinalIgnoreCase))
            return await RoslynMCP.Services.Debugging.DapServer.RunAsync(args[1..]);

        // Shared-host daemon mode: roslyn-sense --host <solution>
        // Long-lived process that owns the Roslyn workspaces for one solution and serves tool
        // calls forwarded by thin MCP-client processes over a named pipe.
        if (args.Length > 0 && args[0].Equals("--host", StringComparison.OrdinalIgnoreCase))
        {
            string target = args.Length > 1 ? args[1] : Directory.GetCurrentDirectory();
            // The daemon exists for exactly one solution; recording it means callers that need
            // the solution's identity do not have to wait for a project to be loaded first.
            WorkspaceService.BindSolution(target);
            return await RoslynMCP.Daemon.DaemonServer.RunHostAsync(target);
        }

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

        // Shared-host: when enabled and this working dir belongs to a solution, forward every
        // tool call to a per-solution daemon shared across chats (with in-process fallback).
        // Resolved up-front because it gates the hosted services below.
        string? sharedHostSolution = settings.SharedHost
            ? RoslynMCP.Daemon.HostPaths.ResolveSolutionKey(Directory.GetCurrentDirectory())
            : null;

        // A shared-host client is a pure forwarder: it never opens a workspace (the daemon does),
        // so it needs neither preload nor shutdown cleanup. Both touch WorkspaceService statically,
        // whose static ctor registers MSBuild and loads its assemblies — work a thin client must
        // avoid. (On the rare in-process fallback, process exit releases all OS resources and the
        // next startup orphan-sweeps temp dirs, so skipping explicit cleanup is safe.) The daemon
        // warms the solution and disposes its own workspaces on idle shutdown.
        if (sharedHostSolution is null)
        {
            builder.Services.AddHostedService<InfrastructureCleanupHostedService>();
            builder.Services.AddHostedService<WorkspacePreloadHostedService>();
        }

        // Returns immediately: it only starts a background request when the cached answer has
        // expired, so this costs nothing on a normal session.
        UpdateCheckService.BeginCheck();

        // Register output formatter (markdown default, TOON via tableFormat=="toon")
        bool useToon = string.Equals(settings.TableFormat, "toon", StringComparison.OrdinalIgnoreCase);
        builder.Services.AddSingleton<IOutputFormatter>(useToon ? new ToonFormatter() : new MarkdownFormatter());
        builder.Services.AddSingleton<ProfilingSessionStore>();
        builder.Services.AddSingleton<ProfileRecordingStore>();
        builder.Services.AddSingleton<RoslynMCP.Services.Memory.MemorySnapshotStore>();
        builder.Services.AddSingleton<BackgroundTaskStore>();
        builder.Services.AddSingleton<BuildWarningsStore>();
        builder.Services.AddSingleton(new DbConnectionRegistry(dbProviders));
        builder.Services.AddSingleton<ExecutionPlanStore>();

        builder.Services.AddSingleton<DesignerRegenerationService>();
        builder.Services.AddSingleton<SolutionSessionService>();
        builder.Services.AddSingleton<AppSessionStore>();
        builder.Services.AddSingleton<AppRunService>();
        builder.Services.AddSingleton<IDesignerGenerator, DbmlDesignerGenerator>();

        // Register non-C# file type handlers conditionally
        if (settings.WebForms)
            builder.Services.AddSingleton<IDesignerGenerator, AspxDesignerGenerator>();

        builder.Services.AddLanguagePacks(settings);

        var toolTypes = typeof(Program).Assembly
            .GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .Where(t => settings.Debugger || !t.Name.StartsWith("Debug", StringComparison.Ordinal))
            .Where(t => settings.Profiling || !t.Name.StartsWith("Profile", StringComparison.Ordinal))
            .Where(t => settings.Database || !t.Name.StartsWith("Database", StringComparison.Ordinal))
            .ToArray();

        // Cap cached workspaces (LRU) for this process. Skipped in shared-host-client mode:
        // touching WorkspaceService eagerly runs its static ctor (MSBuild + shadow-copy init),
        // which a thin forwarding client never needs. If it ever falls back to in-process, the
        // static ctor runs lazily then; the cap there is the default (the daemon sets its own).
        if (sharedHostSolution is null)
            WorkspaceService.MaxCachedWorkspaces = settings.MaxWorkspaces;

        // WithStdioServerTransport() would take stdout from Console.OpenStandardOutput(), whose
        // stream reports short and failed pipe writes as successes — silent data loss on a channel
        // that carries JSON-RPC, and tool results run well past the buffer size where it bites.
        // Same streams, opened the way a protocol needs. See StdIo.
        var mcpBuilder = builder.Services
            .AddMcpServer()
            .WithStreamServerTransport(Console.OpenStandardInput(), RoslynMCP.StdIo.OpenProtocolOutput());

        if (sharedHostSolution is not null)
        {
            var toolMethods = RoslynMCP.Daemon.ToolInvoker.AllTools
                .Where(m => IsToolEnabled(m, settings))
                .ToList();
            var resourceMethods = RoslynMCP.Daemon.ToolInvoker.AllResources;
            string format = useToon ? "toon" : "markdown";
            // Forward both tool calls AND resource reads to the daemon; resources otherwise run
            // in-process and would load a workspace here, defeating the shared host.
            RoslynMCP.Daemon.DaemonClient.Configure(mcpBuilder, toolMethods, resourceMethods, sharedHostSolution, format);
            startupWarnings.Add($"Shared host active for solution '{System.IO.Path.GetFileName(sharedHostSolution)}' (set ROSLYNMCP_SHARED_HOST=0 to disable).");
        }
        else
        {
            mcpBuilder
                .WithTools((IEnumerable<Type>)toolTypes)
                .WithResourcesFromAssembly();
        }

        mcpBuilder.WithPromptsFromAssembly();

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

    /// <summary>Mirrors the feature-flag filtering applied to tool TYPES, at the method level,
    /// so the shared-host client advertises exactly the enabled tools.</summary>
    private static bool IsToolEnabled(MethodInfo method, RoslynMCP.Config.EffectiveSettings settings)
    {
        var typeName = method.DeclaringType?.Name ?? "";
        if (!settings.Debugger && typeName.StartsWith("Debug", StringComparison.Ordinal)) return false;
        if (!settings.Profiling && typeName.StartsWith("Profile", StringComparison.Ordinal)) return false;
        if (!settings.Database && typeName.StartsWith("Database", StringComparison.Ordinal)) return false;
        return true;
    }
}
