using System.Text.Json;

namespace RoslynMCP.Services.Debugging;

/// <summary>
/// Serialization for the structured debug payloads that travel as JSON strings through the
/// command pipe. camelCase, matching what StreamJsonRpc puts on the LSP wire, so the extension
/// reads one shape regardless of which route the data took.
/// </summary>
internal static class DebugJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}

/// <summary>One frame of the stopped thread's call stack.</summary>
/// <param name="Id">Frame index, 0 being the innermost. Doubles as the DAP frame id.</param>
/// <param name="IsExternal">Framework, native, or symbol-less code — rendered subtly and
/// collapsed in the markdown surface.</param>
/// <param name="ModulePath">The module the frame executes in — carried for frames without
/// source, so external-source resolution can find the method behind them.</param>
/// <param name="MethodToken">The frame's MethodDef token in <paramref name="ModulePath"/>;
/// 0 when the engine did not say.</param>
/// <param name="IlOffset">Where the IP is within the method's IL; -1 when unknown.</param>
/// <param name="SourceOrigin">How <paramref name="FilePath"/> was obtained when it is not the
/// PDB's own answer: <c>embedded</c>, <c>source link</c>, <c>reference source</c> or
/// <c>decompiled</c>. Empty for ordinary project frames.</param>
public sealed record StackFrameInfo(
    int Id,
    string Name,
    string FilePath,
    int Line,
    int Column,
    bool IsExternal,
    string ModulePath = "",
    int MethodToken = 0,
    int IlOffset = -1,
    string SourceOrigin = "");

/// <summary>
/// One variable in scope, or one child of an expandable one.
/// </summary>
/// <param name="VariablesReference">Non-zero when the value can be expanded; pass it back to
/// <c>GetVariableChildrenAsync</c>. Zero for leaves, matching DAP's convention exactly.</param>
/// <param name="Evaluable">Whether the name is a path the backend can assign to, which decides
/// whether the Variables view offers editing.</param>
public sealed record VariableInfo(
    string Name,
    string Value,
    string Type,
    int VariablesReference,
    int NamedChildCount,
    int IndexedChildCount,
    bool Evaluable);

public sealed record ThreadInfo(int Id, string Name, string State);

/// <summary>The exception that suspended the target, for the debugger's exception popup.</summary>
/// <param name="BreakMode">DAP's vocabulary: <c>always</c>, <c>unhandled</c>, or
/// <c>userUnhandled</c>.</param>
public sealed record ExceptionDetail(
    string TypeName,
    string Message,
    string? StackTrace,
    string BreakMode);

/// <summary>Which exceptions should suspend the target.</summary>
/// <remarks>netcoredbg advertises exactly two filters, <c>all</c> and <c>user-unhandled</c>,
/// so anything richer would be a promise we cannot keep.</remarks>
public sealed record ExceptionFilters(bool All, bool UserUnhandled)
{
    public static ExceptionFilters None { get; } = new(false, false);

    public static ExceptionFilters FromIds(IEnumerable<string> ids)
    {
        bool all = false, userUnhandled = false;
        foreach (string id in ids)
        {
            if (id.Equals("all", StringComparison.OrdinalIgnoreCase))
                all = true;
            else if (id.Equals("user-unhandled", StringComparison.OrdinalIgnoreCase) ||
                     id.Equals("userUnhandled", StringComparison.OrdinalIgnoreCase))
                userUnhandled = true;
        }
        return new ExceptionFilters(all, userUnhandled);
    }
}

/// <summary>
/// Maps expandable values to the integer references DAP speaks, since both backends address
/// children by expression path rather than by handle.
/// </summary>
/// <remarks>
/// References are reset on every stop: an expression's identity survives, but the object it
/// names does not, and handing out a stale handle silently reports the wrong object's fields.
/// </remarks>
internal sealed class VariableHandles
{
    private readonly Dictionary<int, string> _byReference = [];
    private readonly Dictionary<string, int> _byExpression = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    // Above every reference the adapter reserves for scopes; overlapping them would make a
    // variable's handle read as a frame's locals.
    private int _next = DapServer.ScopeBase + DapServer.ScopeLimit;

    public int For(string expression)
    {
        lock (_gate)
        {
            if (_byExpression.TryGetValue(expression, out int existing))
                return existing;

            int reference = _next++;
            _byExpression[expression] = reference;
            _byReference[reference] = expression;
            return reference;
        }
    }

    public string? Expression(int reference)
    {
        lock (_gate)
            return _byReference.GetValueOrDefault(reference);
    }

    public void Reset()
    {
        lock (_gate)
        {
            _byReference.Clear();
            _byExpression.Clear();
        }
    }
}
