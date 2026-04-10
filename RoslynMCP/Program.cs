using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using RoslynMCP.Services;
using RoslynMCP.Tools;
using RoslynMCP.Tools.Razor;
using RoslynMCP.Tools.WebForms;

[ExcludeFromCodeCoverage]
class Program
{
    static async Task Main(string[] args)
    {
        bool noWebForms = args.Contains("--no-webforms", StringComparer.OrdinalIgnoreCase);
        bool noRazor = args.Contains("--no-razor", StringComparer.OrdinalIgnoreCase);

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

        // Register non-C# file type handlers conditionally
        if (!noWebForms)
        {
            builder.Services.AddSingleton<IGoToDefinitionHandler, AspxGoToDefinition>();
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

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly()
            .WithResourcesFromAssembly()
            .WithPromptsFromAssembly();
        await builder.Build().RunAsync();
    }
}
