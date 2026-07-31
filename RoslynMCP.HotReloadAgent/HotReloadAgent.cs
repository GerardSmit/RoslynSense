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

    public const int ProtocolVersion = 1;

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

        try
        {
            var assembly = FindAssembly(moduleId);
            if (assembly is null)
                return (false, $"no loaded assembly has module id {moduleId}");

            MetadataUpdater.ApplyUpdate(assembly, metadata, il, pdb);
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
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
            var method = typeof(System.Reflection.Metadata.AssemblyExtensions).GetMethod(
                "GetMetadataUpdateCapabilities",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                binder: null, types: Type.EmptyTypes, modifiers: null);

            if (method?.Invoke(null, null) is string { Length: > 0 } capabilities)
                return capabilities.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }
        catch
        {
            // Internal API; a runtime that moved it falls back rather than losing hot reload.
        }

        return FallbackCapabilities;
    }
}
