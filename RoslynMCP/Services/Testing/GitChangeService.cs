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
/// <param name="UnstagedRanges">
/// The lines that still differ from the index — the part of the change not staged yet, in the
/// same coordinates as <paramref name="Ranges"/>. An empty list means the whole change is
/// staged; <see langword="null"/> means nobody asked, and nothing counts as staged.
/// </param>
/// <param name="RemovedRanges">
/// The old side of every hunk that removed or replaced lines — what a consumer needs to name
/// what a deletion deleted. <see langword="null"/> when the diff source carries no old side.
/// </param>
/// <param name="Deleted">Whether the whole file is gone; <see cref="FilePath"/> then names
/// where it was, and <see cref="RemovedRanges"/> covers everything it held.</param>
public sealed record ChangedFile(
    string FilePath,
    IReadOnlyList<LineRange> Ranges,
    IReadOnlyList<LineRange>? UnstagedRanges = null,
    IReadOnlyList<RemovedRange>? RemovedRanges = null,
    bool Deleted = false)
{
    /// <summary>Stands in for "all of it" where a file has no per-line answer to give.</summary>
    public static readonly IReadOnlyList<LineRange> Everything = [new LineRange(1, int.MaxValue)];

    public bool WholeFile => Ranges.Count == 0;

    public bool Touches(int line) =>
        WholeFile || Ranges.Any(r => line >= r.Start && line <= r.End);

    public bool TouchesAny(IEnumerable<int> lines) =>
        WholeFile || lines.Any(Touches);

    /// <summary>
    /// Whether every line of the given run is already staged — the run is part of the change
    /// against HEAD, and no part of it is still dirty against the index.
    /// </summary>
    public bool IsStaged(int start, int end) =>
        UnstagedRanges is { } unstaged && !unstaged.Any(r => r.Start <= end && r.End >= start);

    /// <summary>Whether the file's whole change is staged.</summary>
    public bool IsFullyStaged => IsStaged(1, int.MaxValue);
}

public readonly record struct LineRange(int Start, int End);

/// <summary>One hunk's old side: the base-revision lines it removed or replaced (inclusive,
/// 1-based), and the new-file line the change is visible at now.</summary>
public readonly record struct RemovedRange(int OldStart, int OldEnd, int NewLine);

/// <summary>What the diff said, or why it could not be taken.</summary>
/// <param name="DiffTarget">The revision the working tree was compared against — "HEAD", a
/// merge-base sha, or the caller's reference. What a client needs to show the same diff.</param>
public sealed record GitChangeSet(
    IReadOnlyList<ChangedFile> Files,
    string Description,
    string? Error = null,
    string? DiffTarget = null)
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

        // Which part of the change is staged, so a client can treat staging as "reviewed".
        // Only for the uncommitted scope: against a merge base or an older ref, everything
        // committed would read as staged, which says nothing about whether anyone looked at it.
        if (scope == GitChangeScope.Uncommitted)
            await MarkUnstagedAsync(repository, files, ct);

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
                        files[path] = new ChangedFile(path, [], ChangedFile.Everything);
                }
            }
        }

        return new GitChangeSet(
            files.Values.OrderBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase).ToList(),
            description,
            DiffTarget: diffTarget);
    }

    /// <summary>
    /// Fills in each file's unstaged lines from the working-tree-against-index diff. Both diffs
    /// number their new side by the working tree, so the two sets of ranges line up; a file the
    /// index already matches gets an empty list, which reads as "all of it is staged".
    /// </summary>
    private static async Task MarkUnstagedAsync(
        string repository, Dictionary<string, ChangedFile> files, CancellationToken ct)
    {
        var (exitCode, stdout, _) = await RunGitAsync(
            repository, "diff --unified=0 --no-color --no-ext-diff --", ct);

        // Without an answer, nothing is claimed to be staged — the safe direction, since the
        // client uses staged-ness to hide rows.
        if (exitCode != 0)
            return;

        var unstaged = ParseUnifiedDiff(stdout, repository).ToDictionary(
            f => f.FilePath, StringComparer.OrdinalIgnoreCase);

        foreach (string path in files.Keys.ToList())
        {
            files[path] = files[path] with
            {
                UnstagedRanges = unstaged.TryGetValue(path, out var dirty)
                    ? (dirty.WholeFile ? ChangedFile.Everything : dirty.Ranges)
                    : [],
            };
        }
    }

    /// <summary>
    /// Turns <c>git diff --unified=0</c> output into per-file line ranges.
    /// </summary>
    /// <remarks>
    /// The new side of each hunk header carries the ranges: a range that is purely a deletion
    /// has a zero length and is recorded as the single line it collapsed onto, because that line
    /// is where the change is visible now. The old side of every removing hunk is kept apart in
    /// <see cref="ChangedFile.RemovedRanges"/>, so a consumer can still name what a deletion
    /// deleted. A deleted file lists under its old path with everything in
    /// <see cref="ChangedFile.RemovedRanges"/>; renames arrive as a header with no hunks and are
    /// reported as whole-file changes.
    /// </remarks>
    internal static IReadOnlyList<ChangedFile> ParseUnifiedDiff(string diff, string repositoryRoot)
    {
        var files = new List<ChangedFile>();
        string? currentPath = null;
        string? currentOldPath = null;
        List<LineRange>? currentRanges = null;
        List<RemovedRange>? currentRemoved = null;
        bool currentIsWholeFile = false;
        bool currentIsDeleted = false;

        void Flush()
        {
            if (currentPath is not null)
            {
                files.Add(new ChangedFile(
                    currentPath,
                    currentIsWholeFile || currentIsDeleted ? [] : currentRanges ?? [],
                    RemovedRanges: currentRemoved,
                    Deleted: currentIsDeleted));
            }
            currentPath = null;
            currentOldPath = null;
            currentRanges = null;
            currentRemoved = null;
            currentIsWholeFile = false;
            currentIsDeleted = false;
        }

        string ToFullPath(string path) =>
            Path.GetFullPath(Path.Combine(repositoryRoot, path));

        foreach (string raw in diff.Split('\n'))
        {
            string line = raw.TrimEnd('\r');

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                Flush();
                continue;
            }

            // "--- a/path/to/file" names the old side; /dev/null means the file is new. Only a
            // header before "+++" counts — once the new side is named, a removed content line
            // could spell the same prefix.
            if (currentPath is null && !currentIsDeleted &&
                line.StartsWith("--- ", StringComparison.Ordinal))
            {
                string path = line[4..].Trim();
                currentOldPath = path == "/dev/null"
                    ? null
                    : path.StartsWith("a/", StringComparison.Ordinal) ? path[2..] : path;
                continue;
            }

            // "+++ b/path/to/file" names the new side. /dev/null means the file was deleted;
            // it lists under its old path so the deletion still has a name.
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                string path = line[4..].Trim();
                if (path == "/dev/null")
                {
                    currentPath = currentOldPath is null ? null : ToFullPath(currentOldPath);
                    currentIsDeleted = currentPath is not null;
                    continue;
                }
                if (path.StartsWith("b/", StringComparison.Ordinal))
                    path = path[2..];

                currentPath = ToFullPath(path);
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
            int oldStart = int.Parse(match.Groups["oldStart"].Value);
            int oldCount = match.Groups["oldCount"].Success
                ? int.Parse(match.Groups["oldCount"].Value)
                : 1;

            // A pure deletion collapses onto the line before it on the new side; start can be 0
            // when the removal was at the very top of the file.
            int newAnchor = count == 0 ? Math.Max(1, start) : start;

            if (!currentIsDeleted)
            {
                currentRanges ??= [];
                currentRanges.Add(count == 0
                    ? new LineRange(newAnchor, newAnchor)
                    : new LineRange(start, start + count - 1));
            }

            if (oldCount > 0)
            {
                currentRemoved ??= [];
                currentRemoved.Add(new RemovedRange(oldStart, oldStart + oldCount - 1, newAnchor));
            }
        }

        Flush();
        return files;
    }

    /// <summary>The file's content at a revision, or null when git cannot produce it — an
    /// unknown revision, or a path the revision does not have (a rename's new name).</summary>
    internal static async Task<string?> ReadFileAtAsync(
        string repository, string reference, string relativePath, CancellationToken ct)
    {
        var (exitCode, stdout, _) = await RunGitAsync(
            repository, $"show \"{reference}:{relativePath.Replace('\\', '/')}\"", ct);
        return exitCode == 0 ? stdout : null;
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

    /// <summary>"@@ -12,3 +45,7 @@" — both sides are captured.</summary>
    [GeneratedRegex(@"^@@ -(?<oldStart>\d+)(?:,(?<oldCount>\d+))? \+(?<start>\d+)(?:,(?<count>\d+))? @@")]
    private static partial Regex HunkHeader();
}
