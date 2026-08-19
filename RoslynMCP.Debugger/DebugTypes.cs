namespace RoslynMCP.Debugger;

/// <summary>Which runtime the debuggee hosts, selecting the ICorDebug bootstrap.</summary>
public enum DebugRuntime
{
    /// <summary>The caller did not say; the engine infers it from the target.</summary>
    Unspecified,

    NetFramework,
    CoreClr,
}

public enum StepKind
{
    Over,
    Into,
    Out,
}

/// <summary>Why the debuggee raised an event.</summary>
public enum DebugEventKind
{
    Unspecified,
    Created,
    Breakpoint,
    Exception,
    Output,
    Exited,

    /// <summary>A module loaded. Diagnostic only — not a stop. Makes shadow-copied and generated
    /// <c>App_Web_*</c> assemblies visible.</summary>
    Module,

    /// <summary>A pending source breakpoint bound to real code.</summary>
    BreakpointBound,

    /// <summary>A source-level step completed — a distinct stop reason from a breakpoint hit.</summary>
    Step,

    /// <summary>The debuggee was suspended by an explicit pause.</summary>
    Paused,

    /// <summary>A bound breakpoint lost its module (unload or app-domain recycle) and is pending
    /// again until the module reloads.</summary>
    BreakpointUnbound,

    /// <summary>The engine talking about itself — symbols it could not find, a breakpoint it could
    /// not bind, an edit the runtime refused. Split from <see cref="Output"/>, which is the
    /// debuggee's own console, so a client can route the two to different places.</summary>
    Diagnostic,
}

/// <summary>A span of source, used to report where a breakpoint was requested versus where it bound.</summary>
public sealed class SourceRange
{
    public string FilePath { get; set; } = "";
    public uint Line { get; set; }
    public uint Column { get; set; }
    public uint EndLine { get; set; }
    public uint EndColumn { get; set; }

    /// <summary>
    /// A detached copy. Ranges are handed to callers and stored on events, so they must not alias
    /// a range the engine may still mutate.
    /// </summary>
    public SourceRange Clone() => new()
    {
        FilePath = FilePath,
        Line = Line,
        Column = Column,
        EndLine = EndLine,
        EndColumn = EndColumn,
    };
}

/// <summary>Something that happened in the debuggee.</summary>
public sealed class DebugEvent
{
    public DebugEventKind Kind { get; set; }
    public string Message { get; set; } = "";
    public string MethodName { get; set; } = "";
    public int ThreadId { get; set; }

    /// <summary>The stop location for breakpoint, step and pause events, when symbols resolve it.</summary>
    public string FilePath { get; set; } = "";

    /// <summary>1-based; 0 when unknown.</summary>
    public uint Line { get; set; }

    /// <summary>1-based; 0 when unknown.</summary>
    public uint Column { get; set; }

    public SourceRange? RequestedLocation { get; set; }
    public SourceRange? ActualLocation { get; set; }
    public string BreakpointId { get; set; } = "";

    /// <summary>
    /// The debuggee's PID, once there is one. Carried on every event because a worker-hosted
    /// session runs in another process: this channel is the only way the PID reaches the host,
    /// and the host needs it to report the process to its own client.
    /// </summary>
    public int ProcessId { get; set; }
}

/// <summary>A breakpoint request, which may bind now or when a matching module loads.</summary>
public sealed class BreakpointSpec
{
    /// <summary>Method-entry form: optional type plus a required method name.</summary>
    public string TypeName { get; set; } = "";
    public string MethodName { get; set; } = "";

    /// <summary>Source-line form. Takes precedence when set; binds through PDB sequence points as
    /// modules load, including delayed and shadow-copied assemblies.</summary>
    public string FilePath { get; set; } = "";

    /// <summary>1-based.</summary>
    public uint Line { get; set; }

    /// <summary>Stop only on hit number <c>SkipHits + 1</c> and later.</summary>
    public uint SkipHits { get; set; }

    /// <summary>Optional <c>name == literal</c> / <c>name != literal</c> compared against the
    /// stringified value of an argument, local, or dotted member path. Empty means always stop.</summary>
    public string Condition { get; set; } = "";

    public string Id { get; set; } = "";
    public uint Column { get; set; }
    public uint EndLine { get; set; }
    public uint EndColumn { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Removed automatically once hit — used to implement "run to location".</summary>
    public bool Temporary { get; set; }

    public BreakpointKind Kind { get; set; }
}

public sealed class StackFrame
{
    public uint Index { get; set; }

    /// <summary>Formatted as <c>Type.Method</c>.</summary>
    public string Method { get; set; } = "";

    /// <summary>Empty for frames without symbols.</summary>
    public string FilePath { get; set; } = "";

    /// <summary>1-based; 0 when unknown.</summary>
    public uint Line { get; set; }

    /// <summary>1-based; 0 when unknown.</summary>
    public uint Column { get; set; }

    public int ThreadId { get; set; }
}

public sealed class DebugThread
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool Stopped { get; set; }
    public string Location { get; set; } = "";
}

public sealed class DebugModule
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";

    /// <summary>Whether a PDB was found. Without symbols, breakpoints in this module never bind.</summary>
    public bool SymbolsLoaded { get; set; }

    public string SymbolPath { get; set; } = "";
    public string Runtime { get; set; } = "";
}

public sealed class DebugVariable
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";

    /// <summary><c>arg</c>, <c>local</c>, <c>field</c>, <c>element</c>, <c>proxy</c> (a member of a
    /// <c>DebuggerTypeProxy</c> view), <c>raw</c> (the Raw View node), or <c>diagnostic</c>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>The declared type name, empty when it could not be read.</summary>
    public string Type { get; set; } = "";

    /// <summary>
    /// The expression this value is reachable by, set only when it has children worth expanding.
    /// Pass it back to <c>ExpandAsync</c>.
    /// </summary>
    /// <remarks>
    /// A path rather than a handle, so it stays valid across the process boundary to a
    /// bitness-matched worker and can be handed to Evaluate or SetVariable unchanged. Two segments
    /// are not members: <c>$proxy</c> steps through the type's debugger view and <c>$raw</c>
    /// suppresses that view for one level.
    /// </remarks>
    public string VariablesReference { get; set; } = "";

    public bool Settable { get; set; }
}

public sealed class DebugScope
{
    public string Name { get; set; } = "";
    public string VariablesReference { get; set; } = "";

    /// <summary>Whether enumerating this scope is costly enough to defer until asked.</summary>
    public bool Expensive { get; set; }
}
