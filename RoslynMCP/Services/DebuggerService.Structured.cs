using System.Text;
using System.Text.RegularExpressions;
using RoslynMCP.Services.Debugging;

namespace RoslynMCP.Services;

/// <summary>
/// The structured half of the netcoredbg backend: the same MI responses the markdown surface
/// formats, returned as records so the editor's Call Stack and Variables views get real paths,
/// types, and expandable objects.
/// </summary>
internal sealed partial class DebuggerService
{
    private readonly VariableHandles _handles = new();

    /// <summary>MI evaluates in whichever frame is selected, so frame selection is session state
    /// rather than a per-command argument.</summary>
    private int _selectedFrame;

    /// <summary>Every listed variable costs a <c>-var-create</c>/<c>-var-delete</c> pair to learn
    /// whether it can be expanded; a runaway frame must not stall the Variables view.</summary>
    private const int MaxProbedVariables = 100;

    public async Task<IReadOnlyList<StackFrameInfo>> GetStackFramesAsync(
        CancellationToken cancellationToken = default)
    {
        if (_state != DebugState.Stopped)
            return [];

        return ParseStackFrames(await SendCommandAsync("-stack-list-frames", cancellationToken));
    }

    public async Task<IReadOnlyList<VariableInfo>> GetVariablesAsync(
        int frameId, CancellationToken cancellationToken = default)
    {
        if (_state != DebugState.Stopped)
            return [];

        await EnsureFrameAsync(frameId, cancellationToken);

        var response = await SendCommandAsync($"-stack-list-variables --frame {frameId} 1", cancellationToken);
        if (IsError(response))
        {
            // Older netcoredbg builds reject --frame; the selection above already put us in the
            // right frame, so the plain form returns the same variables.
            response = await SendCommandAsync("-stack-list-variables 1", cancellationToken);
            if (IsError(response))
                return [];
        }

        var variables = new List<VariableInfo>();
        foreach (var (name, value) in ParseNameValueList(response))
        {
            variables.Add(await DescribeAsync(name, name, value, frameId, variables.Count, cancellationToken));
        }
        return variables;
    }

    public async Task<IReadOnlyList<VariableInfo>> GetVariableChildrenAsync(
        int variablesReference, CancellationToken cancellationToken = default)
    {
        if (_state != DebugState.Stopped)
            return [];
        if (_handles.Expression(variablesReference) is not { } handle)
            return [];

        var (frameId, expression) = DecodeHandle(handle);
        await EnsureFrameAsync(frameId, cancellationToken);

        string variable = NextVariableName();
        try
        {
            if (IsError(await SendCommandAsync(
                    $"-var-create {variable} * \"{EscapeMiString(expression)}\"", cancellationToken)))
            {
                return [];
            }

            var response = await SendCommandAsync(
                $"-var-list-children --all-values {variable}", cancellationToken);
            if (IsError(response))
                return [];

            var children = new List<VariableInfo>();
            int listStart = response.IndexOf("children=[", StringComparison.Ordinal);
            if (listStart < 0)
                return children;

            foreach (string tuple in SplitMiTuples(response[(listStart + "children=[".Length)..]))
            {
                string display = ExtractMiField(tuple, "exp") ?? ExtractMiField(tuple, "name") ?? "?";
                string childPath = Compose(expression, display);
                string value = ExtractMiField(tuple, "value") ?? "";
                string type = ExtractMiField(tuple, "type") ?? "";
                int.TryParse(ExtractMiField(tuple, "numchild"), out int numchild);

                children.Add(BuildVariable(
                    display, value, type, numchild, childPath, frameId, children.Count));
            }
            return children;
        }
        finally
        {
            try { await SendCommandAsync($"-var-delete {variable}", CancellationToken.None); }
            catch { /* best effort cleanup */ }
        }
    }

    public async Task<(bool Ok, string Value, string Error)> SetVariableAsync(
        string name, string value, int frameId = 0, CancellationToken cancellationToken = default)
    {
        if (_state != DebugState.Stopped)
            return (false, "", "The debugger is not stopped.");

        await EnsureFrameAsync(frameId, cancellationToken);

        string variable = NextVariableName();
        try
        {
            var created = await SendCommandAsync(
                $"-var-create {variable} * \"{EscapeMiString(name)}\"", cancellationToken);
            if (IsError(created))
                return (false, "", ExtractError(created));

            var assigned = await SendCommandAsync(
                $"-var-assign {variable} \"{EscapeMiString(value)}\"", cancellationToken);
            if (IsError(assigned))
                return (false, "", ExtractError(assigned));

            // The target reports what it actually stored, which narrowing and property setters
            // can make different from what was written.
            string stored = ExtractMiField(assigned, "value") ?? value;
            return (true, stored, "");
        }
        finally
        {
            try { await SendCommandAsync($"-var-delete {variable}", CancellationToken.None); }
            catch { /* best effort cleanup */ }
        }
    }

    public async Task<IReadOnlyList<ThreadInfo>> GetThreadsAsync(
        CancellationToken cancellationToken = default)
    {
        if (_state is DebugState.NotStarted or DebugState.Exited)
            return [];

        var response = await SendCommandAsync("-thread-info", cancellationToken);
        if (IsError(response))
            return [];

        int listStart = response.IndexOf("threads=[", StringComparison.Ordinal);
        if (listStart < 0)
            return [];

        var threads = new List<ThreadInfo>();
        foreach (string tuple in SplitMiTuples(response[(listStart + "threads=[".Length)..]))
        {
            if (!int.TryParse(ExtractMiField(tuple, "id"), out int id))
                continue;

            string name = ExtractMiField(tuple, "name")
                ?? ExtractMiField(tuple, "target-id")
                ?? $"Thread {id}";

            threads.Add(new ThreadInfo(id, name, ExtractMiField(tuple, "state") ?? "unknown"));
        }
        return threads;
    }

    public Task<ExceptionDetail?> GetExceptionInfoAsync(CancellationToken cancellationToken = default)
    {
        StoppedFrame? frame;
        lock (_outputLock) frame = _currentFrame;

        if (frame?.ExceptionName is not { Length: > 0 } typeName)
            return Task.FromResult<ExceptionDetail?>(null);

        // netcoredbg reports the stage as "throw" for first-chance and "unhandled" otherwise;
        // DAP wants its own vocabulary.
        string breakMode = frame.ExceptionStage switch
        {
            "unhandled" => "unhandled",
            "user-unhandled" => "userUnhandled",
            _ => "always",
        };

        return Task.FromResult<ExceptionDetail?>(new ExceptionDetail(
            typeName, frame.ExceptionMessage ?? "", StackTrace: null, breakMode));
    }

    public async Task<string> SetExceptionFiltersAsync(
        ExceptionFilters filters, CancellationToken cancellationToken = default)
    {
        if (_netcoredbgProcess is null or { HasExited: true })
            return "Error: no debug session is active.";

        // MI has no filter list of its own; netcoredbg models each filter as a catchpoint.
        await SendCommandAsync("-break-exception-delete *", cancellationToken);

        var enabled = new List<string>();
        if (filters.All)
        {
            enabled.Add("all");
            await SendCommandAsync("-break-exception-insert throw *", cancellationToken);
        }
        if (filters.UserUnhandled)
        {
            enabled.Add("user-unhandled");
            await SendCommandAsync("-break-exception-insert user-unhandled *", cancellationToken);
        }

        return enabled.Count == 0
            ? "Exception breakpoints cleared."
            : $"Breaking on: {string.Join(", ", enabled)}.";
    }

    /// <summary>
    /// Run to Cursor, as a one-shot breakpoint plus a continue.
    /// </summary>
    /// <remarks>
    /// MI's own <c>-exec-until</c> only moves within the current frame, which is not what the
    /// command means to a user pointing at a line elsewhere. A temporary breakpoint is what the
    /// gesture actually is, and netcoredbg deletes it on hit.
    /// </remarks>
    public async Task<string> RunToLocationAsync(
        string filePath, int line, CancellationToken cancellationToken = default)
    {
        if (_netcoredbgProcess is null or { HasExited: true })
            return "Error: no debug session is active.";

        string response = await SendCommandAsync(
            $"-break-insert -t \"{Path.GetFullPath(filePath).Replace("\\", "/")}:{line}\"",
            cancellationToken);

        if (IsError(response))
            return $"Error: {ExtractMiField(response, "msg")}";

        return await ContinueAsync(cancellationToken);
    }

    /// <summary>
    /// Not available on CoreCLR through netcoredbg.
    /// </summary>
    /// <remarks>
    /// MI has no set-next-statement, and netcoredbg exposes none of ICorDebug's
    /// <c>SetIP</c> equivalent. Saying so is better than a command that appears to work: moving
    /// the instruction pointer is the one operation whose silent failure changes what the program
    /// does next.
    /// </remarks>
    public Task<string> SetNextStatementAsync(
        string filePath, int line, CancellationToken cancellationToken = default) =>
        Task.FromResult(
            "Moving the next statement is not supported on CoreCLR: netcoredbg exposes no way to " +
            "set the instruction pointer. It is available when debugging .NET Framework.");

    public async Task<IReadOnlyList<ModuleInfo>> GetModulesAsync(
        CancellationToken cancellationToken = default)
    {
        if (_netcoredbgProcess is null or { HasExited: true })
            return [];

        var response = await SendCommandAsync("-file-list-shared-libraries", cancellationToken);
        if (IsError(response))
            return [];

        var modules = new List<ModuleInfo>();
        foreach (string entry in SplitMiTuples(response))
        {
            // An absent field reads as null, not as an empty string — a module with no symbols
            // simply has no symbols-path.
            if (ExtractMiField(entry, "target-name") is not { Length: > 0 } path)
                continue;

            // netcoredbg reports symbol state as "Yes"/"No" in symbols-loaded.
            bool symbols = ExtractMiField(entry, "symbols-loaded")
                ?.StartsWith("y", StringComparison.OrdinalIgnoreCase) ?? false;

            modules.Add(new ModuleInfo(
                Path.GetFileName(path), path, symbols,
                ExtractMiField(entry, "symbols-path") ?? "", "CoreCLR"));
        }

        return modules;
    }

    public async Task<string> DetachAsync(CancellationToken cancellationToken = default)
    {
        if (_netcoredbgProcess is null or { HasExited: true })
            return "Error: no debug session is active.";

        var response = await SendCommandAsync("-target-detach", cancellationToken);
        if (IsError(response))
            return $"Error: {ExtractMiField(response, "msg")}";

        _state = DebugState.NotStarted;
        return "Detached. The process is still running.";
    }

    public async Task<string> InterruptAsync(CancellationToken cancellationToken = default)
    {
        if (_state == DebugState.Stopped)
            return "The debugger is already stopped.";
        if (_state is DebugState.NotStarted or DebugState.Exited)
            return "Error: no debug session is running.";

        var response = await SendCommandAsync("-exec-interrupt", cancellationToken);
        if (IsError(response))
            return $"Error: {ExtractError(response)}";

        return await WaitForStopAsync(cancellationToken);
    }

    public async Task<string> SelectFrameAsync(int frameId, CancellationToken cancellationToken = default)
    {
        if (_state != DebugState.Stopped)
            return "Error: the debugger is not stopped.";
        if (frameId < 0)
            return "Error: frame numbers start at 0 (the innermost frame).";

        if (frameId == _selectedFrame)
            return $"Frame #{frameId} is already selected.";

        var response = await SendCommandAsync($"-stack-select-frame {frameId}", cancellationToken);
        if (IsError(response))
            return $"Error: {ExtractError(response)}";

        _selectedFrame = frameId;
        var frames = await GetStackFramesAsync(cancellationToken);
        var frame = frames.FirstOrDefault(f => f.Id == frameId);

        return frame is null
            ? $"Selected frame #{frameId}."
            : $"Selected frame #{frameId}: {frame.Name}" +
              (frame.FilePath.Length == 0 ? "" : $" at {Path.GetFileName(frame.FilePath)}:{frame.Line}");
    }

    // --- MI plumbing ---

    /// <summary>Selection without the reporting, for the structured reads that take a frame
    /// argument rather than relying on what the user selected.</summary>
    private async Task EnsureFrameAsync(int frameId, CancellationToken cancellationToken)
    {
        if (frameId == _selectedFrame)
            return;

        if (!IsError(await SendCommandAsync($"-stack-select-frame {frameId}", cancellationToken)))
            _selectedFrame = frameId;
    }

    private string NextVariableName() => $"v{Interlocked.Increment(ref _tokenCounter)}";

    /// <summary>Learns a listed variable's type and child count, which is the only way to know
    /// whether the Variables view should offer an expand arrow.</summary>
    private async Task<VariableInfo> DescribeAsync(
        string display, string expression, string value, int frameId, int index,
        CancellationToken cancellationToken)
    {
        if (index >= MaxProbedVariables)
            return BuildVariable(display, value, "", numchild: 0, expression, frameId, index);

        string variable = NextVariableName();
        try
        {
            var created = await SendCommandAsync(
                $"-var-create {variable} * \"{EscapeMiString(expression)}\"", cancellationToken);
            if (IsError(created))
                return BuildVariable(display, value, "", numchild: 0, expression, frameId, index);

            int.TryParse(ExtractMiField(created, "numchild"), out int numchild);
            return BuildVariable(
                display,
                value.Length > 0 ? value : ExtractMiField(created, "value") ?? "",
                ExtractMiField(created, "type") ?? "",
                numchild,
                expression,
                frameId,
                index);
        }
        finally
        {
            try { await SendCommandAsync($"-var-delete {variable}", CancellationToken.None); }
            catch { /* best effort cleanup */ }
        }
    }

    private VariableInfo BuildVariable(
        string display, string value, string type, int numchild, string expression, int frameId, int index)
    {
        // DAP splits children into named and indexed so a large collection can be paged; MI only
        // says how many there are, and an indexer-shaped child name is the one signal available.
        bool indexed = numchild > 0 && display.StartsWith('[');

        return new VariableInfo(
            Name: display,
            Value: value,
            Type: type,
            VariablesReference: numchild > 0 ? _handles.For(EncodeHandle(frameId, expression)) : 0,
            NamedChildCount: indexed ? 0 : numchild,
            IndexedChildCount: indexed ? numchild : 0,
            Evaluable: expression.Length > 0);
    }

    private static string EncodeHandle(int frameId, string expression) => $"{frameId}|{expression}";

    private static (int FrameId, string Expression) DecodeHandle(string handle)
    {
        int separator = handle.IndexOf('|');
        return separator < 0
            ? (0, handle)
            : (int.TryParse(handle[..separator], out int frame) ? frame : 0, handle[(separator + 1)..]);
    }

    /// <summary>Appends a child to its parent's path: <c>[0]</c> indexes, everything else is a
    /// member.</summary>
    private static string Compose(string parent, string child) =>
        child.StartsWith('[') ? parent + child : $"{parent}.{child}";

    private static bool IsError(string response) =>
        response.Contains("^error", StringComparison.Ordinal);

    internal static IReadOnlyList<StackFrameInfo> ParseStackFrames(string response)
    {
        if (IsError(response))
            return [];

        var frames = new List<StackFrameInfo>();
        foreach (Match match in StackFrameRegex().Matches(response))
        {
            string content = match.Groups[1].Value;
            string function = ExtractMiField(content, "func") ?? "";
            string file = ExtractMiField(content, "fullname") ?? ExtractMiField(content, "file") ?? "";

            int.TryParse(ExtractMiField(content, "level"), out int level);
            int.TryParse(ExtractMiField(content, "line"), out int line);
            int.TryParse(ExtractMiField(content, "col"), out int column);

            // A frame with neither a name nor a file says nothing a reader can act on, and
            // netcoredbg labels runtime transitions explicitly.
            bool external = function is "[Native Frames]" ||
                            (function.Length == 0 && file.Length == 0);

            frames.Add(new StackFrameInfo(
                level, function.Length == 0 ? "unknown" : function, file, line, column, external));
        }
        return frames;
    }

    /// <summary>The <c>name</c>/<c>value</c> pairs of a <c>variables=[]</c> or <c>locals=[]</c>
    /// response, in listed order.</summary>
    internal static List<(string Name, string Value)> ParseNameValueList(string response)
    {
        var result = new List<(string, string)>();
        if (!TryFindVariableList(response, out int start))
            return result;

        foreach (string tuple in SplitMiTuples(response[start..]))
        {
            string name = ExtractMiField(tuple, "name") ?? "";
            if (name.Length == 0)
                continue;
            result.Add((name, ExtractMiField(tuple, "value") ?? ""));
        }
        return result;
    }

    private static bool TryFindVariableList(string response, out int start)
    {
        foreach (string marker in (string[])["variables=[", "locals=["])
        {
            int index = response.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0)
            {
                start = index + marker.Length;
                return true;
            }
        }
        start = -1;
        return false;
    }

    /// <summary>
    /// Splits the top-level <c>{...}</c> tuples of an MI list, stopping at its closing bracket.
    /// </summary>
    /// <remarks>
    /// Brace counting alone is not enough: a value like <c>value="{ Count = 2 }"</c> carries
    /// braces inside a string, and a Windows path carries backslash escapes, so quoting has to
    /// be tracked as well.
    /// </remarks>
    internal static List<string> SplitMiTuples(string text)
    {
        var tuples = new List<string>();
        var current = new StringBuilder();
        int depth = 0;
        bool inString = false, escaped = false;

        foreach (char ch in text)
        {
            if (inString)
            {
                if (escaped) escaped = false;
                else if (ch == '\\') escaped = true;
                else if (ch == '"') inString = false;

                if (depth > 0) current.Append(ch);
                continue;
            }

            switch (ch)
            {
                case '"':
                    inString = true;
                    if (depth > 0) current.Append(ch);
                    continue;
                case '{':
                    depth++;
                    if (depth == 1) { current.Clear(); continue; }
                    break;
                case '}':
                    depth--;
                    if (depth == 0) { tuples.Add(current.ToString()); continue; }
                    break;
                case ']' when depth == 0:
                    return tuples;
            }

            if (depth > 0)
                current.Append(ch);
        }

        return tuples;
    }
}
