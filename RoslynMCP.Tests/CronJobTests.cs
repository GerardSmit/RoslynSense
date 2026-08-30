using Microsoft.CodeAnalysis;
using RoslynMCP.Languages.Cron;
using RoslynMCP.Languages.Cron.Core;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services.Symbols;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Finding the scheduled jobs in a project, and saying which parts of one a reader can actually
/// know.
/// </summary>
/// <remarks>
/// The distinction everything here turns on: <c>AddOrUpdate("nightly", …, "0 3 * * *")</c> and
/// <c>AddOrUpdate(id, …, _config["Jobs:Cron"])</c> are the same call, and a list that drew them the
/// same way would be lying about the second. Nobody can say what that job is called or when it
/// runs, and a wrong fire time in a list of jobs is worse than a missing one — the reader has no
/// way to tell that it is wrong.
/// </remarks>
public class CronJobTests
{
    // ---- Finding them -----------------------------------------------------------------------------

    [Fact]
    public async Task EveryRegistrationInTheProjectIsFound()
    {
        var jobs = await JobsAsync();

        Assert.Equal(11, jobs.Count);
    }

    [Fact]
    public async Task AnOrdinaryCallThatIsNotARegistrationIsNotOne()
    {
        var jobs = await JobsAsync();

        Assert.DoesNotContain(jobs, job => job.JobId.Text == "not a job");
    }

    [Fact]
    public async Task AQuartzTriggerIsFoundAndReadWithQuartzsRules()
    {
        var job = await ScheduleAsync("0 0 12 ? * 5");

        Assert.Equal(CronLibrary.Quartz, job.Library);
        Assert.Equal(CronDialect.Quartz, job.Dialect);
        Assert.Equal(CronRegistrationKind.Trigger, job.Kind);
    }

    /// <summary>A removal names a job and carries no schedule, and the row has to say so.</summary>
    [Fact]
    public async Task ARemovalCarriesNoSchedule()
    {
        var job = Assert.Single(
            (await JobsAsync()).Where(j => j.Kind == CronRegistrationKind.Remove));

        Assert.Equal("retired", job.JobId.Text);
        Assert.Equal(RegistrationOrigin.Absent, job.Cron.Origin);
    }

    // ---- What is knowable -------------------------------------------------------------------------

    [Fact]
    public async Task AScheduleWrittenOnTheSpotIsALiteral()
    {
        var job = await ScheduleAsync("*/10 * * * *");

        Assert.Equal(RegistrationOrigin.Literal, job.Cron.Origin);
        Assert.False(job.IsDynamic);
    }

    /// <summary>
    /// A <c>const</c> is as knowable as a literal — the compiler folded it, and so would a reader.
    /// </summary>
    [Fact]
    public async Task AConstScheduleIsRead()
    {
        var job = await JobAsync("const");

        Assert.Equal("0 4 * * *", job.Cron.Text);
        Assert.Equal(RegistrationOrigin.Constant, job.Cron.Origin);
        Assert.False(job.IsDynamic);
    }

    /// <summary>
    /// <c>static readonly</c> is how a schedule shared between registrations is usually written,
    /// and it is not a constant to the compiler — <c>GetConstantValue</c> says nothing about it.
    /// </summary>
    [Fact]
    public async Task AStaticReadonlyScheduleIsFoldedToo()
    {
        var job = await JobAsync("readonly");

        Assert.Equal("0 5 * * *", job.Cron.Text);
        Assert.Equal(RegistrationOrigin.Constant, job.Cron.Origin);
    }

    /// <summary>A local assigned once is its initializer, which is what a reader concludes too.</summary>
    [Fact]
    public async Task ALocalAssignedOnceIsRead()
    {
        var job = await JobAsync("local");

        Assert.Equal("0 6 * * *", job.Cron.Text);
        Assert.False(job.IsDynamic);
    }

    /// <summary>
    /// Reassigned, so the declaration is its first value rather than its value — and reading it
    /// would name a schedule the job does not run on.
    /// </summary>
    [Fact]
    public async Task ALocalThatIsReassignedIsNotRead()
    {
        var job = await JobAsync("reassigned");

        Assert.Null(job.Cron.Text);
        Assert.Equal(RegistrationOrigin.Variable, job.Cron.Origin);
        Assert.True(job.IsDynamic);
    }

    /// <summary>
    /// The case the whole distinction exists for. The value lives in a settings file this cannot
    /// read, but the key is right there — and "read from Jobs:Nightly:Cron" is a useful row where
    /// "unknown" is not.
    /// </summary>
    [Fact]
    public async Task AScheduleReadFromConfigurationNamesItsKey()
    {
        var job = await JobAsync("configured");

        Assert.Null(job.Cron.Text);
        Assert.Equal(RegistrationOrigin.Configuration, job.Cron.Origin);
        Assert.Equal("Jobs:Nightly:Cron", job.Cron.Detail);
    }

    [Fact]
    public async Task AScheduleThatIsAParameterSaysWhoseItIs()
    {
        var job = await JobAsync("passed");

        Assert.Equal(RegistrationOrigin.Parameter, job.Cron.Origin);
        Assert.Equal("cronExpression", job.Cron.Detail);
    }

    [Fact]
    public async Task AComputedScheduleIsShownAsWhatWasWritten()
    {
        var job = await JobAsync("ternary");

        Assert.Equal(RegistrationOrigin.Expression, job.Cron.Origin);
        Assert.Contains("?", job.Cron.Detail!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A job id built at run time must not read as a literal somebody could grep for — a tenant
    /// prefix is the usual shape, and there is one row per tenant at run time and one here.
    /// </summary>
    [Fact]
    public async Task AnInterpolatedJobIdIsDynamic()
    {
        var job = Assert.Single((await JobsAsync()).Where(j => j.JobId.IsDynamic));

        Assert.Null(job.JobId.Text);
        Assert.True(job.IsDynamic);
    }

    // ---- The job's own method ---------------------------------------------------------------------

    /// <summary>
    /// Hangfire's argument is an expression tree whose whole purpose is to be read rather than
    /// run, so the method is found by binding the call inside the lambda.
    /// </summary>
    [Fact]
    public async Task TheMethodInsideALambdaIsNamed()
    {
        var job = await ScheduleAsync("*/10 * * * *");

        Assert.Equal("Jobs.SyncOrders", job.Method.Text);
        Assert.NotNull(job.Target);
        Assert.NotNull(job.TargetUri);
    }

    /// <summary>
    /// A body that branches has no one method to name, and naming the first one it happens to call
    /// would be a row pointing at the wrong code.
    /// </summary>
    [Fact]
    public async Task ALambdaThatIsNotOneCallNamesNoMethod()
    {
        var job = await JobAsync("branching");

        Assert.Null(job.Method.Text);
        Assert.Equal(RegistrationOrigin.Expression, job.Method.Origin);
        Assert.Null(job.Target);
    }

    // ---- The rows ---------------------------------------------------------------------------------

    [Fact]
    public async Task AStaticJobsRowSaysWhenItRunsAndWhatItRuns()
    {
        var row = CronLanguage.Node(await ScheduleAsync("*/10 * * * *"));

        Assert.Equal("resend", row.Label);
        Assert.Equal("Every 10 minutes · Jobs.SyncOrders", row.Description);
        Assert.Equal(SolutionNodeKind.CronJob + SolutionNodeKind.SecondaryTargetSuffix, row.ContextValue);
    }

    /// <summary>
    /// A schedule nobody read is shown as where it comes from, never as a schedule — and the
    /// context value carries the mark so the row is drawn apart from the ones that are known.
    /// </summary>
    [Fact]
    public async Task ADynamicJobsRowShowsTheKeyRatherThanASchedule()
    {
        var row = CronLanguage.Node(await JobAsync("configured"));

        Assert.Contains("⟨config: Jobs:Nightly:Cron⟩", row.Description!, StringComparison.Ordinal);
        Assert.DoesNotContain("Every", row.Description!, StringComparison.Ordinal);
        Assert.Contains("Dynamic", row.ContextValue, StringComparison.Ordinal);

        // Not dimmed. Dimming means "the workspace cannot answer about this" everywhere else in
        // this tree, and an unloaded project and a config-driven schedule must not look the same.
        Assert.False(row.Dimmed);
    }

    /// <summary>
    /// Clicking a job opens the registration — where the id, the schedule and the wiring are, and
    /// the only target that exists for every job.
    /// </summary>
    [Fact]
    public async Task ClickingAJobOpensItsRegistration()
    {
        var job = await ScheduleAsync("*/10 * * * *");
        var row = CronLanguage.Node(job);

        Assert.NotNull(row.GoTo);
        Assert.Equal(job.Registration, row.GoTo.Range);
        Assert.NotNull(row.GoToSecondary);
        Assert.NotEqual(row.GoTo.Range, row.GoToSecondary.Range);
    }

    [Fact]
    public async Task AJobWithNoMethodToOpenIsNotOfferedTheMenuItem()
    {
        var row = CronLanguage.Node(await JobAsync("branching"));

        Assert.Null(row.GoToSecondary);
        Assert.DoesNotContain(
            SolutionNodeKind.SecondaryTargetSuffix, row.ContextValue, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two registrations whose ids are both computed would collide on any id built from the id
    /// alone, and a duplicate makes the second branch fail to render rather than merely look odd.
    /// </summary>
    [Fact]
    public async Task EveryRowHasItsOwnId()
    {
        var ids = (await JobsAsync()).Select(job => CronLanguage.Node(job).Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    // ---- The cache ---------------------------------------------------------------------------------

    /// <summary>
    /// The same compilation is scanned once. A compilation is immutable, so there is nothing to
    /// invalidate — but there is also nothing forcing a second ask to be cheap except this.
    /// </summary>
    [Fact]
    public async Task TheSameCompilationIsScannedOnce()
    {
        var (compilation, projectPath) = await CompilationAsync();
        var index = new CronJobIndex(CronSettings.Default);

        var first = index.Of(compilation, projectPath, default);
        var second = index.Of(compilation, projectPath, default);

        Assert.Same(first, second);
    }

    /// <summary>
    /// An edit produces a new compilation, and a new compilation is a new answer — which is the
    /// whole reason the key is the compilation rather than the project.
    /// </summary>
    [Fact]
    public async Task AnEditIsANewAnswer()
    {
        var (compilation, projectPath) = await CompilationAsync();
        var index = new CronJobIndex(CronSettings.Default);

        var before = index.Of(compilation, projectPath, default);

        var edited = compilation.RemoveAllSyntaxTrees();
        var after = index.Of(edited, projectPath, default);

        Assert.NotEmpty(before);
        Assert.Empty(after);
    }

    /// <summary>
    /// Two indexes never share an answer, which is what lets the settings stay out of the key.
    /// </summary>
    /// <remarks>
    /// <see cref="CronSettings"/> holds immutable arrays, and an immutable array compares by the
    /// identity of the array underneath it — so a settings-keyed cache would have missed on every
    /// call the moment a second, structurally identical instance existed. Owning a table per index
    /// makes the comparison never happen.
    /// </remarks>
    [Fact]
    public async Task AnIndexBuiltUnderOtherSettingsIsNotShared()
    {
        var (compilation, projectPath) = await CompilationAsync();

        var shipped = new CronJobIndex(CronSettings.Default).Of(compilation, projectPath, default);
        var narrowed = new CronJobIndex(CronSettings.Default with { Bindings = [] })
            .Of(compilation, projectPath, default);

        Assert.NotEmpty(shipped);
        Assert.Empty(narrowed);
    }

    // ---- The fixture and the harness --------------------------------------------------------------

    /// <summary>
    /// Stubs rather than the real packages, so the fixture needs no restore and pins the shapes
    /// the pack keys on rather than whatever version happened to resolve.
    /// </summary>
    private const string Source = """
        using System;
        using System.Collections.Generic;

        namespace Hangfire
        {
            public static class RecurringJob
            {
                public static void AddOrUpdate<T>(
                    string recurringJobId, Action<T> methodCall, string cronExpression)
                {
                }

                public static void RemoveIfExists(string recurringJobId) { }
            }
        }

        namespace Quartz
        {
            public interface ITriggerConfigurator
            {
                ITriggerConfigurator WithCronSchedule(string cronExpression);
            }
        }

        namespace Application
        {
            using Hangfire;
            using Quartz;

            public sealed class Configuration
            {
                public string this[string key] => null;
            }

            public sealed class Jobs
            {
                private const string Nightly = "0 4 * * *";

                private static readonly string Shared = "0 5 * * *";

                private readonly Configuration _config = new Configuration();

                private bool _weekly;

                public void SyncOrders() { }

                public void Rebuild() { }

                public void Register(string cronExpression, string tenant)
                {
                    RecurringJob.AddOrUpdate<Jobs>("resend", x => x.SyncOrders(), "*/10 * * * *");
                    RecurringJob.AddOrUpdate<Jobs>("const", x => x.Rebuild(), Nightly);
                    RecurringJob.AddOrUpdate<Jobs>("readonly", x => x.Rebuild(), Shared);

                    string once = "0 6 * * *";
                    RecurringJob.AddOrUpdate<Jobs>("local", x => x.Rebuild(), once);

                    string changing = "0 7 * * *";
                    changing = "0 8 * * *";
                    RecurringJob.AddOrUpdate<Jobs>("reassigned", x => x.Rebuild(), changing);

                    RecurringJob.AddOrUpdate<Jobs>(
                        "configured", x => x.Rebuild(), _config["Jobs:Nightly:Cron"]);

                    RecurringJob.AddOrUpdate<Jobs>("passed", x => x.Rebuild(), cronExpression);

                    RecurringJob.AddOrUpdate<Jobs>(
                        "ternary", x => x.Rebuild(), _weekly ? "0 9 * * 1" : "0 9 * * *");

                    // A body that does more than one thing names no one method.
                    RecurringJob.AddOrUpdate<Jobs>(
                        $"{tenant}-branching",
                        x => { x.SyncOrders(); x.Rebuild(); },
                        "0 10 * * *");

                    RecurringJob.RemoveIfExists("retired");

                    Announce("not a job");
                }

                public void Schedule(ITriggerConfigurator trigger)
                {
                    trigger.WithCronSchedule("0 0 12 ? * 5");
                }

                private void Announce(string message) { }
            }
        }
        """;

    /// <summary>The one job whose schedule was written as <paramref name="cron"/>.</summary>
    private static async Task<CronJob> ScheduleAsync(string cron)
    {
        var jobs = await JobsAsync();
        return Assert.Single(jobs.Where(job => job.Cron.Text == cron));
    }

    /// <summary>The one job registered under <paramref name="id"/>.</summary>
    private static async Task<CronJob> JobAsync(string id)
    {
        var jobs = await JobsAsync();

        return Assert.Single(jobs.Where(job =>
            job.JobId.Text == id
            || (job.JobId.Text is null && job.Method.Detail?.Contains(id) == true)
            || job.JobId.Detail?.Contains(id, StringComparison.Ordinal) == true));
    }

    private static async Task<IReadOnlyList<CronJob>> JobsAsync()
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();

        const string projectPath = @"C:\src\Application.csproj";

        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId, VersionStamp.Default, "Application", "Application", LanguageNames.CSharp,
                filePath: projectPath,
                metadataReferences:
                [
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                ]))
            .AddDocument(
                DocumentId.CreateNewId(projectId), "Jobs.cs", Source, filePath: @"C:\src\Jobs.cs");

        var compilation = await solution.GetProject(projectId)!.GetCompilationAsync(default);

        return new CronJobIndex(CronSettings.Default).Of(compilation!, projectPath, default);
    }

    private static async Task<(Compilation Compilation, string ProjectPath)> CompilationAsync()
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();

        const string projectPath = @"C:\src\Application.csproj";

        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId, VersionStamp.Default, "Application", "Application", LanguageNames.CSharp,
                filePath: projectPath,
                metadataReferences:
                [
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                ]))
            .AddDocument(
                DocumentId.CreateNewId(projectId), "Jobs.cs", Source, filePath: @"C:\src\Jobs.cs");

        return ((await solution.GetProject(projectId)!.GetCompilationAsync(default))!, projectPath);
    }
}
