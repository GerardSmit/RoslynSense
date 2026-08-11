using System.Text.Json;

namespace RoslynMCP.Services.Testing;

/// <summary>One method's coverage, flattened out of a Cobertura report.</summary>
public sealed record CoverageSnapshotMethod(
    string Namespace,
    string ClassFullName,
    string MethodName,
    string Signature,
    string FilePath,
    int Line,
    int CoveredStatements,
    int TotalStatements,
    int CoveredBranches,
    int TotalBranches);

/// <summary>
/// The last coverage measurement, kept per solution so a coverage view can be opened without
/// re-running anything — and can still be there tomorrow.
/// </summary>
/// <remarks>
/// Flat rather than a tree: the namespace/class nesting a coverage window shows is a rendering
/// choice, and building it here would force every client to accept the same one. Written by
/// every path that collects coverage, so the view does not care which one the user used.
/// </remarks>
public sealed record CoverageSnapshot(
    string SolutionPath,
    DateTime CollectedAtUtc,
    IReadOnlyList<CoverageSnapshotMethod> Methods)
{
    public static CoverageSnapshot Empty(string solutionPath) => new(solutionPath, default, []);

    public bool IsEmpty => Methods.Count == 0;

    public int CoveredStatements => Methods.Sum(m => m.CoveredStatements);

    public int TotalStatements => Methods.Sum(m => m.TotalStatements);
}

public static class CoverageSnapshotStore
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Records what a coverage run measured, for the solution the project belongs to. Best
    /// effort: a run that cannot be recorded is still a run that happened.
    /// </summary>
    public static void Record(string projectPath, CoverageData data)
    {
        try
        {
            if (PathHelper.FindNearestSolution(projectPath) is not { } solution)
                return;

            Save(solution, new CoverageSnapshot(solution, DateTime.UtcNow, Flatten(data)));
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"Could not record the coverage snapshot: {ex.Message}", key: "coverage-snapshot");
        }
    }

    /// <summary>
    /// Folds one more report into what is already recorded, keeping the better measurement of
    /// each method.
    /// </summary>
    /// <remarks>
    /// The coverage map is built one test class at a time, and each of those runs sees the whole
    /// codebase but exercises a slice of it. Overwriting per run would leave the snapshot showing
    /// whatever the last class happened to touch; taking the maximum per method turns the same
    /// passes into the aggregate the whole suite would have produced.
    /// </remarks>
    public static void Merge(string projectPath, CoverageData data)
    {
        try
        {
            if (PathHelper.FindNearestSolution(projectPath) is not { } solution)
                return;

            var merged = new Dictionary<(string, string, string), CoverageSnapshotMethod>();

            foreach (var method in Load(solution).Methods.Concat(Flatten(data)))
            {
                var key = (method.ClassFullName, method.MethodName, method.Signature);
                if (!merged.TryGetValue(key, out var existing)
                    || method.CoveredStatements > existing.CoveredStatements)
                {
                    merged[key] = method;
                }
            }

            Save(solution, new CoverageSnapshot(
                solution, DateTime.UtcNow,
                merged.Values.OrderBy(m => m.ClassFullName, StringComparer.Ordinal).ToList()));
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"Could not merge the coverage snapshot: {ex.Message}", key: "coverage-snapshot");
        }
    }

    public static void Save(string solutionPath, CoverageSnapshot snapshot)
    {
        string file = FileFor(solutionPath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(snapshot, s_json));
    }

    public static CoverageSnapshot Load(string solutionPath)
    {
        try
        {
            string file = FileFor(solutionPath);
            return File.Exists(file)
                ? JsonSerializer.Deserialize<CoverageSnapshot>(File.ReadAllText(file), s_json)
                    ?? CoverageSnapshot.Empty(solutionPath)
                : CoverageSnapshot.Empty(solutionPath);
        }
        catch
        {
            return CoverageSnapshot.Empty(solutionPath);
        }
    }

    public static CoverageSnapshot LoadNearest(string anchorPath) =>
        PathHelper.FindNearestSolution(anchorPath) is { } solution
            ? Load(solution)
            : CoverageSnapshot.Empty(anchorPath);

    public static void Clear(string solutionPath)
    {
        try
        {
            string file = FileFor(solutionPath);
            if (File.Exists(file))
                File.Delete(file);
        }
        catch { /* best effort */ }
    }

    private static IReadOnlyList<CoverageSnapshotMethod> Flatten(CoverageData data)
    {
        var methods = new List<CoverageSnapshotMethod>();

        foreach (var file in data.Files.Values)
        {
            foreach (var cls in file.Classes)
            {
                foreach (var method in cls.Methods)
                {
                    // Coverlet writes generated members — property backing, async state machine
                    // moves — as methods of their own. They are noise in a tree meant to be read.
                    if (method.Name.StartsWith('<') || method.TotalLines == 0)
                        continue;

                    int lastDot = cls.FullName.LastIndexOf('.');
                    methods.Add(new CoverageSnapshotMethod(
                        Namespace: lastDot > 0 ? cls.FullName[..lastDot] : "",
                        ClassFullName: cls.FullName,
                        MethodName: method.Name,
                        Signature: method.Signature,
                        FilePath: method.FilePath,
                        Line: method.Lines.Count > 0 ? method.Lines.Min(l => l.LineNumber) : 0,
                        CoveredStatements: method.CoveredLines,
                        TotalStatements: method.TotalLines,
                        CoveredBranches: method.CoveredBranches,
                        TotalBranches: method.TotalBranches));
                }
            }
        }

        return methods;
    }

    private static string FileFor(string solutionPath) =>
        Path.Combine(
            Path.GetTempPath(), "roslyn-sense", "coverage-snapshot",
            Daemon.HostPaths.SolutionHash(Path.GetFullPath(solutionPath)) + ".json");
}
