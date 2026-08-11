using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace RoslynMCP.Services.Testing;

/// <summary>Which revision the working tree is compared against.</summary>
public enum GitChangeScope
{
    /// <summary>Everything not committed: staged and unstaged, plus untracked files.</summary>
    Uncommitted,

    /// <summary>Everything this branch changed — its merge base with the main branch through the
    /// working tree, so committed work counts too.</summary>
    Branch,

    /// <summary>A caller-supplied revision or range.</summary>
    Ref,
}

/// <summary>One file the diff touched, with the lines it touched in the file as it is now.</summary>
/// <param name="Ranges">
/// Inclusive 1-based line ranges in the <em>new</em> file. Empty means "the whole file" — a new,
/// deleted, or untracked file, where asking which lines changed has no useful answer.
/// </param>
public sealed record ChangedFile(string FilePath, IReadOnlyList<LineRange> Ranges)
{
    public bool WholeFile => Ranges.Count == 0;

    public bool Touches(int line) =>
        WholeFile || Ranges.Any(r => line >= r.Start && line <= r.End);

    public bool TouchesAny(IEnumerable<int> lines) =>
        WholeFile || lines.Any(Touches);
}

public readonly record struct LineRange(int Start, int End);

/// <summary>What the diff said, or why it could not be taken.</summary>
public sealed record GitChangeSet(
    IReadOnlyList<ChangedFile> Files,
    string Description,
    string? Error = null)
{
    public static GitChangeSet Failed(string error) => new([], "", error);
}

/// <summary>
/// Reads the working copy's own diff, so a test run can be scoped to what the user actually
/// changed. Shells out to git rather than linking a git library: the repository may be in any
/// state git supports, and the porcelain output for <c>diff --unified=0</c> is the one contract
/// that does not move.
/// </summary>
public static partial class GitChangeService
{
    /// <summary>Branch names tried, in order, when resolving the merge base for
    /// <see cref="GitChangeScope.Branch"/>.</summary>
    private static readonly string[] s_mainBranches =
        ["origin/main", "origin/master", "main", "master"];

    public static async Task<GitChangeSet> GetChangesAsync(
        string anchorPath,
        GitChangeScope scope = GitChangeScope.Uncommitted,
        string? reference = null,
        CancellationToken ct = default)
    {
        string? repository = FindRepositoryRoot(anchorPath);
        if (repository is null)
            return GitChangeSet.Failed($"'{anchorPath}' is not inside a git repository.");

        string? diffTarget;
        string description;

        switch (scope)
        {
            case GitChangeScope.Uncommitted:
                diffTarget = "HEAD";
                description = "uncommitted changes (staged and unstaged) against HEAD";
                break;

            case GitChangeScope.Branch:
                string? mergeBase = await FindMergeBaseAsync(repository, ct);
                if (mergeBase is null)
                {
                    return GitChangeSet.Failed(
                        "Could not find a merge base with a main branch. " +
                        "Pass an explicit reference instead.");
                }
                diffTarget = mergeBase;
                description = $"the whole branch against its merge base ({mergeBase[..Math.Min(8, mergeBase.Length)]})";
                break;

            default:
                if (string.IsNullOrWhiteSpace(reference))
                    return GitChangeSet.Failed("No git reference was given.");
                diffTarget = reference;
                description = $"changes against {reference}";
                break;
        }

        // --unified=0 keeps the hunk headers to exactly the changed lines; with context they
        // would name lines nobody touched and over-select tests.
        var (exitCode, stdout, stderr) = await RunGitAsync(
            repository, $"diff --unified=0 --no-color --no-ext-diff \"{diffTarget}\" --", ct);

        if (exitCode != 0)
            return GitChangeSet.Failed($"git diff failed: {Summarize(stderr, stdout)}");

        var files = ParseUnifiedDiff(stdout, repository).ToDictionary(
            f => f.FilePath, StringComparer.OrdinalIgnoreCase);

        // Untracked files never appear in a diff, and a brand new source file is exactly the
        // change most worth running tests for. It has no "before", so the whole file is changed.
        if (scope == GitChangeScope.Uncommitted)
        {
            var (untrackedExit, untrackedOut, _) = await RunGitAsync(
                repository, "ls-files --others --exclude-standard", ct);

            if (untrackedExit == 0)
            {
                foreach (string line in untrackedOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    string path = Path.GetFullPath(Path.Combine(repository, line.Trim()));
                    if (path.Length > 0 && !files.ContainsKey(path))
                        files[path] = new ChangedFile(path, []);
                }
            }
        }

        return new GitChangeSet(
            files.Values.OrderBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase).ToList(),
            description);
    }

    /// <summary>
    /// Turns <c>git diff --unified=0</c> output into per-file line ranges.
    /// </summary>
    /// <remarks>
    /// Only the new-side of each hunk header matters: a range that is purely a deletion has a
    /// zero length and is recorded as the single line it collapsed onto, because that line is
    /// where the change is visible now. Renames and deletions of the whole file arrive as a
    /// header with no hunks and are reported as whole-file changes.
    /// </remarks>
    internal static IReadOnlyList<ChangedFile> ParseUnifiedDiff(string diff, string repositoryRoot)
    {
        var files = new List<ChangedFile>();
        string? currentPath = null;
        List<LineRange>? currentRanges = null;
        bool currentIsWholeFile = false;

        void Flush()
        {
            if (currentPath is null)
                return;
            files.Add(new ChangedFile(
                currentPath, currentIsWholeFile ? [] : currentRanges ?? []));
            currentPath = null;
            currentRanges = null;
            currentIsWholeFile = false;
        }

        foreach (string raw in diff.Split('\n'))
        {
            string line = raw.TrimEnd('\r');

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                Flush();
                continue;
            }

            // "+++ b/path/to/file" names the new side. /dev/null means the file was deleted;
            // nothing in it can be covered, so it is dropped rather than recorded.
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                string path = line[4..].Trim();
                if (path == "/dev/null")
                {
                    currentPath = null;
                    continue;
                }
                if (path.StartsWith("b/", StringComparison.Ordinal))
                    path = path[2..];

                currentPath = Path.GetFullPath(Path.Combine(repositoryRoot, path));
                currentRanges = [];
                currentIsWholeFile = false;
                continue;
            }

            // A file added or renamed wholesale: git reports it without per-line hunks when the
            // diff is binary or the rename is exact.
            if (currentPath is not null &&
                (line.StartsWith("Binary files ", StringComparison.Ordinal) ||
                 line.StartsWith("rename to ", StringComparison.Ordinal)))
            {
                currentIsWholeFile = true;
                continue;
            }

            if (currentPath is null || !line.StartsWith("@@", StringComparison.Ordinal))
                continue;

            var match = HunkHeader().Match(line);
            if (!match.Success)
                continue;

            int start = int.Parse(match.Groups["start"].Value);
            int count = match.Groups["count"].Success ? int.Parse(match.Groups["count"].Value) : 1;

            currentRanges ??= [];
            currentRanges.Add(count == 0
                ? new LineRange(Math.Max(1, start), Math.Max(1, start))
                : new LineRange(start, start + count - 1));
        }

        Flush();
        return files;
    }

    /// <summary>The nearest enclosing directory that git calls a work tree.</summary>
    public static string? FindRepositoryRoot(string anchorPath)
    {
        string? directory = Directory.Exists(anchorPath)
            ? Path.GetFullPath(anchorPath)
            : Path.GetDirectoryName(Path.GetFullPath(anchorPath));

        while (!string.IsNullOrEmpty(directory))
        {
            // A worktree and a submodule have .git as a file, not a directory.
            if (Directory.Exists(Path.Combine(directory, ".git"))
                || File.Exists(Path.Combine(directory, ".git")))
                return directory;

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    private static async Task<string?> FindMergeBaseAsync(string repository, CancellationToken ct)
    {
        foreach (string branch in s_mainBranches)
        {
            var (exitCode, stdout, _) = await RunGitAsync(repository, $"merge-base HEAD {branch}", ct);
            if (exitCode == 0 && stdout.Trim() is { Length: > 0 } sha)
                return sha.Trim();
        }
        return null;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunGitAsync(
        string workingDirectory, string arguments, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo("git", arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };

        try
        {
            using var process = new Process { StartInfo = startInfo };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (-1, stdout.ToString(), "git did not finish in time.");
            }

            return (process.ExitCode, stdout.ToString(), stderr.ToString());
        }
        catch (Exception ex)
        {
            // Most often: git is not installed, or not on PATH.
            return (-1, "", ex.Message);
        }
    }

    private static string Summarize(string stderr, string stdout)
    {
        string text = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        return text.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "unknown error";
    }

    /// <summary>"@@ -12,3 +45,7 @@" — only the new side is captured.</summary>
    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(?<start>\d+)(?:,(?<count>\d+))? @@")]
    private static partial Regex HunkHeader();
}
