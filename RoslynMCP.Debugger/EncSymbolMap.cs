using System.Text.Json;

namespace RoslynMCP.Debugger;

/// <summary>
/// What an Edit-and-Continue delta did to the debugger's view of the source, as opposed to what it
/// did to the runtime.
/// </summary>
/// <remarks>
/// <para>
/// A delta PDB describes only the methods the edit changed. Every other method in an edited file
/// still carries its pre-edit line numbers, so after the first edit that adds or removes a line,
/// every method below it in that file is off by that many lines — and the error accumulates with
/// each further edit. The compiler knows exactly how the lines moved; this carries that knowledge
/// to the symbol store, which is the only place that can act on it.
/// </para>
/// <para>
/// Travels as JSON because the delta crosses two process boundaries on its way to the debuggee: the
/// worker pipe and, when the session is owned by another host, the command pipe.
/// </para>
/// </remarks>
public sealed class EncSymbolMap
{
    /// <summary>MethodDef tokens the delta itself describes. They are already correct in the delta
    /// PDB, so they are the methods that must <em>not</em> be shifted a second time.</summary>
    public int[] UpdatedMethods { get; set; } = [];

    /// <summary>How lines moved, per source file.</summary>
    public EncFileLineMap[] Files { get; set; } = [];

    public bool IsEmpty => UpdatedMethods.Length == 0 && Files.Length == 0;

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>
    /// Reads a map back, treating anything unreadable as "no information".
    /// </summary>
    /// <remarks>
    /// A malformed map must not fail the apply: the edit itself is already in the runtime by the
    /// time the symbols are updated, and degrading to the old drifting-line behaviour beats
    /// reporting a failure for an edit that succeeded.
    /// </remarks>
    public static EncSymbolMap? Parse(string? json)
    {
        if (json is not { Length: > 0 })
            return null;

        try
        {
            var map = JsonSerializer.Deserialize<EncSymbolMap>(json, Options);
            return map is null || map.IsEmpty ? null : map;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>One source file's line movements.</summary>
/// <param name="File">The path as the compiler saw it, which is also how the PDB spells it.</param>
/// <param name="Shifts">Ordered runs; see <see cref="EncLineShift"/>.</param>
public sealed record EncFileLineMap(string File, EncLineShift[] Shifts)
{
    /// <summary>
    /// How far a given 0-based line moved.
    /// </summary>
    /// <remarks>
    /// The runs are ordered here rather than trusted to arrive ordered, because reading them in the
    /// wrong order does not fail — it quietly returns the wrong shift for every line past the first
    /// one out of place, which shows up much later as a breakpoint on the wrong statement.
    /// A line before the first run has not moved: the edit was below it.
    /// </remarks>
    public int ShiftAt(int line)
    {
        int delta = 0;
        foreach (var shift in Shifts.OrderBy(s => s.OldLine))
        {
            if (shift.OldLine > line)
                break;
            delta = shift.NewLine - shift.OldLine;
        }

        return delta;
    }
}

/// <summary>
/// The start of a run of lines that moved: every line from <paramref name="OldLine"/> onwards moves
/// to <paramref name="NewLine"/> and beyond, until the next run says otherwise. Both are 0-based,
/// as the compiler reports them; symbols count from 1.
/// </summary>
public sealed record EncLineShift(int OldLine, int NewLine);
