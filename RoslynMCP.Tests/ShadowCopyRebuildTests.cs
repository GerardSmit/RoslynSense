using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// What the analyzer-directory watcher counts as a rebuild.
/// </summary>
/// <remarks>
/// The watcher fires on writes, and a build rewrites a project-referenced analyzer's DLL whether or
/// not the analyzer changed. Every one of those writes used to evict every workspace that had
/// pinned the directory, so building anything meant the solution reloaded from MSBuild afterwards —
/// the reported "everything reloads constantly". The compiler is deterministic for unchanged input,
/// so content is what separates a real rebuild from a rewrite of the same bytes.
/// </remarks>
/// <remarks>
/// Serialized with the rest: constructing a <see cref="ShadowCopyManager"/> touches process-wide
/// state — it cleans up other instances' shadow directories on the way up and deletes its own on
/// the way down — so running it beside the workspace tests pulls analyzer copies out from under
/// them.
/// </remarks>
[Collection(SharedState.Name)]
public class ShadowCopyRebuildTests
{
    [Fact]
    public async Task RewritingAnAnalyzerWithIdenticalContentIsNotARebuild()
    {
        await using var fixture = await WatchedAnalyzerDirectory.CreateAsync([1, 2, 3, 4]);

        // What an incremental build does: same input, same output, new timestamp.
        await fixture.WriteDllAsync([1, 2, 3, 4]);

        Assert.False(await fixture.SawChangeAsync());
    }

    [Fact]
    public async Task RewritingAnAnalyzerWithDifferentContentIsARebuild()
    {
        await using var fixture = await WatchedAnalyzerDirectory.CreateAsync([1, 2, 3, 4]);

        await fixture.WriteDllAsync([9, 9, 9, 9, 9]);

        // The other half: a real rebuild still has to evict, or the editor keeps reporting
        // diagnostics from the analyzer the user just changed.
        Assert.True(await fixture.SawChangeAsync());
    }

    /// <summary>A temp directory shadow-copied and watched, with the change signal captured.</summary>
    private sealed class WatchedAnalyzerDirectory : IAsyncDisposable
    {
        private readonly ShadowCopyManager _manager;
        private readonly string _directory;
        private readonly string _dll;
        private readonly TaskCompletionSource<string> _changed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private WatchedAnalyzerDirectory(ShadowCopyManager manager, string directory, string dll)
        {
            _manager = manager;
            _directory = directory;
            _dll = dll;
            _manager.AnalyzerDirectoryChanged += OnChanged;
        }

        public static async Task<WatchedAnalyzerDirectory> CreateAsync(byte[] content)
        {
            string directory = Path.Combine(
                Path.GetTempPath(), $"roslyn-sense-analyzer-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);

            string dll = Path.Combine(directory, "Fake.Analyzer.dll");
            await File.WriteAllBytesAsync(dll, content);

            // Without the stale-instance cleanup: it deletes shadow copies belonging to other
            // instances, which in a test process means the live manager the rest of the suite is
            // loading analyzers and source generators through.
            var manager = new ShadowCopyManager(cleanupStaleInstances: false);
            var fixture = new WatchedAnalyzerDirectory(manager, directory, dll);

            // Arms the watcher and records the baseline fingerprint.
            manager.GetLoadPath(dll);
            return fixture;
        }

        public async Task WriteDllAsync(byte[] content)
        {
            // Distinct from the baseline write, so the watcher has something to report even when
            // the bytes are the same.
            await Task.Delay(50);
            await File.WriteAllBytesAsync(_dll, content);
        }

        /// <summary>
        /// Whether a rebuild was reported. Waits past the manager's own quiet period, so a "no"
        /// means the signal was suppressed rather than merely late.
        /// </summary>
        public async Task<bool> SawChangeAsync() =>
            await Task.WhenAny(_changed.Task, Task.Delay(TimeSpan.FromSeconds(5))) == _changed.Task;

        private void OnChanged(string directory)
        {
            if (string.Equals(directory, _directory, StringComparison.OrdinalIgnoreCase))
                _changed.TrySetResult(directory);
        }

        public ValueTask DisposeAsync()
        {
            _manager.AnalyzerDirectoryChanged -= OnChanged;
            _manager.Dispose();
            try { Directory.Delete(_directory, recursive: true); }
            catch { /* best effort */ }
            return ValueTask.CompletedTask;
        }
    }
}
