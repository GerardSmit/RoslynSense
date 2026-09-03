using System.Collections.Concurrent;
using System.Text.Json;

namespace RoslynMCP.Debugger;

/// <summary>
/// One sequence point in decompiled source: the same shape a PDB records, for a file the compiler
/// never saw.
/// </summary>
public sealed record DecompiledPoint(
    int Offset, int Line, int Column, int EndLine, int EndColumn);

/// <summary>One decompiled type: the file it was written to, and the methods it covers.</summary>
public sealed class DecompiledSymbolMap
{
    /// <summary>Where the decompiled text was persisted. Real and readable, unlike a PDB's
    /// document URL, which names the build machine's path.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>MethodDef token to that method's points, which need not arrive ordered.</summary>
    public Dictionary<int, DecompiledPoint[]> Methods { get; set; } = [];

    public bool IsEmpty => FilePath.Length == 0 || Methods.Count == 0;

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>
    /// Reads a map back, treating anything unreadable as "no information".
    /// </summary>
    /// <remarks>
    /// Symbols that fail to parse must not fail anything: this is a fallback for modules that had
    /// no symbols to begin with, so the worst case of dropping it is the behaviour that was there
    /// before it existed.
    /// </remarks>
    public static DecompiledSymbolMap? Parse(string? json)
    {
        if (json is not { Length: > 0 })
            return null;

        try
        {
            var map = JsonSerializer.Deserialize<DecompiledSymbolMap>(json, Options);
            return map is null || map.IsEmpty ? null : map;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// The decompiled source known for one module, as a symbol store the engine can read the same way
/// it reads a PDB.
/// </summary>
/// <remarks>
/// <para>
/// A module without a PDB used to be answered twice: the engine returned no location at all, and
/// the host afterwards decompiled the frame and wrote a file and line back into the reported stack.
/// That covered the stack and nothing else — a step inside such a frame had no statement range, so
/// stepping over a line ran one IL instruction and landed back on the same line, and a breakpoint
/// in the decompiled file had no document to bind against.
/// </para>
/// <para>
/// So the decompiled data comes back the other way: the host hands what it decompiled to the
/// engine, and the engine treats it as a third kind of symbols. Everything downstream — locating a
/// frame, ranging a step, binding a breakpoint, listing a file's stoppable lines — then asks one
/// question of one interface instead of having a with-symbols path and a without-symbols path that
/// drift apart.
/// </para>
/// <para>
/// It grows a type at a time, as the host decompiles them, and is never complete: a module holds
/// far more types than a session will ever stop in, and decompiling all of them to answer one
/// question would cost more than the question is worth. Absence therefore means "not decompiled
/// yet", not "not there".
/// </para>
/// </remarks>
public sealed class DecompiledSymbolSet
{
    private readonly ConcurrentDictionary<int, MethodPoints> _byToken = new();
    private readonly ConcurrentDictionary<string, byte> _files =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One method's points, ordered by offset once so every lookup need not order them.</summary>
    private sealed record MethodPoints(string FilePath, DecompiledPoint[] Ordered);

    /// <summary>Takes in one decompiled type. Types already known are replaced rather than merged:
    /// a second decompile of the same type is a newer answer for the whole of it.</summary>
    public void Add(DecompiledSymbolMap map)
    {
        if (map.IsEmpty)
            return;

        _files[map.FilePath] = 0;
        foreach (var (token, points) in map.Methods)
        {
            if (points is not { Length: > 0 })
                continue;
            var ordered = points.Where(p => p is not null).OrderBy(p => p.Offset).ToArray();
            if (ordered.Length > 0)
                _byToken[token] = new MethodPoints(map.FilePath, ordered);
        }
    }

    public bool IsEmpty => _byToken.IsEmpty;

    /// <summary>The decompiled files known so far, for listing a module's documents.</summary>
    public IEnumerable<string> Files => _files.Keys;

    public bool Describes(string filePath) => _files.ContainsKey(filePath);

    /// <summary>The file a method was decompiled into, or empty if it has not been.</summary>
    public string FileOf(int methodToken) =>
        _byToken.TryGetValue(methodToken, out var method) ? method.FilePath : string.Empty;

    /// <summary>The methods decompiled into one file, so a breakpoint in it has somewhere to look.</summary>
    public IEnumerable<int> MethodsIn(string filePath)
    {
        foreach (var (token, method) in _byToken)
        {
            if (string.Equals(method.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                yield return token;
        }
    }

    /// <summary>
    /// The point covering an IL offset, with where the next one starts.
    /// </summary>
    /// <remarks>
    /// The next point's offset is what makes a step a statement rather than an instruction, and it
    /// is not recorded anywhere — it is the ordering that says it, which is why the points are
    /// ordered on the way in. The last point in a method has no next, and reports
    /// <see cref="int.MaxValue"/> for the caller to bound against the method's IL size, exactly as
    /// the PDB readers do.
    /// </remarks>
    public (DecompiledPoint Point, int NextOffset, string FilePath)? PointAt(int methodToken, int ilOffset)
    {
        if (!_byToken.TryGetValue(methodToken, out var method))
            return null;

        var points = method.Ordered;
        for (var i = 0; i < points.Length; i++)
        {
            int start = points[i].Offset;

            // Past any point sharing this one's offset: a zero-width range degrades a step to a
            // single IL instruction, which is the failure this whole path exists to fix.
            int next = i + 1;
            while (next < points.Length && points[next].Offset <= start)
                next++;

            int end = next < points.Length ? points[next].Offset : int.MaxValue;
            if (ilOffset >= start && ilOffset < end)
                return (points[i], end, method.FilePath);
        }

        return null;
    }

    /// <summary>
    /// The best point in a method for a source line, for binding a breakpoint.
    /// </summary>
    /// <remarks>
    /// Exact line wins; otherwise the first point below it, because a line the decompiler wrote no
    /// code for — a brace, a blank line, a declaration it folded away — should bind to the next
    /// line that does run rather than refuse.
    /// </remarks>
    public (DecompiledPoint Point, string FilePath)? BestPoint(int methodToken, int line, int column)
    {
        if (!_byToken.TryGetValue(methodToken, out var method))
            return null;

        DecompiledPoint? fallback = null;
        foreach (var point in method.Ordered)
        {
            if (point.Line == line)
            {
                if (column <= 1 || point.Column == 0 || point.Column >= column)
                    return (point, method.FilePath);
                fallback ??= point;
            }
            else if (point.Line > line && fallback is null)
            {
                fallback = point;
            }
        }

        return fallback is null ? null : (fallback, method.FilePath);
    }

    /// <summary>Every point in a file, for listing where a breakpoint could go.</summary>
    public IEnumerable<(int MethodToken, DecompiledPoint Point)> PointsIn(string filePath)
    {
        foreach (var (token, method) in _byToken)
        {
            if (!string.Equals(method.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var point in method.Ordered)
                yield return (token, point);
        }
    }
}
