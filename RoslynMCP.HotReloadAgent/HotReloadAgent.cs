using System.IO.Pipes;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text;

namespace RoslynMCP.HotReloadAgent;

/// <summary>
/// Applies metadata deltas inside the running application.
/// </summary>
/// <remarks>
/// <para>
/// Edit-and-Continue on CoreCLR needs code executing <em>in</em> the target process:
/// <see cref="MetadataUpdater.ApplyUpdate"/> is the only supported way to change a loaded
/// assembly, and it can only be called from inside. There is no debugger in this path at all —
/// this is why hot reload works whether or not the app is being debugged, which is the point.
/// </para>
/// <para>
/// The channel is a named pipe rather than the diagnostic IPC socket because the tool already
/// knows how to run named pipes on every platform it supports, and because a startup hook must
/// not drag a dependency into the user's load context.
/// </para>
/// </remarks>
internal static class HotReloadAgent
{
    /// <summary>Names the pipe to connect back to. Absent means "not launched for hot reload".</summary>
    public const string PipeVariable = "ROSLYNSENSE_HOTRELOAD_PIPE";

    public const int ProtocolVersion = 2;

    private const int OpApplyUpdate = 1;

    /// <summary>
    /// What Roslyn may assume when it computes an edit, for runtimes too old to be asked.
    /// </summary>
    /// <remarks>
    /// This is the .NET 6 baseline. Reporting more than the runtime can do produces a delta it
    /// rejects at apply time; reporting less only costs the user a rude edit they did not need.
    /// </remarks>
    private static readonly string[] FallbackCapabilities =
    [
        "Baseline",
        "AddMethodToExistingType",
        "AddStaticFieldToExistingType",
        "AddInstanceFieldToExistingType",
        "NewTypeDefinition",
        "ChangeCustomAttributes",
    ];

    public static void Listen(string pipeName)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            pipe.Connect(timeout: 30_000);

            using var reader = new BinaryReader(pipe, Encoding.UTF8, leaveOpen: true);
            using var writer = new BinaryWriter(pipe, Encoding.UTF8, leaveOpen: true);

            Handshake(writer);

            while (pipe.IsConnected)
            {
                int operation;
                try
                {
                    operation = reader.ReadInt32();
                }
                catch (EndOfStreamException)
                {
                    return; // the tool went away; the app carries on unchanged
                }

                if (operation != OpApplyUpdate)
                    return;

                var (ok, error) = ApplyOne(reader);
                writer.Write(ok);
                writer.Write(error);
                writer.Flush();
            }
        }
        catch (Exception ex)
        {
            // A failed hot reload must never take the application with it.
            Console.Error.WriteLine($"[roslyn-sense] hot reload agent stopped: {ex.Message}");
        }
    }

    private static void Handshake(BinaryWriter writer)
    {
        writer.Write(ProtocolVersion);
        writer.Write(Environment.ProcessId);
        writer.Write(AppDomain.CurrentDomain.FriendlyName ?? "");
        writer.Write(string.Join(" ", Capabilities()));
        writer.Flush();
    }

    private static (bool Ok, string Error) ApplyOne(BinaryReader reader)
    {
        var moduleId = new Guid(reader.ReadBytes(16));
        byte[] metadata = ReadBlock(reader);
        byte[] il = ReadBlock(reader);
        byte[] pdb = ReadBlock(reader);
        int[] updatedTypes = ReadTokens(reader);

        try
        {
            var assembly = FindAssembly(moduleId);
            if (assembly is null)
                return (false, $"no loaded assembly has module id {moduleId}");

            MetadataUpdater.ApplyUpdate(assembly, metadata, il, pdb);
            NotifyUpdateHandlers(assembly, updatedTypes);
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Runs every <see cref="MetadataUpdateHandlerAttribute"/> handler after an apply.
    /// </summary>
    /// <remarks>
    /// The IL is already swapped by the time this runs, but frameworks cache what they derived
    /// from the old metadata — ASP.NET's action descriptors, serialiser contracts, compiled
    /// bindings. Without this pass the process runs the new code only where nothing cached the
    /// old shape, which for a web app reads as the edit not taking. ClearCache for every handler
    /// first, then UpdateApplication for every handler, matching the runtime's documented order.
    /// </remarks>
    private static void NotifyUpdateHandlers(Assembly updatedAssembly, int[] updatedTypeTokens)
    {
        Type[] updatedTypes;
        try
        {
            var module = updatedAssembly.ManifestModule;
            updatedTypes = updatedTypeTokens
                .Select(token =>
                {
                    try { return module.ResolveType(token); }
                    catch { return null; }
                })
                .Where(t => t is not null)
                .ToArray()!;
        }
        catch
        {
            updatedTypes = Type.EmptyTypes;
        }

        var clearCache = new List<MethodInfo>();
        var updateApplication = new List<MethodInfo>();

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic)
                continue;

            try
            {
                foreach (var attribute in assembly.GetCustomAttributes<MetadataUpdateHandlerAttribute>())
                {
                    var handler = attribute.HandlerType;
                    const BindingFlags flags =
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

                    if (handler.GetMethod("ClearCache", flags, null, [typeof(Type[])], null) is { } clear)
                        clearCache.Add(clear);
                    if (handler.GetMethod("UpdateApplication", flags, null, [typeof(Type[])], null) is { } update)
                        updateApplication.Add(update);
                }
            }
            catch
            {
                // A handler that cannot be inspected must not fail the apply that already landed.
            }
        }

        foreach (var method in clearCache.Concat(updateApplication))
        {
            try
            {
                method.Invoke(null, [updatedTypes]);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[roslyn-sense] metadata update handler {method.DeclaringType?.Name}.{method.Name} failed: {ex.Message}");
            }
        }
    }

    private static int[] ReadTokens(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count <= 0)
            return [];

        var tokens = new int[count];
        for (int i = 0; i < count; i++)
            tokens[i] = reader.ReadInt32();
        return tokens;
    }

    private static byte[] ReadBlock(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        return length <= 0 ? [] : reader.ReadBytes(length);
    }

    /// <summary>
    /// Finds the loaded assembly a delta belongs to by the module id Roslyn built it against.
    /// </summary>
    /// <remarks>
    /// Matching on the MVID rather than the name is what makes this correct when the same
    /// assembly name is loaded twice — a plugin host, or a test runner with several contexts.
    /// A delta applied to the wrong one corrupts it.
    /// </remarks>
    private static Assembly? FindAssembly(Guid moduleId)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic)
                continue;

            try
            {
                foreach (var module in assembly.Modules)
                {
                    if (module.ModuleVersionId == moduleId)
                        return assembly;
                }
            }
            catch
            {
                // A collectible context can unload while we walk it.
            }
        }

        return null;
    }

    /// <summary>
    /// Asks the runtime what kinds of edit it will accept, so Roslyn reports a rude edit at the
    /// keyboard rather than emitting a delta that fails on apply.
    /// </summary>
    private static string[] Capabilities()
    {
        try
        {
            var method = typeof(MetadataUpdater).GetMethod(
                "GetCapabilities",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                binder: null, types: Type.EmptyTypes, modifiers: null);

            // An empty string is an answer, not an absence: it is the runtime saying it accepts
            // no edits at all, and reporting the fallback instead would have Roslyn emit deltas
            // the runtime rejects one by one at apply time.
            if (method?.Invoke(null, null) is string capabilities)
                return capabilities.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }
        catch
        {
            // Internal API; a runtime that moved it falls back rather than losing hot reload.
        }

        return FallbackCapabilities;
    }
}
