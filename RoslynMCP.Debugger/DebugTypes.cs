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

    /// <summary>
    /// A logpoint fired: its message, already interpolated, with no stop attached.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Output"/> because it is not the debuggee talking — the debuggee
    /// never ran a print statement — and a client that buffers logpoint output separately from
    /// console output needs to be able to tell them apart.
    /// </remarks>
    Logpoint,
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

    /// <summary>
    /// A hit-count rule in the editor's vocabulary — <c>&gt; n</c>, <c>&gt;= n</c>, <c>&lt; n</c>,
    /// <c>&lt;= n</c>, <c>= n</c>, <c>% n</c>, or a bare count meaning "on hit n and after".
    /// Empty means every hit counts.
    /// </summary>
    /// <remarks>
    /// Applied in the engine, on the runtime's own callback thread, so a hit that does not satisfy
    /// it is never a stop at all. Emulating it in the host instead costs a full suspend and a
    /// cross-process round trip per ignored hit, which in a loop is the difference between a
    /// breakpoint and a hang.
    /// </remarks>
    public string HitCondition { get; set; } = "";

    /// <summary>
    /// A logpoint message with <c>{expression}</c> placeholders. When set, the breakpoint logs and
    /// resumes instead of stopping.
    /// </summary>
    public string LogMessage { get; set; } = "";

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

    /// <summary>IL form: the module the token lives in. Matched by full path, falling back to
    /// file name for shadow-copied modules. Takes precedence over the source and entry forms
    /// when <see cref="MethodToken"/> is set.</summary>
    public string ModulePath { get; set; } = "";

    /// <summary>IL form: a MethodDef token in <see cref="ModulePath"/>; 0 when unused. Set by the
    /// host when the requested file is decompiled or fetched source — paths no PDB records — so
    /// binding cannot go through documents and goes straight to the IL instead.</summary>
    public int MethodToken { get; set; }

    /// <summary>IL form: the offset within the method's IL body; meaningful only with
    /// <see cref="MethodToken"/>.</summary>
    public int IlOffset { get; set; }
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

    /// <summary>
    /// Where the statement at the IP ends. 0 when the symbols did not say.
    /// </summary>
    /// <remarks>
    /// Carried because a frame is not only a place to show — it is also a statement that is
    /// currently executing, and telling the compiler which statements those are is what lets it
    /// refuse an edit to a method that is on a stack. A point is not enough for that; the span is.
    /// </remarks>
    public uint EndLine { get; set; }

    /// <summary>1-based; 0 when unknown.</summary>
    public uint EndColumn { get; set; }

    public int ThreadId { get; set; }

    /// <summary>The module the frame executes in, always filled when the runtime says. The host
    /// resolves external source from it for frames without symbols, and identifies the module an
    /// active statement belongs to for the ones with.</summary>
    public string ModulePath { get; set; } = "";

    /// <summary>The frame's MethodDef token in <see cref="ModulePath"/>; 0 when unknown.</summary>
    public int MethodToken { get; set; }

    /// <summary>The IP within the method's IL; -1 when unknown.</summary>
    public int IlOffset { get; set; } = -1;

    /// <summary>
    /// Whether this frame is plumbing rather than something the user wrote — a
    /// <c>DebuggerHidden</c> or <c>DebuggerNonUserCode</c> method, or a module outside the
    /// solution's own output.
    /// </summary>
    /// <remarks>
    /// Reported rather than removed. Frames are addressed by index everywhere else in the
    /// protocol, so dropping one would silently shift every variable lookup above it; the client
    /// folds them away instead.
    /// </remarks>
    public bool IsNonUserCode { get; set; }
}

/// <summary>
/// Which exceptions suspend the target.
/// </summary>
/// <remarks>
/// Decided inside the engine, on the runtime's callback thread, for the same reason hit counts
/// are: an exception the user does not want to see should never become a stop. A framework that
/// throws internally on a hot path — and several do — turns "break on all exceptions" into an
/// unusable session otherwise, which is why the type filter matters more than it looks.
/// </remarks>
public sealed class ExceptionPolicy
{
    /// <summary>
    /// Exceptions no handler was found for. Enabled by default: without a stop here the process
    /// simply dies, and the one moment worth looking at is gone.
    /// </summary>
    public ExceptionRule Unhandled { get; set; } = new() { Enabled = true };

    /// <summary>Exceptions the moment they are thrown, before any handler runs.</summary>
    public ExceptionRule Caught { get; set; } = new();

    /// <summary>The default: unhandled exceptions stop, nothing else does.</summary>
    public static ExceptionPolicy Default => new();
}

/// <summary>
/// One class of exception and the types within it that are worth stopping for.
/// </summary>
/// <remarks>
/// The type lists belong to the rule rather than to the policy because the two rules are set
/// independently. A user who limits "break on every throw" to one type is saying nothing about
/// which unhandled exceptions should stop the process — and applying that limit to both would
/// let an unhandled crash of any other type run straight past the debugger.
/// </remarks>
public sealed class ExceptionRule
{
    public bool Enabled { get; set; }

    /// <summary>When non-empty, only these types stop. Matched against the thrown type and every
    /// base type, by full name or by simple name.</summary>
    public List<string> IncludeTypes { get; set; } = [];

    /// <summary>These types never stop, even when <see cref="IncludeTypes"/> would admit them.
    /// Matched the same way, and applied after it.</summary>
    public List<string> ExcludeTypes { get; set; } = [];
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

    /// <summary>
    /// One word for what happened when symbols were looked for: <see cref="SymbolStatuses"/>.
    /// </summary>
    /// <remarks>
    /// A bare "no symbols" is the same answer for four situations with four different fixes —
    /// the module was deliberately excluded, nothing was found, something was found and refused,
    /// or nobody has looked yet. Telling them apart is the first question when a breakpoint
    /// misbehaves, and it is the one thing the debugger knows and the user cannot.
    /// </remarks>
    public string SymbolStatus { get; set; } = "";

    /// <summary>Which kind of symbols answered, when they did: see <see cref="SymbolOrigins"/>.</summary>
    public string SymbolOrigin { get; set; } = "";

    /// <summary>What was observed, in a sentence — the reason behind <see cref="SymbolStatus"/>.
    /// Empty when the status says everything there is to say.</summary>
    public string SymbolDetail { get; set; } = "";
}

/// <summary>The vocabulary of <see cref="DebugModule.SymbolStatus"/>.</summary>
public static class SymbolStatuses
{
    /// <summary>Symbols are open and breakpoints in this module can bind.</summary>
    public const string Loaded = "loaded";

    /// <summary>The symbol globs exclude this module, so no PDB was looked for.</summary>
    public const string Excluded = "excluded";

    /// <summary>Looked for and not found — no debug directory entry, no embedded PDB, no file.</summary>
    public const string NotFound = "not found";

    /// <summary>A PDB was found and could not be used for this module.</summary>
    public const string Rejected = "rejected";

    /// <summary>Nothing has needed this module's symbols yet, so none were opened.</summary>
    public const string NotProbed = "not probed";
}

/// <summary>The vocabulary of <see cref="DebugModule.SymbolOrigin"/>.</summary>
public static class SymbolOrigins
{
    public const string PortablePdb = "portable pdb";
    public const string EmbeddedPdb = "embedded pdb";
    public const string WindowsPdb = "windows pdb";

    /// <summary>Symbols the runtime handed over for a module built at run time.</summary>
    public const string Runtime = "supplied at run time";
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
