using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using RoslynMCP.Services;
using RoslynMCP.Services.Debugging;

namespace RoslynMCP.Tools;

/// <summary>
/// The parts of a debug session an editor user reaches by clicking — suspending a runaway
/// target, walking up the stack, expanding an object, and writing a value back — exposed so the
/// AI can do the same instead of restarting the session with different breakpoints.
/// </summary>
[McpServerToolType]
[InProcessOnly]
public static class DebugVariablesTool
{
    [McpServerTool, Description(
        "Suspend a running debug session, as the debugger's pause button does. Use this when " +
        "execution is not reaching a breakpoint (an infinite loop, a deadlock, a long wait) and " +
        "you need to see where it actually is.")]
    public static async Task<string> DebugPause(
        IOutputFormatter fmt,
        CancellationToken cancellationToken = default)
    {
        var session = DebugSessionManager.GetSession();
        if (session is null)
        {
            var routed = await EditorDebugRouter.TryRouteAsync("pause", ct: cancellationToken);
            return routed ?? "Error: No active debug session.";
        }

        try
        {
            var sb = new StringBuilder(await session.InterruptAsync(cancellationToken));
            fmt.AppendHints(sb,
                "Use DebugStatus to see locals and the call stack",
                "Use DebugContinue to resume");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description(
        "Select which stack frame DebugEvaluate and the locals view read from. Frame 0 is where " +
        "execution stopped; higher numbers are its callers. Use this to inspect a caller's " +
        "state, then DebugStatus or DebugEvaluate to read it.")]
    public static async Task<string> DebugSelectFrame(
        [Description("Frame number from the call stack; 0 is the innermost frame.")]
        int frame,
        IOutputFormatter fmt,
        CancellationToken cancellationToken = default)
    {
        var session = DebugSessionManager.GetSession();
        if (session is null)
            return "Error: No active debug session.";

        try
        {
            var sb = new StringBuilder(await session.SelectFrameAsync(frame, cancellationToken));
            fmt.AppendHints(sb, "Use DebugEvaluate to read a variable in this frame");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description(
        "List the variables of a stack frame, or expand one of them. Call without a reference " +
        "for the frame's locals and arguments; pass the reference of an expandable entry to see " +
        "its fields or elements. Values that can be expanded are marked.")]
    public static async Task<string> DebugExpand(
        IOutputFormatter fmt,
        [Description("Reference of the value to expand, from an earlier call. Omit for the frame's own variables.")]
        int variablesReference = 0,
        [Description("Frame to list; 0 is the innermost. Ignored when a reference is given.")]
        int frame = 0,
        CancellationToken cancellationToken = default)
    {
        var session = DebugSessionManager.GetSession();
        if (session is null)
            return "Error: No active debug session.";

        try
        {
            var variables = variablesReference > 0
                ? await session.GetVariableChildrenAsync(variablesReference, cancellationToken)
                : await session.GetVariablesAsync(frame, cancellationToken);

            if (variables.Count == 0)
            {
                return variablesReference > 0
                    ? "That value has no children, or its reference is from an earlier stop and " +
                      "no longer resolves. Read the variables again to get a current reference."
                    : "No variables in scope.";
            }

            var sb = new StringBuilder();
            fmt.BeginTable(sb, variablesReference > 0 ? "Children" : "Variables",
                ["Name", "Value", "Type", "Expand"], variables.Count);

            foreach (var variable in variables)
            {
                fmt.AddRow(sb, [
                    variable.Name,
                    variable.Value,
                    variable.Type,
                    variable.VariablesReference > 0 ? variable.VariablesReference.ToString() : "",
                ]);
            }
            fmt.EndTable(sb);

            fmt.AppendHints(sb,
                "Pass a value from the Expand column to DebugExpand to see its members",
                "References are only valid until the target resumes");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description(
        "Assign a new value to a variable, field, or property path in the current frame, without " +
        "editing code or restarting. Use it to drive a branch that is hard to reach, or to " +
        "correct state and continue. Limited to values the target can parse from a literal.")]
    public static async Task<string> DebugSetVariable(
        [Description("Variable or member path, e.g. 'count' or 'order.Customer.Id'.")]
        string name,
        [Description("New value, written as a literal, e.g. '42', 'true', '\"done\"'.")]
        string value,
        IOutputFormatter fmt,
        [Description("Frame to assign in; 0 is the innermost.")]
        int frame = 0,
        CancellationToken cancellationToken = default)
    {
        var session = DebugSessionManager.GetSession();
        if (session is null)
            return "Error: No active debug session.";

        try
        {
            var (ok, stored, error) = await session.SetVariableAsync(name, value, frame, cancellationToken);
            if (!ok)
                return $"Error: could not assign to '{name}': {error}";

            var sb = new StringBuilder();
            // Reporting what the target stored rather than what was asked for: narrowing and
            // property setters can make the two differ.
            fmt.AppendField(sb, name, stored);
            fmt.AppendHints(sb, "Use DebugContinue to run on with the new value");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}
