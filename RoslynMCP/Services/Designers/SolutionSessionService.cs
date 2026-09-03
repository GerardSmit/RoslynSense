using System.Collections.Concurrent;

namespace RoslynMCP.Services.Designers;

/// <summary>A designer regeneration triggered by the watcher rather than by a tool call.</summary>
public sealed record WatchedRegeneration(
    string SourcePath,
    DesignerOutcome Outcome,
    DateTime AtUtc,
    IReadOnlyList<string> Errors)
{
    /// <summary>
    /// The generated file, so a subscriber knows which document changed without having to ask a
    /// generator again. Empty when generation failed before it settled on one.
    /// </summary>
    public string DesignerPath { get; init; } = "";
}

/// <summary>
/// Tracks the currently open solution and, optionally, watches it so generated designer files stay
/// in step with the markup they come from.
/// </summary>
/// <remarks>
/// <para>
/// This deliberately lives at solution scope rather than per chat: regenerating a designer file is
/// a side effect on the shared source tree, so several chats each running their own watcher would
/// duplicate the work and race on the same files.
/// </para>
/// <para>
/// The watcher only reacts to markup and model files, never to <c>.cs</c>, so writing a designer
/// file cannot retrigger it.
/// </para>
/// </remarks>
public sealed class SolutionSessionService(DesignerRegenerationService regeneration) : IDisposable
{
    /// <summary>
    /// How long to wait after the last change to a file before regenerating. Editors commonly
    /// write a file in several bursts, and each burst raises its own event.
    /// </summary>
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>How many recent regenerations to keep for reporting.</summary>
    private const int HistoryLimit = 50;

    private readonly Lock _gate = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly KeyedDebouncer _pending = new("SolutionSession");
    private readonly ConcurrentQueue<WatchedRegeneration> _history = new();

    public string? SolutionPath { get; private set; }
    public bool IsWatching { get; private set; }

    /// <summary>Most recent watcher-driven regenerations, newest last.</summary>
    public IReadOnlyList<WatchedRegeneration> History => [.. _history];

    /// <summary>
    /// Raised after the watcher rewrote a designer file — the only outcome that changes what a
    /// compilation, and therefore an editor, should be seeing.
    /// </summary>
    /// <remarks>
    /// An event rather than a direct call into anything: writing generated files is this
    /// service's whole job, and it has to keep doing it identically whether an editor is
    /// connected, several are, or none is. Handlers run on the watcher's pool thread.
    /// </remarks>
    public event Action<WatchedRegeneration>? Regenerated;

    /// <summary>Number of regenerations queued but not yet applied.</summary>
    public int PendingCount => _pending.PendingCount;

    public void Open(string solutionPath, IEnumerable<string> projectDirectories, bool watch)
    {
        lock (_gate)
        {
            // Reopening what is already open must not restart the watchers: the editor arms them
            // at initialize, and an MCP open_solution arriving afterwards would otherwise tear
            // them down mid-debounce and lose whatever regeneration was pending.
            if (string.Equals(SolutionPath, solutionPath, StringComparison.OrdinalIgnoreCase)
                && IsWatching == watch)
            {
                return;
            }

            StopWatchersLocked();

            SolutionPath = solutionPath;
            IsWatching = false;

            if (!watch)
                return;

            foreach (var directory in projectDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
                TryWatchLocked(directory);

            IsWatching = _watchers.Count > 0;
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            StopWatchersLocked();
            SolutionPath = null;
            IsWatching = false;
            _history.Clear();
        }
    }

    private void TryWatchLocked(string directory)
    {
        if (!Directory.Exists(directory))
            return;

        try
        {
            var watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };

            watcher.Changed += (_, e) => OnChanged(e.FullPath);
            watcher.Created += (_, e) => OnChanged(e.FullPath);
            watcher.Renamed += (_, e) => OnChanged(e.FullPath);

            // Deletions are ignored: removing markup does not imply the designer should be
            // rewritten, and deleting a generated file the user may still need is not this
            // service's call to make.
            _watchers.Add(watcher);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SolutionSession] Could not watch '{directory}': {ex.Message}");
        }
    }

    private void StopWatchersLocked()
    {
        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch
            {
                // A watcher that is already gone needs no further cleanup.
            }
        }

        _watchers.Clear();

        _pending.CancelAll();
    }

    private void OnChanged(string path)
    {
        if (IsBuildOutput(path) || !regeneration.IsGeneratedFrom(path))
            return;

        // Restart this file's debounce window; the previous pending run is superseded.
        _pending.Restart(path, DebounceDelay, async ct =>
        {
            try
            {
                var result = await regeneration.RegenerateAsync(path, dryRun: false, ct);
                Publish(new WatchedRegeneration(path, result.Outcome, DateTime.UtcNow, result.Errors)
                {
                    DesignerPath = result.DesignerPath,
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed regeneration is an answer the session should surface, not a log line.
                Publish(new WatchedRegeneration(path, DesignerOutcome.Failed, DateTime.UtcNow, [ex.Message]));
            }
        });
    }

    private void Publish(WatchedRegeneration entry)
    {
        Record(entry);

        if (entry.Outcome != DesignerOutcome.Updated)
            return;

        try
        {
            Regenerated?.Invoke(entry);
        }
        catch (Exception ex)
        {
            // For the same reason the regeneration itself is wrapped: this is still the watcher's
            // pool thread, and a subscriber's failure is not worth the process.
            Console.Error.WriteLine($"[SolutionSession] A regeneration handler failed: {ex.Message}");
        }
    }

    private void Record(WatchedRegeneration entry)
    {
        // Unchanged results are the steady state and would crowd out anything worth reporting.
        if (entry.Outcome == DesignerOutcome.Unchanged)
            return;

        _history.Enqueue(entry);
        while (_history.Count > HistoryLimit)
            _history.TryDequeue(out _);
    }

    private static bool IsBuildOutput(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment =>
                segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("bin", StringComparison.OrdinalIgnoreCase));

    public void Dispose() => Close();
}
