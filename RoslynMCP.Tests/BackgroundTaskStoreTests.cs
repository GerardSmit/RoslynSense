using RoslynMCP.Services;
using static RoslynMCP.Services.BackgroundTaskStore;
using Xunit;

namespace RoslynMCP.Tests;

public sealed class BackgroundTaskStoreTests
{
    [Fact]
    public void CreateTask_ReturnsUniqueId()
    {
        var store = new BackgroundTaskStore();
        var id1 = store.CreateTask(TaskKind.Tests, "run tests");
        var id2 = store.CreateTask(TaskKind.Build, "build project");

        Assert.NotEqual(id1, id2);
        Assert.StartsWith("bg-tests-", id1);
        Assert.StartsWith("bg-build-", id2);
    }

    [Fact]
    public void NewTask_HasRunningStatus()
    {
        var store = new BackgroundTaskStore();
        var id = store.CreateTask(TaskKind.Tests, "test run");
        var task = store.Get(id);

        Assert.NotNull(task);
        Assert.Equal(BackgroundTaskStore.TaskStatus.Running, task.Status);
        Assert.Null(task.CompletedAt);
        Assert.Null(task.Result);
    }

    [Fact]
    public void Complete_SetsCompletedStatus_WhenExitCodeZero()
    {
        var store = new BackgroundTaskStore();
        var id = store.CreateTask(TaskKind.Tests, "test run");

        store.Complete(id, "All tests passed", 0);

        var task = store.Get(id)!;
        Assert.Equal(BackgroundTaskStore.TaskStatus.Completed, task.Status);
        Assert.Equal(0, task.ExitCode);
        Assert.Equal("All tests passed", task.Result);
        Assert.NotNull(task.CompletedAt);
    }

    [Fact]
    public void Complete_SetsFailedStatus_WhenExitCodeNonZero()
    {
        var store = new BackgroundTaskStore();
        var id = store.CreateTask(TaskKind.Tests, "test run");

        store.Complete(id, "3 tests failed", 1);

        var task = store.Get(id)!;
        Assert.Equal(BackgroundTaskStore.TaskStatus.Failed, task.Status);
        Assert.Equal(1, task.ExitCode);
    }

    [Fact]
    public void Cancel_SetsCancelledStatus()
    {
        var store = new BackgroundTaskStore();
        var id = store.CreateTask(TaskKind.Build, "build");

        store.Cancel(id, "User cancelled");

        var task = store.Get(id)!;
        Assert.Equal(BackgroundTaskStore.TaskStatus.Cancelled, task.Status);
        Assert.Equal("User cancelled", task.Result);
    }

    [Fact]
    public void ListTasks_ReturnsAllTasks()
    {
        var store = new BackgroundTaskStore();
        store.CreateTask(TaskKind.Tests, "t1");
        store.CreateTask(TaskKind.Build, "t2");
        store.CreateTask(TaskKind.Coverage, "t3");

        var all = store.ListTasks();
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public void ListTasks_WithStatusFilter_ReturnsMatching()
    {
        var store = new BackgroundTaskStore();
        var id1 = store.CreateTask(TaskKind.Tests, "t1");
        var id2 = store.CreateTask(TaskKind.Build, "t2");
        store.Complete(id1, "done", 0);

        var running = store.ListTasks(BackgroundTaskStore.TaskStatus.Running);
        Assert.Single(running);
        Assert.Equal(id2, running[0].Id);

        var completed = store.ListTasks(BackgroundTaskStore.TaskStatus.Completed);
        Assert.Single(completed);
        Assert.Equal(id1, completed[0].Id);
    }

    [Fact]
    public void Get_ReturnsNull_ForUnknownId()
    {
        var store = new BackgroundTaskStore();
        Assert.Null(store.Get("nonexistent"));
    }

    [Fact]
    public void ListTasks_OrderedByStartTimeDescending()
    {
        var store = new BackgroundTaskStore();
        var id1 = store.CreateTask(TaskKind.Tests, "first");
        var id2 = store.CreateTask(TaskKind.Tests, "second");
        var id3 = store.CreateTask(TaskKind.Tests, "third");

        var tasks = store.ListTasks();
        // Most recent first
        Assert.Equal(id3, tasks[0].Id);
        Assert.Equal(id2, tasks[1].Id);
        Assert.Equal(id1, tasks[2].Id);
    }

    [Fact]
    public void Complete_NoOp_ForUnknownId()
    {
        var store = new BackgroundTaskStore();
        // Should not throw
        store.Complete("unknown", "result", 0);
    }

    [Fact]
    public void Cancel_DefaultMessage_WhenNullProvided()
    {
        var store = new BackgroundTaskStore();
        var id = store.CreateTask(TaskKind.Tests, "test");

        store.Cancel(id);

        var task = store.Get(id)!;
        Assert.Equal("Task was cancelled.", task.Result);
    }

    [Fact]
    public void TaskKinds_ProduceCorrectIdPrefixes()
    {
        var store = new BackgroundTaskStore();

        var testsId = store.CreateTask(TaskKind.Tests, "t");
        var buildId = store.CreateTask(TaskKind.Build, "b");
        var coverageId = store.CreateTask(TaskKind.Coverage, "c");
        var profileId = store.CreateTask(TaskKind.Profile, "p");

        Assert.StartsWith("bg-tests-", testsId);
        Assert.StartsWith("bg-build-", buildId);
        Assert.StartsWith("bg-coverage-", coverageId);
        Assert.StartsWith("bg-profile-", profileId);
    }

    [Fact]
    public async Task WaitForCompletionAsync_WhenTaskAlreadyCompleted_ReturnsImmediately()
    {
        var store = new BackgroundTaskStore();
        var id = store.CreateTask(TaskKind.Tests, "test run");
        store.Complete(id, "All tests passed", 0);

        var result = await store.WaitForCompletionAsync(id, TimeSpan.FromSeconds(5));

        Assert.NotNull(result);
        Assert.Equal(BackgroundTaskStore.TaskStatus.Completed, result.Status);
    }

    [Fact]
    public async Task WaitForCompletionAsync_WhenTaskCompletesBeforeTimeout_ReturnsCompleted()
    {
        var store = new BackgroundTaskStore();
        var id = store.CreateTask(TaskKind.Tests, "test run");

        _ = Task.Run(async () =>
        {
            await Task.Delay(150);
            store.Complete(id, "Passed", 0);
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await store.WaitForCompletionAsync(id, TimeSpan.FromSeconds(5));
        sw.Stop();

        Assert.NotNull(result);
        Assert.Equal(BackgroundTaskStore.TaskStatus.Completed, result.Status);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"Expected early return, took {sw.Elapsed.TotalSeconds:F1}s");
    }

    [Fact]
    public async Task WaitForCompletionAsync_WhenTimeoutExpires_ReturnsRunningTask()
    {
        var store = new BackgroundTaskStore();
        var id = store.CreateTask(TaskKind.Build, "long build");

        var result = await store.WaitForCompletionAsync(id, TimeSpan.FromMilliseconds(300));

        Assert.NotNull(result);
        Assert.Equal(BackgroundTaskStore.TaskStatus.Running, result.Status);
    }

    [Fact]
    public async Task WaitForCompletionAsync_WhenTaskDoesNotExist_ReturnsNull()
    {
        var store = new BackgroundTaskStore();

        var result = await store.WaitForCompletionAsync("nonexistent-id", TimeSpan.FromSeconds(1));

        Assert.Null(result);
    }
}
