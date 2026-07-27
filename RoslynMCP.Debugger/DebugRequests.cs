namespace RoslynMCP.Debugger;

/// <summary>What a breakpoint is attached to.</summary>
public enum BreakpointKind
{
    Unspecified,
    Source,
    Function,
    Exception,
    Data,
}

/// <summary>
/// Where a breakpoint was asked for and where it actually bound. The two differ whenever the
/// requested line carries no sequence point and the nearest following one is used instead.
/// </summary>
public sealed class BreakpointLocation
{
    public string Id { get; set; } = "";
    public SourceRange? Requested { get; set; }
    public SourceRange? Actual { get; set; }

    /// <summary>Whether the breakpoint bound to real code. An unverified one stays pending.</summary>
    public bool Verified { get; set; }

    public string Message { get; set; } = "";
    public string Label { get; set; } = "";
    public BreakpointKind Kind { get; set; }
}

/// <summary>Asks which lines in a span can actually hold a breakpoint.</summary>
public sealed class BreakpointLocationsRequest
{
    public string FilePath { get; set; } = "";
    public uint Line { get; set; }
    public uint Column { get; set; }
    public uint EndLine { get; set; }
    public uint EndColumn { get; set; }
}

public sealed class BreakpointLocationsResponse
{
    public List<BreakpointLocation> Locations { get; set; } = [];
}

/// <summary>Runs the debuggee until it reaches a location, via a temporary breakpoint.</summary>
public sealed class RunToLocationRequest
{
    public SourceRange? Location { get; set; }
    public bool Force { get; set; }
}

public sealed class RunToLocationResponse
{
    public bool Ok { get; set; }
    public BreakpointLocation? Location { get; set; }
    public string Error { get; set; } = "";
}

/// <summary>Moves the instruction pointer within the current frame.</summary>
public sealed class SetNextStatementRequest
{
    public uint FrameIndex { get; set; }
    public SourceRange? Location { get; set; }
}

public sealed class SetNextStatementResponse
{
    public bool Ok { get; set; }
    public SourceRange? Actual { get; set; }
    public string Error { get; set; } = "";
}
