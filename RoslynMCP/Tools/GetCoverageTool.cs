using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using RoslynMCP.Services;

namespace RoslynMCP.Tools;

[McpServerToolType]
public static class GetCoverageTool
{
    [McpServerTool, Description(
        "Query code coverage data. Without filters, shows project-wide coverage by type. " +
        "Filter by method, class, or file for detailed views. " +
        "Requires RunCoverage to have been called first.")]
    public static Task<string> GetCoverage(
        IOutputFormatter fmt,
        [Description("Path to the source file to get coverage for. Leave empty for project overview.")]
        string? filePath = null,
        [Description("Optional method name to filter results (partial match).")]
        string? methodName = null,
        [Description("Optional class name to filter results (partial match).")]
        string? className = null,
        [Description("Show line-by-line coverage detail for uncovered lines. Default: true.")]
        bool showUncoveredLines = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var data = CoverageService.GetCachedCoverage(out var projectPath, out var cachedAt);
            if (data is null)
                return Task.FromResult(
                    "Error: No coverage data available. Run `RunCoverage` first to collect coverage data.");

            // If a specific method is requested
            if (!string.IsNullOrWhiteSpace(methodName))
            {
                return Task.FromResult(FormatMethodCoverage(methodName, showUncoveredLines, cachedAt, fmt));
            }

            // If a specific class is requested
            if (!string.IsNullOrWhiteSpace(className))
            {
                return Task.FromResult(FormatClassCoverage(className, showUncoveredLines, cachedAt, fmt));
            }

            // File-level coverage
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                return Task.FromResult(FormatFileCoverage(filePath, showUncoveredLines, cachedAt, fmt));
            }

            // Project-wide overview
            return Task.FromResult(FormatProjectCoverage(data, projectPath, cachedAt, fmt));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Task.FromResult($"Error: {ex.Message}");
        }
    }

    private static string FormatMethodCoverage(string methodName, bool showUncoveredLines, DateTime cachedAt, IOutputFormatter fmt)
    {
        var methods = CoverageService.FindMethodCoverage(methodName);
        if (methods.Count == 0)
            return $"No coverage data found for method matching '{methodName}'. " +
                   "The method may not be covered by any test, or run `RunCoverage` to update data.";

        var sb = new StringBuilder();
        fmt.AppendHeader(sb, $"Method Coverage: {methodName}");
        fmt.AppendField(sb, "Coverage data from", $"{cachedAt:yyyy-MM-dd HH:mm:ss} UTC");
        fmt.AppendSeparator(sb);

        foreach (var method in methods)
        {
            fmt.AppendHeader(sb, method.FullName, 2);
            fmt.AppendField(sb, "File", Path.GetFileName(method.FilePath));
            fmt.AppendField(sb, "Line Coverage", $"{method.LineCoverageRate:P1} ({method.CoveredLines}/{method.TotalLines})");
            if (method.TotalBranches > 0)
                fmt.AppendField(sb, "Branch Coverage", $"{method.BranchCoverageRate:P1} ({method.CoveredBranches}/{method.TotalBranches})");
            fmt.AppendSeparator(sb);

            if (showUncoveredLines && method.Lines.Count > 0)
            {
                // Show branch details for partially covered branches
                var partialBranches = method.Lines
                    .Where(l => l.IsBranch && l.ConditionCoverage is not null && !l.IsFullBranchCoverage)
                    .ToList();
                if (partialBranches.Count > 0)
                {
                    fmt.AppendField(sb, "Partial branches", "not all paths tested");
                    foreach (var line in partialBranches)
                    {
                        sb.AppendLine($"  - Line {line.LineNumber}: {line.ConditionCoverage}");
                    }
                    sb.AppendLine();
                }

                var uncovered = method.Lines.Where(l => l.Hits == 0).ToList();
                if (uncovered.Count > 0)
                {
                    fmt.AppendField(sb, "Uncovered lines", uncovered.Count);
                    foreach (var line in uncovered)
                    {
                        sb.AppendLine($"  - Line {line.LineNumber}");
                    }
                    sb.AppendLine();
                }
                else
                {
                    sb.AppendLine("All lines covered. ✅");
                    sb.AppendLine();
                }
            }
        }

        fmt.AppendHints(sb,
            "Use FindTests to find tests covering specific code",
            "Use RunCoverage to refresh coverage data");

        return sb.ToString();
    }

    private static string FormatClassCoverage(string className, bool showUncoveredLines, DateTime cachedAt, IOutputFormatter fmt)
    {
        var classes = CoverageService.FindClassCoverage(className);
        if (classes.Count == 0)
            return $"No coverage data found for class matching '{className}'. " +
                   "Run `RunCoverage` to update data.";

        var sb = new StringBuilder();
        fmt.AppendHeader(sb, $"Class Coverage: {className}");
        fmt.AppendField(sb, "Coverage data from", $"{cachedAt:yyyy-MM-dd HH:mm:ss} UTC");
        fmt.AppendSeparator(sb);

        foreach (var cls in classes)
        {
            fmt.AppendHeader(sb, cls.FullName, 2);
            fmt.AppendField(sb, "File", Path.GetFileName(cls.FilePath));
            fmt.AppendField(sb, "Line Coverage", $"{cls.LineCoverageRate:P1}");
            fmt.AppendField(sb, "Branch Coverage", $"{cls.BranchCoverageRate:P1}");
            fmt.AppendSeparator(sb);

            if (cls.Methods.Count > 0)
            {
                var columns = new[] { "Method", "Lines", "Branches" };
                var rows = new List<string[]>();
                foreach (var method in cls.Methods)
                {
                    string lineIcon = method.LineCoverageRate >= 1.0 ? "✅" :
                                      method.LineCoverageRate >= 0.5 ? "⚠️" : "❌";
                    string branchCol = method.TotalBranches > 0
                        ? $"{method.BranchCoverageRate:P0} ({method.CoveredBranches}/{method.TotalBranches})"
                        : "—";
                    rows.Add([
                        $"{lineIcon} {fmt.Escape(method.Name)}",
                        $"{method.LineCoverageRate:P0} ({method.CoveredLines}/{method.TotalLines})",
                        branchCol
                    ]);
                }
                fmt.AppendTable(sb, "methods", columns, rows);
                sb.AppendLine();
            }

            if (showUncoveredLines)
            {
                var uncovered = cls.Lines.Where(l => l.Hits == 0).ToList();
                if (uncovered.Count > 0)
                {
                    fmt.AppendField(sb, "Uncovered lines", uncovered.Count);
                    foreach (var line in uncovered.Take(20))
                    {
                        sb.AppendLine($"  - Line {line.LineNumber}");
                    }
                    if (uncovered.Count > 20)
                        sb.AppendLine($"  _... and {uncovered.Count - 20} more_");
                    sb.AppendLine();
                }
            }
        }

        fmt.AppendHints(sb,
            "Use FindTests to find tests covering specific code",
            "Use RunCoverage to refresh coverage data");

        return sb.ToString();
    }

    private static string FormatFileCoverage(string filePath, bool showUncoveredLines, DateTime cachedAt, IOutputFormatter fmt)
    {
        string normalized = PathHelper.NormalizePath(filePath);
        var fileCov = CoverageService.GetFileCoverage(normalized);
        if (fileCov is null)
            return $"No coverage data found for file '{Path.GetFileName(filePath)}'. " +
                   "The file may not be covered by any test, or run `RunCoverage` to update data.";

        var sb = new StringBuilder();
        fmt.AppendHeader(sb, $"File Coverage: {Path.GetFileName(filePath)}");
        fmt.AppendField(sb, "Coverage data from", $"{cachedAt:yyyy-MM-dd HH:mm:ss} UTC");
        fmt.AppendSeparator(sb);

        int totalLines = fileCov.Lines.Count;
        int coveredLines = fileCov.Lines.Count(kv => kv.Value.Hits > 0);
        double rate = totalLines > 0 ? (double)coveredLines / totalLines : 1.0;
        int totalBranches = fileCov.Methods.Sum(m => m.TotalBranches);
        int coveredBranches = fileCov.Methods.Sum(m => m.CoveredBranches);

        fmt.AppendField(sb, "Line Coverage", $"{rate:P1} ({coveredLines}/{totalLines})");
        if (totalBranches > 0)
        {
            double branchRate = (double)coveredBranches / totalBranches;
            fmt.AppendField(sb, "Branch Coverage", $"{branchRate:P1} ({coveredBranches}/{totalBranches})");
        }
        fmt.AppendField(sb, "Classes", $"{fileCov.Classes.Count} | Methods: {fileCov.Methods.Count}");
        fmt.AppendSeparator(sb);

        if (fileCov.Methods.Count > 0)
        {
            fmt.AppendHeader(sb, "Methods", 2);

            var columns = new[] { "Method", "Lines", "Branches" };
            var rows = new List<string[]>();

            foreach (var method in fileCov.Methods.OrderBy(m => m.LineCoverageRate))
            {
                string icon = method.LineCoverageRate >= 1.0 ? "✅" :
                              method.LineCoverageRate >= 0.5 ? "⚠️" : "❌";
                string branchCol = method.TotalBranches > 0
                    ? $"{method.BranchCoverageRate:P0} ({method.CoveredBranches}/{method.TotalBranches})"
                    : "—";
                rows.Add([
                    $"{icon} {fmt.Escape(method.Name)}",
                    $"{method.LineCoverageRate:P0} ({method.CoveredLines}/{method.TotalLines})",
                    branchCol
                ]);
            }
            fmt.AppendTable(sb, "methods", columns, rows);
            sb.AppendLine();
        }

        if (showUncoveredLines)
        {
            // Show partial branches first
            var partialBranches = fileCov.Lines.Values
                .Where(l => l.IsBranch && l.ConditionCoverage is not null && !l.IsFullBranchCoverage)
                .OrderBy(l => l.LineNumber)
                .ToList();
            if (partialBranches.Count > 0)
            {
                fmt.AppendHeader(sb, "Partial Branches", 2);
                foreach (var line in partialBranches.Take(20))
                {
                    sb.AppendLine($"  - Line {line.LineNumber}: {line.ConditionCoverage}");
                }
                if (partialBranches.Count > 20)
                    sb.AppendLine($"  _... and {partialBranches.Count - 20} more_");
                sb.AppendLine();
            }

            var uncovered = fileCov.Lines
                .Where(kv => kv.Value.Hits == 0)
                .OrderBy(kv => kv.Key)
                .ToList();

            if (uncovered.Count > 0)
            {
                fmt.AppendHeader(sb, $"Uncovered Lines ({uncovered.Count})", 2);

                // Group consecutive lines into ranges for readability
                var ranges = GetLineRanges(uncovered.Select(kv => kv.Key).ToList());
                foreach (var range in ranges.Take(30))
                {
                    if (range.Start == range.End)
                        sb.AppendLine($"  - Line {range.Start}");
                    else
                        sb.AppendLine($"  - Lines {range.Start}-{range.End}");
                }

                if (ranges.Count > 30)
                    sb.AppendLine($"  _... and more uncovered ranges_");
            }
        }

        fmt.AppendHints(sb,
            "Use FindTests to find tests covering specific code",
            "Use RunCoverage to refresh coverage data");

        return sb.ToString();
    }

    private static List<(int Start, int End)> GetLineRanges(List<int> lines)
    {
        if (lines.Count == 0) return [];

        var ranges = new List<(int Start, int End)>();
        int start = lines[0];
        int end = lines[0];

        for (int i = 1; i < lines.Count; i++)
        {
            if (lines[i] == end + 1)
            {
                end = lines[i];
            }
            else
            {
                ranges.Add((start, end));
                start = lines[i];
                end = lines[i];
            }
        }
        ranges.Add((start, end));
        return ranges;
    }

    private static string FormatProjectCoverage(CoverageData data, string? projectPath, DateTime cachedAt, IOutputFormatter fmt)
    {
        var sb = new StringBuilder();
        string projectName = projectPath is not null ? Path.GetFileNameWithoutExtension(projectPath) : "Project";
        fmt.AppendHeader(sb, $"Coverage: {projectName}");
        fmt.AppendField(sb, "Coverage data from", $"{cachedAt:yyyy-MM-dd HH:mm:ss} UTC");
        fmt.AppendSeparator(sb);

        // Overall summary
        fmt.AppendHeader(sb, "Summary", 2);
        fmt.AppendField(sb, "Line Coverage", $"{data.LineCoverageRate:P1} ({data.LinesCovered}/{data.LinesValid})");
        fmt.AppendField(sb, "Branch Coverage", $"{data.BranchCoverageRate:P1} ({data.BranchesCovered}/{data.BranchesValid})");
        fmt.AppendField(sb, "Files", data.Files.Count);

        int totalClasses = data.Files.Values.Sum(f => f.Classes.Count);
        int totalMethods = data.Files.Values.Sum(f => f.Methods.Count);
        fmt.AppendField(sb, "Classes", $"{totalClasses} | Methods: {totalMethods}");
        fmt.AppendSeparator(sb);

        // Per-class coverage table, sorted by coverage (worst first)
        var allClasses = data.Files.Values
            .SelectMany(f => f.Classes)
            .OrderBy(c => c.LineCoverageRate)
            .ToList();

        if (allClasses.Count > 0)
        {
            fmt.AppendHeader(sb, "Coverage by Type", 2);

            var columns = new[] { "Type", "Lines", "Branches", "Methods" };
            var rows = new List<string[]>();

            foreach (var cls in allClasses)
            {
                string icon = cls.LineCoverageRate >= 1.0 ? "✅" :
                              cls.LineCoverageRate >= 0.5 ? "⚠️" : "❌";

                int clsTotalBranches = cls.Methods.Sum(m => m.TotalBranches);
                int clsCoveredBranches = cls.Methods.Sum(m => m.CoveredBranches);
                string branchCol = clsTotalBranches > 0
                    ? $"{(double)clsCoveredBranches / clsTotalBranches:P0} ({clsCoveredBranches}/{clsTotalBranches})"
                    : "—";

                int clsTotalLines = cls.Lines.Count;
                int clsCoveredLines = cls.Lines.Count(l => l.Hits > 0);
                string lineCol = clsTotalLines > 0
                    ? $"{(double)clsCoveredLines / clsTotalLines:P0} ({clsCoveredLines}/{clsTotalLines})"
                    : "—";

                int coveredMethods = cls.Methods.Count(m => m.LineCoverageRate >= 1.0);
                string methodCol = $"{coveredMethods}/{cls.Methods.Count}";

                rows.Add([
                    $"{icon} {fmt.Escape(cls.FullName)}",
                    lineCol,
                    branchCol,
                    methodCol
                ]);
            }
            fmt.AppendTable(sb, "types", columns, rows);
            sb.AppendLine();
        }

        // Methods with lowest coverage (actionable)
        var lowCoverageMethods = data.Files.Values
            .SelectMany(f => f.Methods)
            .Where(m => m.LineCoverageRate < 1.0 && m.TotalLines > 0)
            .OrderBy(m => m.LineCoverageRate)
            .Take(10)
            .ToList();

        if (lowCoverageMethods.Count > 0)
        {
            fmt.AppendHeader(sb, "Lowest Coverage Methods", 2);

            var columns = new[] { "Method", "Lines", "Branches", "File" };
            var rows = new List<string[]>();

            foreach (var method in lowCoverageMethods)
            {
                string branchCol = method.TotalBranches > 0
                    ? $"{method.BranchCoverageRate:P0} ({method.CoveredBranches}/{method.TotalBranches})"
                    : "—";
                rows.Add([
                    $"❌ {fmt.Escape(method.FullName)}",
                    $"{method.LineCoverageRate:P0} ({method.CoveredLines}/{method.TotalLines})",
                    branchCol,
                    Path.GetFileName(method.FilePath)
                ]);
            }
            fmt.AppendTable(sb, "low-coverage", columns, rows);
            sb.AppendLine();
        }

        fmt.AppendHints(sb,
            "Use FindTests to find tests covering specific code",
            "Use RunCoverage to refresh coverage data");

        return sb.ToString();
    }
}