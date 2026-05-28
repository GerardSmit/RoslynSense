using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Daemon;

/// <summary>
/// Thin-client wiring: advertises the real tools and resource templates to the MCP client but
/// forwards every tool call and resource read to the shared host over a named pipe. If the host
/// is unreachable or the IPC call fails, the call runs in-process (current behavior) so a daemon
/// problem never breaks a chat.
/// </summary>
internal static class DaemonClient
{
    public static void Configure(
        IMcpServerBuilder builder,
        IReadOnlyList<MethodInfo> toolMethods,
        IReadOnlyList<MethodInfo> resourceMethods,
        string solutionKey,
        string format)
    {
        ConfigureTools(builder, toolMethods, solutionKey, format);
        ConfigureResources(builder, resourceMethods, solutionKey, format);
    }

    private static void ConfigureTools(
        IMcpServerBuilder builder, IReadOnlyList<MethodInfo> toolMethods, string solutionKey, string format)
    {
        List<Tool>? toolListCache = null;

        // Debug tools share a single, process-wide, stateful session (DebugSessionManager +
        // a netcoredbg subprocess). Forwarding them to a host shared across chats would make
        // two chats fight over one session. Instead they run IN-PROCESS in each client, so
        // every chat gets its own independent debug session. The heavy read/analysis tools
        // still go to the shared host.
        var inProcessTools = new HashSet<string>(
            toolMethods
                .Where(m => m.DeclaringType?.Name.StartsWith("Debug", StringComparison.Ordinal) == true)
                .Select(ToolInvoker.ToolCommandName),
            StringComparer.OrdinalIgnoreCase);

        builder.WithListToolsHandler((ctx, _) =>
        {
            toolListCache ??= toolMethods
                .Select(m => McpServerTool.Create(
                    m, (object?)null, new McpServerToolCreateOptions { Services = ctx.Services }).ProtocolTool)
                .ToList();
            return ValueTask.FromResult(new ListToolsResult { Tools = toolListCache });
        });

        builder.WithCallToolHandler(async (ctx, ct) =>
        {
            var name = ctx.Params?.Name;
            if (string.IsNullOrEmpty(name))
                return ErrorResult("Missing tool name.");

            var args = ConvertArguments(ctx.Params!.Arguments);
            bool runLocal = inProcessTools.Contains(name!);
            var (ok, text) = await ForwardOrRunAsync(
                "tool", name!, args, ctx.Services!, solutionKey, format, ct, forceLocal: runLocal);
            return ok ? TextResult(text) : ErrorResult(text);
        });
    }

    private static void ConfigureResources(
        IMcpServerBuilder builder, IReadOnlyList<MethodInfo> resourceMethods, string solutionKey, string format)
    {
        List<ResourceTemplate>? templateCache = null;

        builder.WithListResourceTemplatesHandler((ctx, _) =>
        {
            templateCache ??= resourceMethods
                .Select(m => McpServerResource.Create(
                    m, (object?)null, new McpServerResourceCreateOptions { Services = ctx.Services }).ProtocolResourceTemplate)
                .ToList();
            return ValueTask.FromResult(new ListResourceTemplatesResult { ResourceTemplates = templateCache });
        });

        // No fixed (non-templated) resources are exposed — only the templates above.
        builder.WithListResourcesHandler((_, _) =>
            ValueTask.FromResult(new ListResourcesResult { Resources = new List<Resource>() }));

        builder.WithReadResourceHandler(async (ctx, ct) =>
        {
            string uri = ctx.Params?.Uri ?? "";
            var parsed = ParseResourceUri(uri, resourceMethods);
            if (parsed is null)
                return new ReadResourceResult { Contents = [] };

            var (resourceName, filePath) = parsed.Value;
            var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["filePath"] = filePath };
            var (_, text) = await ForwardOrRunAsync("resource", resourceName, args, ctx.Services!, solutionKey, format, ct);

            return new ReadResourceResult
            {
                Contents = [new TextResourceContents { Uri = uri, MimeType = "text/plain", Text = text }]
            };
        });
    }

    /// <summary>
    /// Forwards a tool/resource invocation to the shared host; on any IPC failure, runs it in
    /// this process against the local DI container. Returns (success, resultOrErrorText).
    /// </summary>
    private static async Task<(bool Ok, string Text)> ForwardOrRunAsync(
        string kind, string name, Dictionary<string, string> args,
        IServiceProvider services, string solutionKey, string format, CancellationToken ct,
        bool forceLocal = false)
    {
        if (!forceLocal)
        {
            try
            {
                var pipe = await DaemonSpawner.ConnectOrSpawnAsync(solutionKey, ct);
                if (pipe is not null)
                {
                    await using (pipe)
                    {
                        var request = new DaemonRequest(Guid.NewGuid().ToString("N"), name, args, format, kind);
                        await IpcProtocol.WriteMessageAsync(pipe, request, ct);
                        var response = await IpcProtocol.ReadMessageAsync<DaemonResponse>(pipe, ct);
                        if (response is not null)
                            return (response.Ok, response.Ok ? response.Result ?? "" : response.Error ?? $"{kind} failed.");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SharedHost] Forwarding failed, running in-process: {ex.Message}");
            }
        }

        // In-process fallback.
        var method = string.Equals(kind, "resource", StringComparison.Ordinal)
            ? ToolInvoker.FindResource(name)
            : ToolInvoker.FindTool(name);
        if (method is null)
            return (false, $"Unknown {kind} '{name}'.");

        IOutputFormatter fmt =
            services.GetService(typeof(IOutputFormatter)) as IOutputFormatter
            ?? (string.Equals(format, "toon", StringComparison.OrdinalIgnoreCase)
                ? new ToonFormatter()
                : new MarkdownFormatter());

        try
        {
            return (true, await ToolInvoker.InvokeAsync(method, args, services, fmt, ct));
        }
        catch (Exception ex)
        {
            return (false, (ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message);
        }
    }

    /// <summary>
    /// Matches <paramref name="uri"/> against each resource's UriTemplate prefix (everything
    /// before the first <c>{</c>) and returns the resource name + URL-decoded tail value.
    /// </summary>
    private static (string Name, string FilePath)? ParseResourceUri(string uri, IReadOnlyList<MethodInfo> resourceMethods)
    {
        foreach (var m in resourceMethods)
        {
            var template = m.GetCustomAttribute<McpServerResourceAttribute>()?.UriTemplate ?? "";
            int brace = template.IndexOf('{');
            if (brace < 0)
                continue;

            string prefix = template[..brace];
            if (uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return (ToolInvoker.ResourceName(m), Uri.UnescapeDataString(uri[prefix.Length..]));
        }
        return null;
    }

    private static Dictionary<string, string> ConvertArguments(IDictionary<string, JsonElement>? arguments)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (arguments is null)
            return result;

        foreach (var (key, value) in arguments)
        {
            result[key] = value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : value.GetRawText();
        }
        return result;
    }

    private static CallToolResult TextResult(string text) =>
        new() { Content = [new TextContentBlock { Text = text }] };

    private static CallToolResult ErrorResult(string message) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = message }] };
}
