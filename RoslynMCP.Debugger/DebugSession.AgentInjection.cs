using System.Runtime.InteropServices;
using ClrDebug;

namespace RoslynMCP.Debugger;

/// <summary>
/// Loading an assembly into the debuggee and starting it, using the debugger as the injector.
/// </summary>
/// <remarks>
/// <para>
/// A debugger can call any static method in the process it is attached to. That makes an attach a
/// way to get code <em>into</em> a running process, not only a way to look at one — which matters
/// for anything whose normal entry is start-time only. The hot reload agent is exactly that: the
/// runtime reads <c>DOTNET_STARTUP_HOOKS</c> once before any managed code runs, so an app that is
/// already up is past the only moment it would have been loaded, and hot reload has had to be a
/// launch option for that reason alone.
/// </para>
/// <para>
/// Nothing here is specific to that agent. It loads an assembly by path and calls a static method
/// on it, and what the assembly then does is its own business.
/// </para>
/// </remarks>
public sealed partial class DebugSession
{
    /// <summary>What to load into the debuggee and what to call once it is there.</summary>
    /// <param name="AssemblyPath">A path as the <em>debuggee</em> will see it. The same machine
    /// today, but the distinction is real the moment it is not.</param>
    /// <param name="TypeName">The type to call, as metadata spells it.</param>
    /// <param name="MethodName">A static method on it. It must return quickly — this runs inside
    /// an evaluation with a timeout — and may return a string to say why it declined.</param>
    /// <param name="Argument">The single string argument to pass, if the method takes one.</param>
    public sealed record AgentInjection(
        string AssemblyPath, string TypeName, string MethodName, string? Argument = null);

    /// <summary>
    /// Loads an assembly into the debuggee and starts it.
    /// </summary>
    /// <remarks>
    /// Requires a stop, and not just any stop: an evaluation needs a thread parked somewhere the
    /// runtime is willing to run code, which a breakpoint gives and an arbitrary interrupt often
    /// does not. Reported rather than worked around — a caller that asked at the wrong moment can
    /// ask again at the right one, whereas resuming and hoping would be a different failure with
    /// no explanation attached.
    /// </remarks>
    public Task<(bool Ok, string Detail)> InjectAgentAsync(AgentInjection injection) =>
        InvokeAsync(() => Inject(injection));

    private (bool Ok, string Detail) Inject(AgentInjection injection)
    {
        if (_process is null)
            return (false, "there is no process to inject into");
        if (_stoppedThread is null)
            return (false, "the process has to be stopped before anything can be loaded into it");

        if (CallStatic(
                "System.Reflection.Assembly", "LoadFrom", [injection.AssemblyPath],
                out var loadError) is null)
        {
            // Distinguished from the call failing: a load that fails leaves nothing behind, and
            // the path is nearly always why.
            return (false, loadError.Length > 0
                ? $"the agent could not be loaded into the process: {loadError}"
                : $"the process would not load '{injection.AssemblyPath}'");
        }

        // Looked up only now. Before the load the type does not exist in the debuggee, and the
        // module carrying it is not among the ones the session knows about.
        var arguments = injection.Argument is null ? Array.Empty<string>() : [injection.Argument];
        var result = CallStatic(injection.TypeName, injection.MethodName, arguments, out var callError);
        if (callError.Length > 0)
            return (false, $"the agent was loaded but would not start: {callError}");

        // A string back is the method declining and saying why — a runtime too old, a process
        // started in a shape the agent cannot work in. Only the agent can answer those, from
        // inside; guessing at them from out here is how a feature comes to be offered and then
        // silently do nothing.
        if (result is not null && StringFromDebuggee(result) is { Length: > 0 } refusal)
            return (false, refusal);

        return (true, string.Empty);
    }

    /// <summary>A string the debuggee returned, or null when it returned something else.</summary>
    private string? StringFromDebuggee(CorDebugValue value) =>
        Safe(() => Dereference(value)) is CorDebugStringValue text
            ? Safe(() => text.GetString(text.Length))
            : null;

    /// <summary>
    /// Calls a static method in the debuggee by name, with string arguments.
    /// </summary>
    /// <param name="error">Empty when the call ran, whatever it returned.</param>
    private CorDebugValue? CallStatic(
        string typeName, string methodName, string[] arguments, out string error)
    {
        error = string.Empty;

        if (FindStatic(typeName, methodName, arguments.Length) is not { } function)
        {
            error = $"{typeName}.{methodName} could not be found in the process";
            return null;
        }

        var values = new CorDebugValue[arguments.Length];
        for (var i = 0; i < arguments.Length; i++)
        {
            // Each argument is a string built inside the debuggee, which is itself an evaluation.
            var text = arguments[i];
            if (RunEval(eval => eval.NewString(text), out error) is not { } value)
            {
                if (error.Length == 0)
                    error = "an argument could not be created in the process";
                return null;
            }
            values[i] = value;
        }

        return InvokeFunction(function, values, out error);
    }

    /// <summary>
    /// A static method of the named type, in whichever loaded module carries it.
    /// </summary>
    /// <remarks>
    /// Every module is searched because the type may be in any of them, including one that was
    /// loaded moments ago by the injection itself. Overloads are told apart by how many arguments
    /// they take, which is the only thing that distinguishes the ones this needs and is far
    /// cheaper than resolving a full signature.
    /// </remarks>
    private CorDebugFunction? FindStatic(string typeName, string methodName, int parameterCount)
    {
        foreach (var module in LoadedModules())
        {
            var metadata = Safe(() => Extensions.GetMetaDataInterface<MetaDataImport>(module));
            if (metadata is null || TypeDefIn(module, typeName) is not { } typeDef)
                continue;

            var handle = IntPtr.Zero;
            try
            {
                var candidates = new mdMethodDef[32];
                int found = Safe(() => (int?)metadata.EnumMethodsWithName(
                    ref handle, typeDef, methodName, candidates)) ?? 0;

                for (var i = 0; i < found; i++)
                {
                    var token = candidates[i];
                    var props = Safe(() =>
                        (MetaDataImport_GetMethodPropsResult?)metadata.GetMethodProps(token));
                    if (props is not { } method || !method.pdwAttr.HasFlag(CorMethodAttr.mdStatic))
                        continue;
                    if (SignatureParameterCount(method.ppvSigBlob, method.pcbSigBlob) != parameterCount)
                        continue;

                    if (Safe(() => module.GetFunctionFromToken(token)) is { } function)
                        return function;
                }
            }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    try { metadata.CloseEnum(handle); } catch { }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// How many parameters a signature declares, given where the metadata left the blob.
    /// </summary>
    /// <remarks>
    /// The count never needs more than the first few bytes, so only those are copied out of the
    /// unmanaged buffer — the blob belongs to the metadata reader and is not ours to hold.
    /// </remarks>
    private static int SignatureParameterCount(IntPtr signature, int length)
    {
        if (signature == IntPtr.Zero || length <= 0)
            return MethodSignature.Unknown;

        // A calling convention, a generic count, and a parameter count, each at most one byte in
        // the forms that are read.
        Span<byte> head = stackalloc byte[3];
        var taken = Math.Min(head.Length, length);
        for (var i = 0; i < taken; i++)
            head[i] = Marshal.ReadByte(signature, i);

        return MethodSignature.ParameterCount(head[..taken]);
    }
}
