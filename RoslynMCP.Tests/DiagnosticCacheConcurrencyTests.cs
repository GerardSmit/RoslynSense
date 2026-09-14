using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Config;
using RoslynMCP.Lsp;
using Xunit;

namespace RoslynMCP.Tests;

[Collection(SharedState.Name)]
public sealed class DiagnosticCacheConcurrencyTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly AdhocWorkspace _workspace = new();
    private readonly bool _analyzers = LspFeatureOptions.AnalyzerDiagnostics;
    private readonly bool _codeStyle = LspFeatureOptions.CodeStyleDiagnostics;

    public DiagnosticCacheConcurrencyTests()
    {
        LspFeatureOptions.AnalyzerDiagnostics = true;
        LspFeatureOptions.CodeStyleDiagnostics = false;
        AnalyzerDiagnosticCache.Clear();
        ProjectWideDiagnosticCache.Clear();
    }

    public void Dispose()
    {
        CompilerDiagnosticCache.BeforeComputeAsyncForTesting = null;
        CompilerDiagnosticCache.BeforeRetireAsyncForTesting = null;
        CompilerDiagnosticCache.WaitForRetirementAsyncForTesting = null;
        AnalyzerDiagnosticCache.BeforeComputeAsyncForTesting = null;
        AnalyzerDiagnosticCache.BeforeRetireAsyncForTesting = null;
        AnalyzerDiagnosticCache.WaitForRetirementAsyncForTesting = null;
        ProjectWideDiagnosticCache.BeforeComputeAsyncForTesting = null;
        AnalyzerDiagnosticCache.Clear();
        ProjectWideDiagnosticCache.Clear();
        LspFeatureOptions.AnalyzerDiagnostics = _analyzers;
        LspFeatureOptions.CodeStyleDiagnostics = _codeStyle;
        _workspace.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LastCanceledWaiterStopsQueuedWorkAndSameVersionCanRetry(bool analyzer)
    {
        var document = CreateDocument();
        var entered = Signal();
        var release = Signal();
        int computations = 0;
        SetComputeHook(analyzer, _ =>
        {
            if (Interlocked.Increment(ref computations) != 1)
                return Task.CompletedTask;
            entered.TrySetResult();
            return release.Task;
        });

        using var cancellation = new CancellationTokenSource();
        var first = ComputeAsync(analyzer, document, cancellation.Token);
        await entered.Task.WaitAsync(Timeout);
        try
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(Timeout));

            // The original queue gate is still closed. Success proves that its abandoned
            // flight exited on cancellation, and the next caller got fresh, uncanceled work.
            await ComputeAsync(analyzer, document, default).WaitAsync(Timeout);
            Assert.Equal(2, computations);
            Assert.False(release.Task.IsCompleted);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InvalidationStopsQueuedWorkWithoutRetryingOldSnapshot(bool analyzer, bool evict)
    {
        var document = CreateDocument();
        var entered = Signal();
        var release = Signal();
        int computations = 0;
        SetComputeHook(analyzer, _ =>
        {
            Interlocked.Increment(ref computations);
            entered.TrySetResult();
            return release.Task;
        });
        var pending = ComputeAsync(analyzer, document, default);
        await entered.Task.WaitAsync(Timeout);
        try
        {
            if (evict)
                AnalyzerDiagnosticCache.Evict(document.Id);
            else
                AnalyzerDiagnosticCache.Clear();

            await pending.WaitAsync(Timeout);
            Assert.Equal(1, computations);
            Assert.False(release.Task.IsCompleted);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedOlderSnapshotDoesNotCancelAStillRequestedNewerSnapshot(bool analyzer)
    {
        var original = CreateDocument();
        var newer = original.WithText(SourceText.From("class C { public int Value = 1; }"));
        var entered = Signal();
        var release = Signal();
        SetComputeHook(analyzer, document =>
        {
            if (!ReferenceEquals(document, newer))
                return Task.CompletedTask;
            entered.TrySetResult();
            return release.Task;
        });

        var pending = ComputeAsync(analyzer, newer, default);
        await entered.Task.WaitAsync(Timeout);
        try
        {
            await ComputeAsync(analyzer, original, default).WaitAsync(Timeout);
            Assert.False(pending.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await pending.WaitAsync(Timeout);
    }

    private static Task ComputeAsync(bool analyzer, Document document, CancellationToken ct) => analyzer
        ? AnalyzerDiagnosticCache.GetOrComputeAsync(document, ct)
        : CompilerDiagnosticCache.GetOrComputeAsync(document, ct);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InvalidationAfterRetirementPreventsRetryIntoNewCache(bool analyzer, bool evict)
    {
        var document = CreateDocument();
        var started = Signal();
        var releaseComputation = Signal();
        var retiring = Signal();
        var releaseRetirement = Signal();
        var waiting = Signal();
        var readyToRetry = Signal();
        var releaseRetry = Signal();
        int computations = 0;
        SetComputeHook(analyzer, _ =>
        {
            Interlocked.Increment(ref computations);
            started.TrySetResult();
            return releaseComputation.Task;
        });
        Task BeforeRetire()
        {
            retiring.TrySetResult();
            return releaseRetirement.Task;
        }
        async Task WaitForRetirement(Task computation)
        {
            waiting.TrySetResult();
            await computation;
            readyToRetry.TrySetResult();
            await releaseRetry.Task;
        }
        if (analyzer)
        {
            AnalyzerDiagnosticCache.BeforeRetireAsyncForTesting = BeforeRetire;
            AnalyzerDiagnosticCache.WaitForRetirementAsyncForTesting = WaitForRetirement;
        }
        else
        {
            CompilerDiagnosticCache.BeforeRetireAsyncForTesting = BeforeRetire;
            CompilerDiagnosticCache.WaitForRetirementAsyncForTesting = WaitForRetirement;
        }

        using var cancellation = new CancellationTokenSource();
        var first = ComputeAsync(analyzer, document, cancellation.Token);
        Task? retry = null;
        try
        {
            await started.Task.WaitAsync(Timeout);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(Timeout));
            await retiring.Task.WaitAsync(Timeout);
            retry = ComputeAsync(analyzer, document, default);
            await waiting.Task.WaitAsync(Timeout);
            releaseRetirement.TrySetResult();
            await readyToRetry.Task.WaitAsync(Timeout);

            // The first flight has been removed, so invalidation cannot mark that flight.
            // The retry must still notice invalidation before registering its old snapshot.
            if (evict)
                AnalyzerDiagnosticCache.Evict(document.Id);
            else
                AnalyzerDiagnosticCache.Clear();
            releaseRetry.TrySetResult();
            await retry.WaitAsync(Timeout);
            Assert.Equal(1, computations);
        }
        finally
        {
            cancellation.Cancel();
            releaseComputation.TrySetResult();
            releaseRetirement.TrySetResult();
            releaseRetry.TrySetResult();
            if (retry is not null)
                await retry.WaitAsync(Timeout);
        }
    }

    private static void SetComputeHook(bool analyzer, Func<Document, Task> hook)
    {
        if (analyzer)
            AnalyzerDiagnosticCache.BeforeComputeAsyncForTesting = hook;
        else
            CompilerDiagnosticCache.BeforeComputeAsyncForTesting = hook;
    }

    [Fact]
    public async Task CancelingAnalyzerRequesterDoesNotCancelSharedAnalysis()
    {
        var document = CreateDocument();
        var entered = Signal();
        var release = Signal();
        int computations = 0;
        AnalyzerDiagnosticCache.BeforeComputeAsyncForTesting = _ =>
        {
            Interlocked.Increment(ref computations);
            entered.TrySetResult();
            return release.Task;
        };
        using var cancellation = new CancellationTokenSource();
        var first = AnalyzerDiagnosticCache.GetOrComputeAsync(document, cancellation.Token);
        await entered.Task.WaitAsync(Timeout);
        var second = AnalyzerDiagnosticCache.GetOrComputeAsync(document, default);

        try
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(Timeout));
            Assert.False(second.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        await second.WaitAsync(Timeout);
        Assert.True(AnalyzerDiagnosticCache.IsComputed(document,
            await AnalyzerDiagnosticCache.GetVersionAsync(document, default)));
        Assert.Equal(1, computations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidatedAnalyzerPassCannotPublishFindingsOrAnalyzedMarker(bool evictDocument)
    {
        var document = CreateDocument();
        var version = await AnalyzerDiagnosticCache.GetVersionAsync(document, default);
        var entered = Signal();
        var release = Signal();
        AnalyzerDiagnosticCache.BeforeComputeAsyncForTesting = _ =>
        {
            entered.TrySetResult();
            return release.Task;
        };
        var pending = AnalyzerDiagnosticCache.GetOrComputeAsync(document, default);
        await entered.Task.WaitAsync(Timeout);
        if (evictDocument)
            AnalyzerDiagnosticCache.Evict(document.Id);
        else
            AnalyzerDiagnosticCache.Clear();
        release.TrySetResult();
        await pending.WaitAsync(Timeout);

        Assert.False(AnalyzerDiagnosticCache.HasStoredFindings(document, version));
        Assert.False(AnalyzerDiagnosticCache.IsComputed(document, version));
        await AnalyzerDiagnosticCache.GetOrComputeAsync(document, default);
        Assert.True(AnalyzerDiagnosticCache.IsComputed(document, version));
    }

    [Fact]
    public async Task OlderAnalyzerPassCannotReplaceNewerFindingsOrAnalyzedMarker()
    {
        var original = CreateDocument();
        var edited = original.WithText(SourceText.From("class C { public int Value = 1; }"));
        var originalVersion = await AnalyzerDiagnosticCache.GetVersionAsync(original, default);
        var editedVersion = await AnalyzerDiagnosticCache.GetVersionAsync(edited, default);
        var entered = Signal();
        var release = Signal();
        AnalyzerDiagnosticCache.BeforeComputeAsyncForTesting = document =>
        {
            if (!ReferenceEquals(document, original))
                return Task.CompletedTask;
            entered.TrySetResult();
            return release.Task;
        };

        var pending = AnalyzerDiagnosticCache.GetOrComputeAsync(original, default);
        await entered.Task.WaitAsync(Timeout);
        try
        {
            await AnalyzerDiagnosticCache.GetOrComputeAsync(edited, default).WaitAsync(Timeout);
        }
        finally
        {
            release.TrySetResult();
        }
        await pending.WaitAsync(Timeout);

        Assert.True(AnalyzerDiagnosticCache.HasStoredFindings(edited, editedVersion));
        Assert.True(AnalyzerDiagnosticCache.IsComputed(edited, editedVersion));
        Assert.False(AnalyzerDiagnosticCache.IsComputed(original, originalVersion));
    }

    [Fact]
    public async Task CancelingCompilerRequesterDoesNotCancelSharedBind()
    {
        var document = CreateDocument();
        var entered = Signal();
        var release = Signal();
        int computations = 0;
        CompilerDiagnosticCache.BeforeComputeAsyncForTesting = _ =>
        {
            Interlocked.Increment(ref computations);
            entered.TrySetResult();
            return release.Task;
        };
        using var cancellation = new CancellationTokenSource();
        var first = CompilerDiagnosticCache.GetOrComputeAsync(document, cancellation.Token);
        await entered.Task.WaitAsync(Timeout);
        var second = CompilerDiagnosticCache.GetOrComputeAsync(document, default);

        try
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(Timeout));
            Assert.False(second.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        var result = await second.WaitAsync(Timeout);
        Assert.Same(result, await CompilerDiagnosticCache.GetOrComputeAsync(document, default));
        Assert.Equal(1, computations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidatedCompilerBindCannotOverwriteOrRemoveItsReplacement(bool evictDocument)
    {
        var document = CreateDocument();
        var firstEntered = Signal();
        var secondEntered = Signal();
        var releaseFirst = Signal();
        var releaseSecond = Signal();
        int computations = 0;
        CompilerDiagnosticCache.BeforeComputeAsyncForTesting = _ =>
        {
            if (Interlocked.Increment(ref computations) == 1)
            {
                firstEntered.TrySetResult();
                return releaseFirst.Task;
            }

            secondEntered.TrySetResult();
            return releaseSecond.Task;
        };

        var first = CompilerDiagnosticCache.GetOrComputeAsync(document, default);
        await firstEntered.Task.WaitAsync(Timeout);
        if (evictDocument)
            CompilerDiagnosticCache.Evict(document.Id);
        else
            CompilerDiagnosticCache.Clear();
        var second = CompilerDiagnosticCache.GetOrComputeAsync(document, default);

        try
        {
            await secondEntered.Task.WaitAsync(Timeout);
            releaseFirst.TrySetResult();
            await first.WaitAsync(Timeout);

            var third = CompilerDiagnosticCache.GetOrComputeAsync(document, default);
            Assert.False(third.IsCompleted);
            releaseSecond.TrySetResult();
            Assert.Same(await second.WaitAsync(Timeout), await third.WaitAsync(Timeout));
            Assert.Equal(2, computations);
        }
        finally
        {
            releaseFirst.TrySetResult();
            releaseSecond.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task CancelingProjectSweepKeepsItsCompilationAvailableToNextSweep()
    {
        var project = CreateDocument().Project;
        var entered = Signal();
        var release = Signal();
        int computations = 0;
        ProjectWideDiagnosticCache.BeforeComputeAsyncForTesting = _ =>
        {
            Interlocked.Increment(ref computations);
            entered.TrySetResult();
            return release.Task;
        };
        using var cancellation = new CancellationTokenSource();
        var first = ProjectWideDiagnosticCache.RefreshAsync(project, cancellation.Token);
        await entered.Task.WaitAsync(Timeout);

        Task<bool>? second = null;
        try
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(Timeout));
            second = ProjectWideDiagnosticCache.RefreshAsync(project, default);
            Assert.False(second.IsCompleted);
            Assert.Equal(1, computations);
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.True(await second!.WaitAsync(Timeout));
        Assert.Equal(1, computations);
        Assert.False(await ProjectWideDiagnosticCache.RefreshAsync(project, default));
    }

    [Fact]
    public async Task OlderProjectCompilationCannotReplaceNewerDiagnostics()
    {
        var original = CreateDocument();
        var edited = original.WithText(SourceText.From("class C { public int Value = 1; }"));
        var originalVersion = await ProjectWideDiagnosticCache.GetVersionAsync(original.Project, default);
        var editedVersion = await ProjectWideDiagnosticCache.GetVersionAsync(edited.Project, default);
        Assert.NotEqual(originalVersion, editedVersion);

        var entered = Signal();
        var release = Signal();
        ProjectWideDiagnosticCache.BeforeComputeAsyncForTesting = project =>
        {
            if (!ReferenceEquals(project, original.Project))
                return Task.CompletedTask;
            entered.TrySetResult();
            return release.Task;
        };

        var first = ProjectWideDiagnosticCache.RefreshAsync(original.Project, default);
        await entered.Task.WaitAsync(Timeout);
        try
        {
            await ProjectWideDiagnosticCache.RefreshAsync(edited.Project, default).WaitAsync(Timeout);
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.False(await first.WaitAsync(Timeout));
        Assert.True(ProjectWideDiagnosticCache.IsComputed(edited.Project, editedVersion));
        Assert.False(ProjectWideDiagnosticCache.IsComputed(original.Project, originalVersion));
        Assert.Empty(ProjectWideDiagnosticCache.TryGetAnyVersion(edited.Project, edited.FilePath));
    }

    [Fact]
    public async Task ClearingProjectCacheDiscardsPendingCompilation()
    {
        var project = CreateDocument().Project;
        var version = await ProjectWideDiagnosticCache.GetVersionAsync(project, default);
        var entered = Signal();
        var release = Signal();
        ProjectWideDiagnosticCache.BeforeComputeAsyncForTesting = _ =>
        {
            entered.TrySetResult();
            return release.Task;
        };
        var pending = ProjectWideDiagnosticCache.RefreshAsync(project, default);
        await entered.Task.WaitAsync(Timeout);
        ProjectWideDiagnosticCache.Clear();
        release.TrySetResult();

        Assert.False(await pending.WaitAsync(Timeout));
        Assert.False(ProjectWideDiagnosticCache.IsComputed(project, version));
        Assert.Empty(ProjectWideDiagnosticCache.TryGetAnyVersion(project, "CacheTest.cs"));
        Assert.True(await ProjectWideDiagnosticCache.RefreshAsync(project, default));
    }

    private Document CreateDocument()
    {
        var project = _workspace.AddProject("DiagnosticCacheTest", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReference(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        return project.AddDocument("CacheTest.cs", SourceText.From("class C { private int unused; }"),
            filePath: "CacheTest.cs");
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
