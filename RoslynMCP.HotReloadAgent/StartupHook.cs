using System.Reflection.Metadata;
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

        Start(pipeName!);
    }

    /// <summary>
    /// Starts the agent in a process the runtime never loaded this assembly into — one a debugger
    /// attached to and injected it after the fact.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The startup-hook contract cannot be used for that: the runtime reads
    /// <c>DOTNET_STARTUP_HOOKS</c> once, before any managed code runs, so an app already running is
    /// past the only moment it would have been honoured. A debugger, though, can call any static
    /// method in the target, and this is one. Everything the hook would have taken from the
    /// environment is passed in instead, because the environment of a process that was not
    /// launched for this says nothing about it.
    /// </para>
    /// <para>
    /// Returns its refusal rather than writing it anywhere, so the debugger can put it in front of
    /// the person who asked instead of in the application's own error stream, where it would look
    /// like the application complaining about itself.
    /// </para>
    /// </remarks>
    /// <returns>Empty once the listener is running; otherwise why it is not.</returns>
    public static string Attach(string pipeName)
    {
        if (string.IsNullOrEmpty(pipeName))
            return "no agent pipe was named";

        // The authoritative form of the question the hook asks the environment. A process not
        // started with DOTNET_MODIFIABLE_ASSEMBLIES=debug loads its assemblies in a shape no
        // update can be applied to, and every apply would be accepted and silently do nothing —
        // which is worse than not offering hot reload at all.
        if (!MetadataUpdater.IsSupported)
        {
            return "this process was not started with DOTNET_MODIFIABLE_ASSEMBLIES=debug, " +
                "so the runtime cannot apply an update to it";
        }

        try
        {
            // Already listening is success, not a failure: the caller wanted hot reload on, and it
            // is. Saying otherwise would make asking twice look like something went wrong.
            Start(pipeName);
            return string.Empty;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Whether a listener has been started, so that a second attempt does nothing.</summary>
    private static int s_started;

    /// <summary>
    /// Starts the listener, once per process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Once, because a second listener is not a harmless duplicate. Each one is a separate
    /// connection to the agent server, the server sends every delta to every connection it holds,
    /// and the second application of a delta fails on the generation it has already consumed — so
    /// from then on every edit is reported as failing against a process that in fact took it. The
    /// ways in are easy to reach: an app launched with hot reload and then injected into as well,
    /// the same injection asked for twice, or this assembly listed twice in the hook variable.
    /// </para>
    /// <para>
    /// On its own thread because the caller must not be held: as a startup hook this runs before
    /// <c>Main</c>, and as an injection it runs inside a debugger evaluation that is on a clock.
    /// Either way the connect is allowed to take as long as it takes.
    /// </para>
    /// </remarks>
    /// <returns>Whether this call is the one that started it.</returns>
    private static bool Start(string pipeName)
    {
        if (Interlocked.Exchange(ref s_started, 1) != 0)
            return false;

        var thread = new Thread(() => HotReloadAgent.Listen(pipeName))
        {
            IsBackground = true,
            Name = "roslyn-sense hot reload",
        };
        thread.Start();
        return true;
    }
}
