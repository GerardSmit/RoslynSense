using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

[Collection(SharedState.Name)]
public sealed class ColdCompilationPrimerTests
{
    [Fact]
    public async Task StoppingARequestDoesNotWaitForAnUncooperativeCompilationOrReleaseItsSlotEarly()
    {
        using var workspace = new AdhocWorkspace(WorkspaceService.HostServices);
        var (root, dependencies) = CreateFanOut(workspace, 9);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        int firstCalls = 0;
        int secondCalls = 0;
        int active = 0;
        int peak = 0;
        var tokens = new ConcurrentBag<CancellationToken>();
        using var session = ColdCompilationPrimer.Start(root.Solution, dependencies.ToHashSet(), ct.Token,
            async (_, token) =>
            {
                tokens.Add(token);
                InterlockedExtensions.UpdateMax(ref peak, Interlocked.Increment(ref active));
                if (Interlocked.Increment(ref firstCalls) == ColdCompilationPrimer.MaxConcurrentRequests)
                    entered.TrySetResult();
                try { await release.Task.WaitAsync(ct.Token); } // Deliberately ignores the primer's token.
                finally { Interlocked.Decrement(ref active); }
            });
        Task? second = null;
        try
        {
            await entered.Task.WaitAsync(ct.Token);
            session.Dispose();
            Assert.False(session.Stopped.IsCompleted);
            Assert.All(tokens, token => Assert.True(token.IsCancellationRequested));
            var stopping = session.Stopped;
            session.Dispose();
            Assert.Same(stopping, session.Stopped);

            second = ColdCompilationPrimer.PrimeAsync(root, (_, _) =>
            {
                InterlockedExtensions.UpdateMax(ref peak, Interlocked.Increment(ref active));
                Interlocked.Increment(ref secondCalls);
                Interlocked.Decrement(ref active);
                return Task.CompletedTask;
            }, ct.Token);
            // Occupied slots remain occupied until the uncooperative work actually ends.
            Assert.False(second.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref secondCalls));
            release.TrySetResult();
            await session.Stopped.WaitAsync(ct.Token);
            await second.WaitAsync(ct.Token);
            Assert.Equal(ColdCompilationPrimer.MaxConcurrentRequests, firstCalls);
            Assert.Equal(9, secondCalls);
            Assert.Equal(ColdCompilationPrimer.MaxConcurrentRequests, peak);
        }
        finally
        {
            release.TrySetResult();
            session.Dispose();
            await session.Stopped;
            if (second is not null) await second;
        }
    }

    [Fact]
    public async Task OptionalFailureIsObservedAndAdmissionCanBeReused()
    {
        using var workspace = new AdhocWorkspace(WorkspaceService.HostServices);
        var (root, dependencies) = CreateFanOut(workspace, 3);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = ColdCompilationPrimer.Start(root.Solution, dependencies.ToHashSet(), default,
            (_, _) =>
            {
                entered.TrySetResult();
                throw new InvalidOperationException("Fail optional priming");
            });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        session.Dispose();
        await session.Stopped.WaitAsync(TimeSpan.FromSeconds(15));
        await ColdCompilationPrimer.PrimeAsync(root, default).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.All(dependencies, id => Assert.True(root.Solution.GetProject(id)!.TryGetCompilation(out _)));
    }

    [Fact]
    public async Task SolutionPrimingUsesTheCapturedSnapshotAndSkipsWarmProjectsAndModules()
    {
        using var workspace = new AdhocWorkspace(WorkspaceService.HostServices);
        var (root, dependencies) = CreateFanOut(workspace, 3);
        var module = ProjectId.CreateNewId();
        var unrelated = ProjectId.CreateNewId();
        var captured = AddProject(root.Solution, unrelated, "OutsideSearchScope")
            .AddProject(module, "Module", "Module", LanguageNames.CSharp)
            .WithProjectCompilationOptions(module, new CSharpCompilationOptions(OutputKind.NetModule));
        Assert.True(workspace.TryApplyChanges(captured));
        var warm = await captured.GetProject(dependencies[0])!.GetCompilationAsync();
        var visited = new ConcurrentBag<ProjectId>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var priming = ColdCompilationPrimer.PrimeSolutionAsync(captured,
            captured.ProjectIds.Where(id => id != unrelated).ToHashSet(), async (project, token) =>
        {
            Assert.Same(captured, project.Solution);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            visited.Add(project.Id);
        }, ct.Token);
        try
        {
            await entered.Task.WaitAsync(ct.Token);
            Assert.True(workspace.TryApplyChanges(workspace.CurrentSolution.RemoveProject(dependencies[1])));
            release.TrySetResult();
            await priming;
            Assert.Equal(3, visited.Count);
            Assert.Equal(3, visited.Distinct().Count());
            Assert.Contains(root.Id, visited);
            Assert.Contains(dependencies[1], visited);
            Assert.Contains(dependencies[2], visited);
            Assert.Same(warm, await captured.GetProject(dependencies[0])!.GetCompilationAsync());
            Assert.False(captured.GetProject(module)!.TryGetCompilation(out _));
            Assert.False(captured.GetProject(unrelated)!.TryGetCompilation(out _));
        }
        finally
        {
            release.TrySetResult();
            ct.Cancel();
            try { await priming; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task SynchronousDependencyWorkOverlapsWithinTheBound()
    {
        using var workspace = new AdhocWorkspace(WorkspaceService.HostServices);
        var (root, _) = CreateFanOut(workspace, 9);
        using var firstWave = new CountdownEvent(ColdCompilationPrimer.MaxConcurrentRequests);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        int active = 0;
        int peak = 0;
        int calls = 0;

        await ColdCompilationPrimer.PrimeAsync(root, (_, token) =>
        {
            int current = Interlocked.Increment(ref active);
            InterlockedExtensions.UpdateMax(ref peak, current);
            int call = Interlocked.Increment(ref calls);
            try
            {
                if (call <= ColdCompilationPrimer.MaxConcurrentRequests)
                {
                    firstWave.Signal();
                    // A plain async-method loop would wait here before starting worker two.
                    firstWave.Wait(token);
                }
                Assert.InRange(current, 1, ColdCompilationPrimer.MaxConcurrentRequests);
                return Task.CompletedTask;
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }, ct.Token);

        Assert.Equal(9, calls);
        Assert.Equal(ColdCompilationPrimer.MaxConcurrentRequests, peak);
    }

    [Fact]
    public async Task CancellationReleasesAdmissionForAnIndependentCaller()
    {
        using var workspace = new AdhocWorkspace(WorkspaceService.HostServices);
        var (root, _) = CreateFanOut(workspace, 6);
        using var firstCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var secondCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int firstCalls = 0;
        int secondCalls = 0;
        Task? second = null;
        var first = ColdCompilationPrimer.PrimeAsync(root, async (_, token) =>
        {
            if (Interlocked.Increment(ref firstCalls) == ColdCompilationPrimer.MaxConcurrentRequests)
                firstEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, firstCancellation.Token);

        try
        {
            await firstEntered.Task.WaitAsync(secondCancellation.Token);
            second = ColdCompilationPrimer.PrimeAsync(root, (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                Interlocked.Increment(ref secondCalls);
                return Task.CompletedTask;
            }, secondCancellation.Token);
            firstCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            await second;
            Assert.Equal(ColdCompilationPrimer.MaxConcurrentRequests, firstCalls);
            Assert.Equal(6, secondCalls);

            // A cancelled primer has not poisoned Roslyn's actual compilation state.
            Assert.NotNull(await root.GetCompilationAsync(secondCancellation.Token));
        }
        finally
        {
            firstCancellation.Cancel();
            secondCancellation.Cancel();
            try { await first; } catch (OperationCanceledException) { }
            if (second is not null)
                try { await second; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task PrecancellationStartsNoDependencyWork()
    {
        using var workspace = new AdhocWorkspace(WorkspaceService.HostServices);
        var (root, _) = CreateFanOut(workspace, 3);
        int calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ColdCompilationPrimer.PrimeAsync(
            root, (_, _) => { Interlocked.Increment(ref calls); return Task.CompletedTask; },
            new CancellationToken(canceled: true)));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task OnlyColdCompilableDependenciesArePrimed()
    {
        using var workspace = new AdhocWorkspace(WorkspaceService.HostServices);
        var (initial, dependencies) = CreateFanOut(workspace, 3);
        var unrelatedId = ProjectId.CreateNewId();
        var moduleId = ProjectId.CreateNewId();
        var transitiveId = ProjectId.CreateNewId();
        var solution = AddProject(initial.Solution, unrelatedId, "Unrelated")
            .AddProject(moduleId, "Module", "Module", LanguageNames.CSharp)
            .WithProjectCompilationOptions(moduleId, new CSharpCompilationOptions(OutputKind.NetModule))
            .AddProjectReference(initial.Id, new ProjectReference(moduleId));
        solution = AddProject(solution, transitiveId, "Transitive")
            .AddProjectReference(dependencies[1], new ProjectReference(transitiveId))
            .AddProjectReference(dependencies[2], new ProjectReference(transitiveId));
        var root = solution.GetProject(initial.Id)!;
        var warm = solution.GetProject(dependencies[0])!;
        var warmCompilation = await warm.GetCompilationAsync();
        var visited = new ConcurrentBag<ProjectId>();

        await ColdCompilationPrimer.PrimeAsync(root, (dependency, _) =>
        {
            Assert.Same(solution, dependency.Solution);
            visited.Add(dependency.Id);
            return Task.CompletedTask;
        }, default);

        Assert.Equal(3, visited.Count);
        Assert.Equal(3, visited.Distinct().Count());
        Assert.All(visited, id => Assert.Contains(id, dependencies.Skip(1).Append(transitiveId)));
        Assert.Same(warmCompilation, await warm.GetCompilationAsync());
        Assert.False(root.TryGetCompilation(out _));
        Assert.False(solution.GetProject(unrelatedId)!.TryGetCompilation(out _));
        Assert.False(solution.GetProject(moduleId)!.TryGetCompilation(out _));

        // Actual binding follows the same closure, and repeating it then hits Roslyn's cache.
        await ColdCompilationPrimer.PrimeAsync(root, default);
        Assert.True(solution.GetProject(transitiveId)!.TryGetCompilation(out _));
        await ColdCompilationPrimer.PrimeAsync(root,
            (_, _) => throw new InvalidOperationException("Already compiled dependency was primed twice."), default);
    }

    [Fact]
    public async Task AWorkspaceEditCannotRetargetTheCapturedCrossLanguageSnapshot()
    {
        using var workspace = new AdhocWorkspace(WorkspaceService.HostServices);
        var consumerId = ProjectId.CreateNewId();
        var contractId = ProjectId.CreateNewId();
        var consumerDocumentId = DocumentId.CreateNewId(consumerId);
        var contractDocumentId = DocumentId.CreateNewId(contractId);
        var solution = AddProject(workspace.CurrentSolution, consumerId, "Consumer")
            .AddProject(contractId, "Contracts", "Contracts", LanguageNames.VisualBasic)
            .WithProjectCompilationOptions(contractId, new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReference(contractId, MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddDocument(contractDocumentId, "Contract.vb", SourceText.From(
                "Public Class Contract\nPublic VersionOne As Integer\nEnd Class"))
            .AddDocument(consumerDocumentId, "Consumer.cs", SourceText.From(
                "public class Consumer { public int Read(Contract value) => value.VersionOne; }"))
            .AddProjectReference(consumerId, new ProjectReference(contractId));
        Assert.True(workspace.TryApplyChanges(solution));
        var captured = workspace.CurrentSolution.GetProject(consumerId)!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var priming = ColdCompilationPrimer.PrimeAsync(captured, async (dependency, token) =>
        {
            Assert.Same(captured.Solution, dependency.Solution);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            await dependency.GetCompilationAsync(token);
        }, ct.Token);
        try
        {
            await entered.Task.WaitAsync(ct.Token);
            var newer = workspace.CurrentSolution
                .WithDocumentText(contractDocumentId, SourceText.From(
                    "Public Class Contract\nPublic VersionTwo As Integer\nEnd Class"))
                .WithDocumentText(consumerDocumentId, SourceText.From(
                    "public class Consumer { public int Read(Contract value) => value.VersionTwo; }"));
            Assert.True(workspace.TryApplyChanges(newer));
            release.TrySetResult();
            await priming;

            var before = (await captured.GetCompilationAsync(ct.Token))!;
            var after = (await workspace.CurrentSolution.GetProject(consumerId)!.GetCompilationAsync(ct.Token))!;
            Assert.Empty(before.GetDiagnostics(ct.Token).Where(d => d.Severity == DiagnosticSeverity.Error));
            Assert.Empty(after.GetDiagnostics(ct.Token).Where(d => d.Severity == DiagnosticSeverity.Error));
            Assert.Single(before.GetTypeByMetadataName("Contract")!.GetMembers("VersionOne"));
            Assert.Empty(before.GetTypeByMetadataName("Contract")!.GetMembers("VersionTwo"));
            Assert.Single(after.GetTypeByMetadataName("Contract")!.GetMembers("VersionTwo"));
            Assert.Empty(after.GetTypeByMetadataName("Contract")!.GetMembers("VersionOne"));
        }
        finally
        {
            release.TrySetResult();
            ct.Cancel();
            try { await priming; } catch (OperationCanceledException) { }
        }
    }

    private static (Project Root, ProjectId[] Dependencies) CreateFanOut(Workspace workspace, int count)
    {
        var rootId = ProjectId.CreateNewId();
        var solution = AddProject(workspace.CurrentSolution, rootId, "Root");
        var dependencies = new ProjectId[count];
        for (int i = 0; i < count; i++)
        {
            var id = dependencies[i] = ProjectId.CreateNewId();
            solution = AddProject(solution, id, "Dependency" + i)
                .AddProjectReference(rootId, new ProjectReference(id));
        }
        return (solution.GetProject(rootId)!, dependencies);
    }

    private static Solution AddProject(Solution solution, ProjectId id, string name) =>
        solution.AddProject(id, name, name, LanguageNames.CSharp)
            .WithProjectCompilationOptions(id, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReference(id, MetadataReference.CreateFromFile(typeof(object).Assembly.Location));

    private static class InterlockedExtensions
    {
        public static void UpdateMax(ref int target, int value)
        {
            int current;
            do { current = Volatile.Read(ref target); }
            while (current < value && Interlocked.CompareExchange(ref target, value, current) != current);
        }
    }
}
