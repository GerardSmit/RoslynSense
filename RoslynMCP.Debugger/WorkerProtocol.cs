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

    /// <summary>One of: attach, addBreakpoint, removeBreakpoint, continue, step, stackTrace,
    /// variables, evaluate, setVariable, terminate.</summary>
    public string Op { get; set; } = "";

    public int Pid { get; set; }
    public DebugRuntime Runtime { get; set; }
    public List<BreakpointSpec>? Breakpoints { get; set; }
    public BreakpointSpec? Breakpoint { get; set; }
    public string? FilePath { get; set; }
    public int Line { get; set; }
    public StepKind Step { get; set; }
    public uint FrameIndex { get; set; }
    public string? Expression { get; set; }
    public string? Name { get; set; }
    public string? Value { get; set; }
}

public sealed class WorkerResponse
{
    public int Id { get; set; }
    public bool Ok { get; set; }
    public string Error { get; set; } = "";

    /// <summary>Set when the request produced a debug event rather than a reply.</summary>
    public DebugEvent? Event { get; set; }

    public List<StackFrame>? Frames { get; set; }
    public List<DebugVariable>? Variables { get; set; }
    public DebugVariable? Variable { get; set; }
    public string? Value { get; set; }
    public bool Removed { get; set; }
}
