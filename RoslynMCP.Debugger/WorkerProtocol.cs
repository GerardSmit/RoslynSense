using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynMCP.Debugger;

/// <summary>
/// The line-delimited JSON protocol between the host and a bitness-matched debug worker.
/// </summary>
/// <remarks>
/// One JSON object per line in each direction. Requests carry an id and are answered by a response
/// with the same id; debug events are unsolicited and carry no id. JSON rather than a binary
/// format because the traffic is low-volume and human-readable framing makes worker failures
/// diagnosable from a log.
/// </remarks>
public static class WorkerProtocol
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
}

public sealed class WorkerRequest
{
    public int Id { get; set; }

    /// <summary>One of: attach, launch, addBreakpoint, removeBreakpoint, continue, pause, step,
    /// stackTrace, threads, variables, expand, displayOptions, evaluate, setVariable, applyDelta,
    /// runToLocation, setNextStatement, modules, detach, exceptionPolicy, shutdown,
    /// terminate.</summary>
    public string Op { get; set; } = "";

    public int Pid { get; set; }

    /// <summary>Which thread 'stackTrace' should walk; 0 means the stopped one.</summary>
    public int ThreadId { get; set; }
    public DebugRuntime Runtime { get; set; }
    public List<BreakpointSpec>? Breakpoints { get; set; }
    public BreakpointSpec? Breakpoint { get; set; }
    public string? FilePath { get; set; }
    public int Line { get; set; }
    public StepKind Step { get; set; }
    public uint FrameIndex { get; set; }
    public string? Expression { get; set; }

    /// <summary>Which debugger attributes the worker's engine should honour, sent by
    /// 'displayOptions'.</summary>
    public DebugDisplayOptions? DisplayOptions { get; set; }

    /// <summary>The value to expand, as the path its <c>VariablesReference</c> carried.</summary>
    public string? Path { get; set; }
    public string? Name { get; set; }
    public string? Value { get; set; }
    public string? Executable { get; set; }
    public List<string>? Arguments { get; set; }
    public Dictionary<string, string>? Environment { get; set; }
    public string? WorkingDirectory { get; set; }

    /// <summary>Hot reload deltas. Base64 in transit because the protocol is line-delimited JSON;
    /// they are small (one method's IL), so the encoding cost does not matter.</summary>
    public string? MetadataDelta { get; set; }
    public string? IlDelta { get; set; }
    public string? PdbDelta { get; set; }

    /// <summary>A serialized <see cref="EncSymbolMap"/> beside the PDB delta: the line movements
    /// the delta itself cannot express. Plain JSON rather than base64 — it is text already.</summary>
    public string? SymbolMap { get; set; }

    /// <summary>The exception stop policy, sent by 'exceptionPolicy'.</summary>
    public ExceptionPolicy? ExceptionPolicy { get; set; }

    public bool Flag { get; set; }
    public bool Force { get; set; }

    /// <summary>IL form of 'runToLocation' and 'setNextStatement', for locations in decompiled
    /// or fetched source that no PDB document names. 0 when unused.</summary>
    public string? ModulePath { get; set; }
    public int MethodToken { get; set; }
    public int IlOffset { get; set; }

    /// <summary>How long a graceful shutdown may take before the debuggee is terminated.</summary>
    public double TimeoutSeconds { get; set; }
}

public sealed class WorkerResponse
{
    public int Id { get; set; }
    public bool Ok { get; set; }
    public string Error { get; set; } = "";

    /// <summary>Set when the request produced a debug event rather than a reply.</summary>
    public DebugEvent? Event { get; set; }

    public List<StackFrame>? Frames { get; set; }
    public List<DebugThread>? Threads { get; set; }
    public List<DebugVariable>? Variables { get; set; }
    public DebugVariable? Variable { get; set; }
    public string? Value { get; set; }
    public bool Removed { get; set; }

    /// <summary>Set by 'shutdown': whether the debuggee exited on its own.</summary>
    public bool Graceful { get; set; }
    public List<DebugModule>? Modules { get; set; }
    public RunToLocationResponse? RunToLocation { get; set; }
    public SetNextStatementResponse? SetNextStatement { get; set; }
}
