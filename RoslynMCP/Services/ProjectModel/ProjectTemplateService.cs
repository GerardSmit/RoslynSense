using System.Diagnostics;
using System.Text;

namespace RoslynMCP.Services.ProjectModel;

/// <summary>One entry from <c>dotnet new list</c>.</summary>
public sealed record ProjectTemplateInfo(string Name, string ShortName, string Tags);

/// <summary>
/// The project templates this machine can create, read from the SDK rather than hard-coded.
/// </summary>
/// <remarks>
/// <para>
/// Listing them from <c>dotnet new</c> means the picker shows whatever is actually installed —
/// MAUI, Aspire, Avalonia, a company's own template pack — instead of a list that goes stale the
/// moment a workload is added.
/// </para>
/// <para>
/// The output is a fixed-width table with no machine-readable form, so the row of dashes under
/// the header is used to find the column boundaries. That is the one part of the layout the
/// SDK has kept stable, and parsing by column beats splitting on runs of spaces, which breaks
/// on any template name that contains two of them.
/// </para>
/// </remarks>
public static class ProjectTemplateService
{
    /// <summary>Spawning `dotnet new` takes about a second, and the answer rarely changes.</summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    private static readonly SemaphoreSlim s_gate = new(1, 1);
    private static (DateTime When, ProjectTemplateInfo[] Templates)? s_cached;

    public static async Task<ProjectTemplateInfo[]> ListAsync(CancellationToken ct = default)
    {
        await s_gate.WaitAsync(ct);
        try
        {
            if (s_cached is { } cached && DateTime.UtcNow - cached.When < CacheFor)
                return cached.Templates;

            var templates = await ReadAsync(ct);
            if (templates.Length > 0)
                s_cached = (DateTime.UtcNow, templates);
            return templates;
        }
        finally
        {
            s_gate.Release();
        }
    }

    /// <summary>
    /// Target frameworks worth offering: one per installed SDK major, plus the .NET Framework
    /// versions <c>dotnet new</c> still accepts for console and class-library projects.
    /// </summary>
    public static async Task<string[]> TargetFrameworksAsync(CancellationToken ct = default)
    {
        var frameworks = new List<string>();

        foreach (string line in await RunAsync("--list-sdks", ct))
        {
            // "10.0.100 [C:\Program Files\dotnet\sdk]"
            string version = line.Split(' ', 2)[0];
            if (version.Split('.') is [var major, ..] && int.TryParse(major, out int number) && number >= 5)
            {
                string tfm = $"net{number}.0";
                if (!frameworks.Contains(tfm))
                    frameworks.Add(tfm);
            }
        }

        frameworks.Sort((a, b) => string.CompareOrdinal(b, a));
        frameworks.AddRange(["net48", "net472", "net462"]);
        return [.. frameworks];
    }

    private static async Task<ProjectTemplateInfo[]> ReadAsync(CancellationToken ct)
    {
        var lines = await RunAsync("new list --type project --language C#", ct);

        // The dashes row separates the header from the data and defines the columns.
        int separator = lines.FindIndex(l => l.StartsWith("---", StringComparison.Ordinal));
        if (separator < 0)
            return [];

        var columns = ColumnsOf(lines[separator]);
        if (columns.Count < 2)
            return [];

        var templates = new List<ProjectTemplateInfo>();
        foreach (string row in lines.Skip(separator + 1))
        {
            if (row.Trim().Length == 0)
                continue;

            string name = Cell(row, columns[0]);
            string shortName = Cell(row, columns[1]);
            string tags = columns.Count >= 4 ? Cell(row, columns[3]) : "";

            // A template can list several short names ("webapp,razor"); the first is canonical.
            shortName = shortName.Split(',')[0].Trim();
            if (name.Length > 0 && shortName.Length > 0)
                templates.Add(new ProjectTemplateInfo(name, shortName, tags));
        }

        return [.. templates];
    }

    private static List<(int Start, int Length)> ColumnsOf(string separator)
    {
        var columns = new List<(int, int)>();
        int index = 0;

        while (index < separator.Length)
        {
            while (index < separator.Length && separator[index] != '-')
                index++;
            int start = index;
            while (index < separator.Length && separator[index] == '-')
                index++;
            if (index > start)
                columns.Add((start, index - start));
        }

        return columns;
    }

    private static string Cell(string row, (int Start, int Length) column)
    {
        if (column.Start >= row.Length)
            return "";
        // The last column runs to the end of the line, and any cell may be wider than its rule.
        int length = Math.Min(column.Length + 2, row.Length - column.Start);
        return row.Substring(column.Start, length).Trim();
    }

    private static async Task<List<string>> RunAsync(string arguments, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo("dotnet", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
                return [];

            var output = new StringBuilder();
            var read = process.StandardOutput.ReadToEndAsync(ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(timeout.Token);
            output.Append(await read);

            return [.. output.ToString().Split('\n').Select(l => l.TrimEnd('\r'))];
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ServiceLog.Warn($"`dotnet {arguments}` did not finish in time.", key: $"dotnet:{arguments}");
            return [];
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"Could not run `dotnet {arguments}`: {ex.Message}", key: $"dotnet:{arguments}");
            return [];
        }
    }
}
