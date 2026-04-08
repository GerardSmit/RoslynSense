using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using RoslynMCP.Tools;

namespace RoslynMCP.Services;

/// <summary>
/// Manages code coverage collection and querying. Runs dotnet test with XPlat Code Coverage,
/// parses Cobertura XML, and caches results in memory for fast querying.
/// </summary>
public static class CoverageService
{
    private static readonly object Lock = new();
    private static CoverageData? _cachedData;
    private static string? _cachedProjectPath;
    private static DateTime _cachedAt;

    /// <summary>
    /// Runs dotnet test with coverage collection for a project. Returns a summary and caches results.
    /// </summary>
    public static async Task<CoverageResult> RunCoverageAsync(
        string projectPath, string? filter = null, CancellationToken cancellationToken = default)
    {
        string csprojPath;
        if (projectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && File.Exists(projectPath))
        {
            csprojPath = projectPath;
        }
        else
        {
            var found = RunTestsTool.ResolveCsprojPath(projectPath);
            if (found is null)
                return new CoverageResult(false, "Error: Could not resolve project path.", null);
            csprojPath = found;
        }

        // Create a temp directory for results to avoid conflicts
        string resultsDir = Path.Combine(Path.GetTempPath(), "roslyn-mcp-coverage", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(resultsDir);

        try
        {
            var args = new StringBuilder();
            args.Append("test ");
            args.Append('"');
            args.Append(csprojPath);
            args.Append('"');
            args.Append(" --collect:\"XPlat Code Coverage\"");
            args.Append($" --results-directory \"{resultsDir}\"");
            args.Append(" --nologo");
            if (!string.IsNullOrWhiteSpace(filter))
            {
                args.Append($" --filter \"{filter.Replace("\"", "\\\"")}\"");
            }
            // Include test assembly in coverage (for projects with code and tests together)
            args.Append(" -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.IncludeTestAssembly=true");

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = args.ToString(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(csprojPath)
                }
            };

            // Disable terminal logger to get clean parseable output
            process.StartInfo.Environment["MSBUILDTERMINALLOGGER"] = "off";

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null) stdout.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null) stderr.AppendLine(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new CoverageResult(false, "Coverage collection was cancelled.", null);
            }

            if (process.ExitCode != 0)
            {
                var output = stdout.ToString();
                if (!string.IsNullOrWhiteSpace(stderr.ToString()))
                    output += "\n" + stderr;
                return new CoverageResult(false, $"Tests failed (exit code {process.ExitCode}).\n{output}", null);
            }

            // Find the coverage.cobertura.xml file
            var coberturaFile = Directory.GetFiles(resultsDir, "coverage.cobertura.xml", SearchOption.AllDirectories)
                .FirstOrDefault();

            if (coberturaFile is null)
                return new CoverageResult(false, "Coverage file not found. Ensure coverlet.collector is referenced in the test project.", null);

            var data = ParseCoberturaXml(coberturaFile);

            lock (Lock)
            {
                _cachedData = data;
                _cachedProjectPath = csprojPath;
                _cachedAt = DateTime.UtcNow;
            }

            return new CoverageResult(true, FormatSummary(data, csprojPath), data);
        }
        finally
        {
            // Clean up temp directory
            try { Directory.Delete(resultsDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Gets cached coverage data. Returns null if no coverage has been collected yet.
    /// </summary>
    public static CoverageData? GetCachedCoverage(out string? projectPath, out DateTime cachedAt)
    {
        lock (Lock)
        {
            projectPath = _cachedProjectPath;
            cachedAt = _cachedAt;
            return _cachedData;
        }
    }

    /// <summary>
    /// Queries coverage for a specific file path.
    /// </summary>
    public static FileCoverage? GetFileCoverage(string filePath)
    {
        lock (Lock)
        {
            if (_cachedData is null) return null;

            var normalized = Path.GetFullPath(filePath);
            return _cachedData.Files.GetValueOrDefault(normalized);
        }
    }

    /// <summary>
    /// Queries coverage for a specific method by name (partial match).
    /// </summary>
    public static List<MethodCoverage> FindMethodCoverage(string methodName)
    {
        lock (Lock)
        {
            if (_cachedData is null) return [];

            var results = new List<MethodCoverage>();
            foreach (var file in _cachedData.Files.Values)
            {
                foreach (var method in file.Methods)
                {
                    if (method.Name.Contains(methodName, StringComparison.OrdinalIgnoreCase) ||
                        method.FullName.Contains(methodName, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(method);
                    }
                }
            }
            return results;
        }
    }

    /// <summary>
    /// Queries coverage for a specific class by name.
    /// </summary>
    public static List<ClassCoverage> FindClassCoverage(string className)
    {
        lock (Lock)
        {
            if (_cachedData is null) return [];

            var results = new List<ClassCoverage>();
            foreach (var file in _cachedData.Files.Values)
            {
                foreach (var cls in file.Classes)
                {
                    if (cls.Name.Contains(className, StringComparison.OrdinalIgnoreCase) ||
                        cls.FullName.Contains(className, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(cls);
                    }
                }
            }
            return results;
        }
    }

    private static CoverageData ParseCoberturaXml(string path)
    {
        var doc = XDocument.Load(path);
        var root = doc.Root!;

        var data = new CoverageData
        {
            LineCoverageRate = ParseDouble(root.Attribute("line-rate")?.Value),
            BranchCoverageRate = ParseDouble(root.Attribute("branch-rate")?.Value),
            LinesValid = ParseInt(root.Attribute("lines-valid")?.Value),
            LinesCovered = ParseInt(root.Attribute("lines-covered")?.Value),
            BranchesValid = ParseInt(root.Attribute("branches-valid")?.Value),
            BranchesCovered = ParseInt(root.Attribute("branches-covered")?.Value),
        };

        // Collect source directories for resolving relative filenames
        var sources = root.Element("sources")?.Elements("source")
            .Select(s => s.Value)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList() ?? [];

        foreach (var package in root.Descendants("package"))
        {
            foreach (var cls in package.Descendants("class"))
            {
                string fileName = cls.Attribute("filename")?.Value ?? "";
                string className = cls.Attribute("name")?.Value ?? "";
                double lineRate = ParseDouble(cls.Attribute("line-rate")?.Value);
                double branchRate = ParseDouble(cls.Attribute("branch-rate")?.Value);

                string normalizedFile = ResolveFilePath(fileName, sources);

                if (!data.Files.TryGetValue(normalizedFile, out var fileCov))
                {
                    fileCov = new FileCoverage { FilePath = normalizedFile };
                    data.Files[normalizedFile] = fileCov;
                }

                var classCov = new ClassCoverage
                {
                    Name = className.Contains('.') ? className[(className.LastIndexOf('.') + 1)..] : className,
                    FullName = className,
                    FilePath = normalizedFile,
                    LineCoverageRate = lineRate,
                    BranchCoverageRate = branchRate,
                };

                // Parse lines (only direct children of <lines>, not nested in <methods>)
                var classLines = cls.Element("lines");
                if (classLines is not null)
                {
                    foreach (var line in classLines.Elements("line"))
                    {
                        int lineNum = ParseInt(line.Attribute("number")?.Value);
                        int hits = ParseInt(line.Attribute("hits")?.Value);
                        bool isBranch = string.Equals(line.Attribute("branch")?.Value, "True", StringComparison.OrdinalIgnoreCase);
                        string? conditionCoverage = line.Attribute("condition-coverage")?.Value;

                        var lineCov = new LineCoverage
                        {
                            LineNumber = lineNum,
                            Hits = hits,
                            IsBranch = isBranch,
                            ConditionCoverage = conditionCoverage,
                        };

                        classCov.Lines.Add(lineCov);
                        fileCov.Lines[lineNum] = lineCov;
                    }
                }

                // Parse methods
                var methodsElement = cls.Element("methods");
                if (methodsElement is not null)
                {
                    foreach (var method in methodsElement.Elements("method"))
                    {
                        string methodName = method.Attribute("name")?.Value ?? "";
                        string signature = method.Attribute("signature")?.Value ?? "";

                        var methodLinesElement = method.Element("lines");
                        var methodLines = (methodLinesElement?.Elements("line") ?? []).Select(l => new LineCoverage
                        {
                            LineNumber = ParseInt(l.Attribute("number")?.Value),
                            Hits = ParseInt(l.Attribute("hits")?.Value),
                            IsBranch = string.Equals(l.Attribute("branch")?.Value, "True", StringComparison.OrdinalIgnoreCase),
                            ConditionCoverage = l.Attribute("condition-coverage")?.Value,
                        }).ToList();

                        int coveredLines = methodLines.Count(l => l.Hits > 0);
                        int totalLines = methodLines.Count;

                        // Parse branch stats from condition-coverage attributes (e.g., "50% (1/2)")
                        int totalBranches = 0, coveredBranches = 0;
                        foreach (var ml in methodLines.Where(l => l.IsBranch && l.ConditionCoverage is not null))
                        {
                            var (covered, total) = ParseConditionCoverage(ml.ConditionCoverage!);
                            totalBranches += total;
                            coveredBranches += covered;
                        }

                        var methodCov = new MethodCoverage
                        {
                            Name = methodName,
                            FullName = $"{className}.{methodName}",
                            Signature = signature,
                            FilePath = normalizedFile,
                            LineCoverageRate = totalLines > 0 ? (double)coveredLines / totalLines : 1.0,
                            Lines = methodLines,
                            CoveredLines = coveredLines,
                            TotalLines = totalLines,
                            TotalBranches = totalBranches,
                            CoveredBranches = coveredBranches,
                        };

                        classCov.Methods.Add(methodCov);
                        fileCov.Methods.Add(methodCov);
                    }
                }

                fileCov.Classes.Add(classCov);
            }
        }

        return data;
    }

    private static string FormatSummary(CoverageData data, string projectPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Coverage Report: {Path.GetFileName(projectPath)}");
        sb.AppendLine();
        sb.AppendLine($"**Line Coverage**: {data.LineCoverageRate:P1} ({data.LinesCovered}/{data.LinesValid})");
        sb.AppendLine($"**Branch Coverage**: {data.BranchCoverageRate:P1} ({data.BranchesCovered}/{data.BranchesValid})");
        sb.AppendLine();

        // Show files with lowest coverage
        var lowCoverageFiles = data.Files.Values
            .Where(f => f.Methods.Count > 0)
            .OrderBy(f => f.Methods.Average(m => m.LineCoverageRate))
            .Take(10)
            .ToList();

        if (lowCoverageFiles.Count > 0)
        {
            sb.AppendLine("## Lowest Coverage Files");
            sb.AppendLine();
            sb.AppendLine("| File | Line Coverage | Methods |");
            sb.AppendLine("|------|-------------|---------|");

            foreach (var file in lowCoverageFiles)
            {
                double avgRate = file.Methods.Average(m => m.LineCoverageRate);
                sb.AppendLine($"| {Path.GetFileName(file.FilePath)} | {avgRate:P1} | {file.Methods.Count} |");
            }
        }

        return sb.ToString();
    }

    private static double ParseDouble(string? value)
        => double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : 0;

    private static int ParseInt(string? value)
        => int.TryParse(value, out var result) ? result : 0;

    /// <summary>
    /// Parses a condition-coverage string like "50% (1/2)" into (covered, total).
    /// </summary>
    private static (int Covered, int Total) ParseConditionCoverage(string conditionCoverage)
    {
        // Format: "50% (1/2)" — extract the (covered/total) part
        int parenStart = conditionCoverage.IndexOf('(');
        int slash = conditionCoverage.IndexOf('/');
        int parenEnd = conditionCoverage.IndexOf(')');
        if (parenStart >= 0 && slash > parenStart && parenEnd > slash)
        {
            var coveredStr = conditionCoverage[(parenStart + 1)..slash];
            var totalStr = conditionCoverage[(slash + 1)..parenEnd];
            if (int.TryParse(coveredStr, out int covered) && int.TryParse(totalStr, out int total))
                return (covered, total);
        }
        return (0, 0);
    }

    /// <summary>
    /// Resolves a filename from Cobertura XML using the <sources> directories.
    /// Coverlet uses relative filenames with source directories as roots.
    /// </summary>
    private static string ResolveFilePath(string fileName, List<string> sources)
    {
        if (string.IsNullOrEmpty(fileName))
            return "";

        // If already absolute, just normalize
        if (Path.IsPathRooted(fileName))
            return Path.GetFullPath(fileName);

        // Try combining with each source directory to find an existing file
        foreach (var source in sources)
        {
            var combined = Path.GetFullPath(Path.Combine(source, fileName));
            if (File.Exists(combined))
                return combined;
        }

        // Fallback: combine with first source if available, otherwise use CWD
        if (sources.Count > 0)
            return Path.GetFullPath(Path.Combine(sources[0], fileName));

        return Path.GetFullPath(fileName);
    }
}

public class CoverageData
{
    public double LineCoverageRate { get; set; }
    public double BranchCoverageRate { get; set; }
    public int LinesValid { get; set; }
    public int LinesCovered { get; set; }
    public int BranchesValid { get; set; }
    public int BranchesCovered { get; set; }
    public Dictionary<string, FileCoverage> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class FileCoverage
{
    public string FilePath { get; set; } = "";
    public List<ClassCoverage> Classes { get; set; } = [];
    public List<MethodCoverage> Methods { get; set; } = [];
    public Dictionary<int, LineCoverage> Lines { get; set; } = [];
}

public class ClassCoverage
{
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public double LineCoverageRate { get; set; }
    public double BranchCoverageRate { get; set; }
    public List<MethodCoverage> Methods { get; set; } = [];
    public List<LineCoverage> Lines { get; set; } = [];
}

public class MethodCoverage
{
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Signature { get; set; } = "";
    public string FilePath { get; set; } = "";
    public double LineCoverageRate { get; set; }
    public List<LineCoverage> Lines { get; set; } = [];
    public int CoveredLines { get; set; }
    public int TotalLines { get; set; }
    public int TotalBranches { get; set; }
    public int CoveredBranches { get; set; }
    public double BranchCoverageRate => TotalBranches > 0 ? (double)CoveredBranches / TotalBranches : 1.0;
}

public class LineCoverage
{
    public int LineNumber { get; set; }
    public int Hits { get; set; }
    public bool IsBranch { get; set; }
    public string? ConditionCoverage { get; set; }
}

public record CoverageResult(bool Success, string Message, CoverageData? Data);
