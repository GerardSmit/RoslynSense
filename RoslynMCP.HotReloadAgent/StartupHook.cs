using RoslynMCP.HotReloadAgent;

/// <summary>
/// The entry point the runtime calls for every assembly listed in <c>DOTNET_STARTUP_HOOKS</c>.
/// </summary>
/// <remarks>
/// The name, the namespace (none), and the signature are all fixed by the runtime — it looks for
/// a type literally called <c>StartupHook</c> with a parameterless static <c>Initialize</c>. This
/// runs before <c>Main</c>, so it must return quickly: the listener is a background thread and the
/// app starts while it is still connecting.
/// </remarks>
internal static class StartupHook
{
    public static void Initialize()
    {
        string? pipeName = Environment.GetEnvironmentVariable(HotReloadAgent.PipeVariable);
        if (string.IsNullOrEmpty(pipeName))
            return;

        // `dotnet run` and the MSBuild nodes it spawns are managed apps too, and they inherit
        // the environment. Registering them would send deltas to processes that do not host the
        // edited module, and their runtimes would join the capability vote for an app they are
        // not. The real target is the apphost child, which passes this check.
        string host = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
        if (host.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
            host.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Applying a delta to a module the runtime did not prepare for it silently does nothing,
        // so say so once here rather than let every apply look like it worked.
        if (Environment.GetEnvironmentVariable("DOTNET_MODIFIABLE_ASSEMBLIES") is not "debug")
        {
            Console.Error.WriteLine(
                "[roslyn-sense] hot reload is inactive: DOTNET_MODIFIABLE_ASSEMBLIES was not set to 'debug'.");
            return;
        }

        var thread = new Thread(() => HotReloadAgent.Listen(pipeName!))
        {
            IsBackground = true,
            Name = "roslyn-sense hot reload",
        };
        thread.Start();
    }
}
