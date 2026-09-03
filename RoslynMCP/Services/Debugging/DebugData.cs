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
/// <param name="IsNonUserCode">A frame the engine identified as plumbing: a
/// <c>DebuggerHidden</c> or <c>DebuggerNonUserCode</c> method, or a module outside the user's own
/// output. Distinct from <paramref name="IsExternal"/>, which is about having no source to show —
/// a hidden method in the user's own assembly has source and still should not be read past.</param>
/// <param name="ModulePath">The module the frame executes in — carried for frames without
/// source, so external-source resolution can find the method behind them.</param>
/// <param name="MethodToken">The frame's MethodDef token in <paramref name="ModulePath"/>;
/// 0 when the engine did not say.</param>
/// <param name="IlOffset">Where the IP is within the method's IL; -1 when unknown.</param>
/// <param name="EndLine">Where the executing statement ends; 0 when the symbols did not say. This
/// is what makes the frame reportable as an active statement rather than only as a location.</param>
/// <param name="EndColumn">The statement's end column; 0 when unknown.</param>
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
    string SourceOrigin = "",
    bool IsNonUserCode = false,
    int EndLine = 0,
    int EndColumn = 0);

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
/// <remarks>
/// The two filter ids are the ones netcoredbg advertises, <c>all</c> and <c>user-unhandled</c>, so
/// the vocabulary is the same on both runtimes. The type lists are honoured only by the ICorDebug
/// engine, which applies them in the debuggee's own suspend; netcoredbg ignores them rather than
/// pretending, because filtering after the stop has already happened would not save the cost the
/// filter exists for.
/// </remarks>
/// <param name="IncludeTypes">When non-empty, only these exception types stop under
/// <paramref name="All"/>. Matched by full or simple name, against the thrown type and its base
/// types.</param>
/// <param name="ExcludeTypes">Types that never stop under <paramref name="All"/>. Applied after
/// <paramref name="IncludeTypes"/>.</param>
/// <param name="UnhandledIncludeTypes">The same, for <paramref name="UserUnhandled"/>. Kept apart
/// from the caught lists because DAP scopes a condition to the filter it was written on: narrowing
/// "All Exceptions" to one type says nothing about which unhandled crashes should stop, and reusing
/// the list there would let every other type die unseen.</param>
/// <param name="UnhandledExcludeTypes">The same, for <paramref name="UserUnhandled"/>.</param>
public sealed record ExceptionFilters(
    bool All,
    bool UserUnhandled,
    IReadOnlyList<string>? IncludeTypes = null,
    IReadOnlyList<string>? ExcludeTypes = null,
    IReadOnlyList<string>? UnhandledIncludeTypes = null,
    IReadOnlyList<string>? UnhandledExcludeTypes = null)
{
    public static ExceptionFilters None { get; } = new(false, false);

    public static ExceptionFilters FromIds(IEnumerable<string> ids) => FromIds(ids, null);

    /// <summary>
    /// Compares the type lists by content.
    /// </summary>
    /// <remarks>
    /// A record compares list members by reference, which would report two settings naming exactly
    /// the same exception types as different. Every use of this type treats it as a value, so it
    /// has to compare like one.
    /// </remarks>
    public bool Equals(ExceptionFilters? other) =>
        other is not null &&
        All == other.All &&
        UserUnhandled == other.UserUnhandled &&
        SameTypes(IncludeTypes, other.IncludeTypes) &&
        SameTypes(ExcludeTypes, other.ExcludeTypes) &&
        SameTypes(UnhandledIncludeTypes, other.UnhandledIncludeTypes) &&
        SameTypes(UnhandledExcludeTypes, other.UnhandledExcludeTypes);

    public override int GetHashCode() =>
        HashCode.Combine(
            All, UserUnhandled,
            IncludeTypes?.Count ?? 0, ExcludeTypes?.Count ?? 0,
            UnhandledIncludeTypes?.Count ?? 0, UnhandledExcludeTypes?.Count ?? 0);

    private static bool SameTypes(IReadOnlyList<string>? left, IReadOnlyList<string>? right) =>
        (left ?? []).SequenceEqual(right ?? [], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads DAP's <c>setExceptionBreakpoints</c> arguments.
    /// </summary>
    /// <param name="conditions">The <c>condition</c> string of each <c>filterOptions</c> entry,
    /// keyed by its <c>filterId</c>: a comma-separated list of type names, each optionally prefixed
    /// with <c>!</c> to exclude it. This is the vocabulary the editor's own exception settings
    /// already use, so a user who knows one knows the other. Keyed rather than merged because DAP
    /// scopes a condition to the filter it was written on.</param>
    public static ExceptionFilters FromIds(
        IEnumerable<string> ids, IReadOnlyDictionary<string, string>? conditions)
    {
        bool all = false, userUnhandled = false;
        foreach (string id in ids)
        {
            if (IsAll(id))
                all = true;
            else if (IsUserUnhandled(id))
                userUnhandled = true;
        }

        var (caughtInclude, caughtExclude) = ParseCondition(Condition(conditions, IsAll));
        var (unhandledInclude, unhandledExclude) =
            ParseCondition(Condition(conditions, IsUserUnhandled));

        return new ExceptionFilters(
            all, userUnhandled,
            caughtInclude, caughtExclude,
            unhandledInclude, unhandledExclude);
    }

    private static bool IsAll(string id) => id.Equals("all", StringComparison.OrdinalIgnoreCase);

    private static bool IsUserUnhandled(string id) =>
        id.Equals("user-unhandled", StringComparison.OrdinalIgnoreCase) ||
        id.Equals("userUnhandled", StringComparison.OrdinalIgnoreCase);

    private static string? Condition(
        IReadOnlyDictionary<string, string>? conditions, Func<string, bool> matches)
    {
        foreach (var (id, condition) in conditions ?? new Dictionary<string, string>())
        {
            if (matches(id))
                return condition;
        }
        return null;
    }

    /// <summary>
    /// Splits one condition string into the types it admits and the types it rejects.
    /// </summary>
    /// <remarks>
    /// Both come back null rather than empty when nothing was named, so "no type filter" is one
    /// value however it was arrived at — the difference is invisible to every reader, and record
    /// equality would otherwise report two identical settings as different.
    /// </remarks>
    private static (List<string>? Include, List<string>? Exclude) ParseCondition(string? condition)
    {
        List<string> include = [], exclude = [];
        foreach (string part in (condition ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string name = part.Trim();
            if (name.StartsWith('!'))
            {
                name = name[1..].Trim();
                if (name.Length > 0)
                    exclude.Add(name);
            }
            else if (name.Length > 0)
            {
                include.Add(name);
            }
        }

        return (include.Count > 0 ? include : null, exclude.Count > 0 ? exclude : null);
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
