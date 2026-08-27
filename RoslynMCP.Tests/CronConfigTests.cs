using System.Text.Json;
using Microsoft.CodeAnalysis;
using RoslynMCP.Config;
using RoslynMCP.Languages.Cron;
using RoslynMCP.Languages.Cron.Core;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The configuration chain behind the scheduled-job pack: what an unconfigured solution gets, what
/// a configured one adds, and what a typo costs.
/// </summary>
/// <remarks>
/// The shipped table covers Hangfire and Quartz, and the parameter-name rule covers a wrapper
/// called <c>cronExpression</c>. What is left over is the in-house scheduler whose method is called
/// something nobody could have guessed, and that is the only thing configuration is for — so the
/// tests here are mostly about a bad entry costing the user one binding rather than the whole pack.
/// </remarks>
public class CronConfigTests
{
    // ---- What an unconfigured solution gets ------------------------------------------------------

    /// <summary>
    /// No <c>cron</c> section at all is not the same as a disabled pack, unlike the value sets: a
    /// solution that never mentions RoslynSense still gets Hangfire and Quartz read for it.
    /// </summary>
    [Fact]
    public void NothingConfiguredIsTheShippedTable()
    {
        var cron = Resolve("{}").Cron;

        Assert.True(cron.Enabled);
        Assert.Equal(Shipped, cron.Bindings.ToArray());
        Assert.Equal(CronPresets.ParameterNames.ToArray(), cron.ParameterNames.ToArray());
        Assert.True(cron.ExpressionDiagnostic);
        Assert.Equal(DiagnosticSeverity.Warning, cron.Severity);
    }

    [Fact]
    public void TheToolsGateTurnsThePackOff()
    {
        Assert.False(Resolve("""{"tools":{"cron":false}}""").Cron.Enabled);
    }

    [Fact]
    public void TheFlagTurnsThePackOff()
    {
        Assert.False(EffectiveSettings.Resolve(["--no-cron"], null, out _).Cron.Enabled);
    }

    /// <summary>
    /// A disabled pack keeps none of its table, so nothing downstream has to check both.
    /// </summary>
    [Fact]
    public void ADisabledPackCarriesNoBindings()
    {
        var cron = Resolve("""{"tools":{"cron":false},"cron":{"parameterNames":["schedule"]}}""").Cron;

        Assert.Empty(cron.Bindings);
        Assert.Empty(cron.ParameterNames);
    }

    /// <summary>Switching it off is worth a line in the reload log, like every other pack.</summary>
    [Fact]
    public void TurningThePackOffIsNamedInTheReloadDiff()
    {
        var changes = SettingsDiff.Describe(Resolve("{}"), Resolve("""{"tools":{"cron":false}}"""));

        Assert.Contains("cron: on → off", changes);
    }

    // ---- What configuration adds -----------------------------------------------------------------

    /// <summary>
    /// Appended rather than replacing. Configuring a wrapper must not cost the solution the library
    /// underneath it — which is the whole reason its wrapper exists.
    /// </summary>
    [Fact]
    public void AConfiguredBindingIsAddedToTheShippedOnes()
    {
        var cron = Resolve(Wrapper).Cron;

        Assert.Equal(CronPresets.Bindings.Length + 1, cron.Bindings.Length);
        Assert.Equal(Shipped, cron.Bindings[..Shipped.Length].ToArray());

        var added = cron.Bindings[^1];
        Assert.Equal("Application.Scheduler", added.ContainingType);
        Assert.Equal("Enqueue", added.MemberName);
        Assert.Equal(1, added.CronIndex);
        Assert.Equal(CronLibrary.Unknown, added.Library);
    }

    [Fact]
    public void AConfiguredParameterNameIsAddedToTheShippedOnes()
    {
        var cron = Resolve("""{"cron":{"parameterNames":["schedule"," whenever "]}}""").Cron;

        Assert.Equal(
            [.. CronPresets.ParameterNames, "schedule", "whenever"],
            cron.ParameterNames.ToArray());
    }

    [Fact]
    public void TheDialectIsReadFromTheEntry()
    {
        var cron = Resolve("""
            {"cron":{"bindings":[{"memberName":"Enqueue","cronIndex":0,"dialect":"quartz"}]}}
            """).Cron;

        Assert.Equal(CronDialect.Quartz, cron.Bindings[^1].Dialect);
    }

    [Fact]
    public void TheDiagnosticCanBeTurnedOffOnItsOwn()
    {
        var cron = Resolve("""{"cron":{"expressionDiagnostic":false}}""").Cron;

        Assert.True(cron.Enabled);
        Assert.False(cron.ExpressionDiagnostic);
    }

    [Fact]
    public void TheSeverityIsReadFromTheConfig()
    {
        Assert.Equal(
            DiagnosticSeverity.Error,
            Resolve("""{"cron":{"severity":"error"}}""").Cron.Severity);
    }

    // ---- What a typo costs -----------------------------------------------------------------------

    /// <summary>
    /// The rule every pack's settings follow: a malformed entry warns and is dropped rather than
    /// failing the load. A typo in one binding must not cost the solution its diagnostics.
    /// </summary>
    [Fact]
    public void AnEntryWithNoMemberNameIsDroppedWithAWarning()
    {
        var cron = Resolve(
            """{"cron":{"bindings":[{"containingType":"Application.Scheduler"}]}}""",
            out var warnings).Cron;

        Assert.Equal(Shipped, cron.Bindings.ToArray());
        Assert.Contains(warnings, w => w.Contains("no memberName", StringComparison.Ordinal));
    }

    /// <summary>
    /// A negative index is not a parameter position, and the honest recovery is the rule that
    /// works without one — the parameter's name — rather than a binding that silently reads
    /// argument zero.
    /// </summary>
    [Fact]
    public void AnIndexThatIsNotAParameterPositionWarnsAndFallsBackToTheName()
    {
        var cron = Resolve(
            """{"cron":{"bindings":[{"memberName":"Enqueue","cronIndex":-2}]}}""",
            out var warnings).Cron;

        Assert.Null(cron.Bindings[^1].CronIndex);
        Assert.Contains(warnings, w => w.Contains("cronIndex -2", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnknownDialectWarnsAndReadsThePlainCrontab()
    {
        var cron = Resolve(
            """{"cron":{"bindings":[{"memberName":"Enqueue","cronIndex":0,"dialect":"cronos"}]}}""",
            out var warnings).Cron;

        Assert.Equal(CronDialect.Standard, cron.Bindings[^1].Dialect);
        Assert.Contains(warnings, w => w.Contains("dialect 'cronos'", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnknownSeverityWarnsAndStaysAWarning()
    {
        var cron = Resolve("""{"cron":{"severity":"shout"}}""", out var warnings).Cron;

        Assert.Equal(DiagnosticSeverity.Warning, cron.Severity);
        Assert.Contains(warnings, w => w.Contains("cron.severity 'shout'", StringComparison.Ordinal));
    }

    // ---- All the way through to a claim -----------------------------------------------------------

    /// <summary>
    /// The point of the whole chain: a line of JSON reaches a scheduler of the solution's own,
    /// whose parameter is called <c>when</c> and which references no scheduling library at all.
    /// </summary>
    [Fact]
    public async Task AConfiguredBindingClaimsAnInHouseScheduler()
    {
        Assert.Null(await ClaimAsync(Resolve("{}").Cron));
        Assert.Equal("Cron", await ClaimAsync(Resolve(Wrapper).Cron));
    }

    /// <summary>
    /// And the same reach through the name rule instead, which is what a solution gets by naming
    /// its own parameter rather than writing any configuration at all.
    /// </summary>
    [Fact]
    public async Task AConfiguredParameterNameClaimsTheSameCallWithNoBinding()
    {
        var settings = Resolve("""{"cron":{"parameterNames":["when"]}}""").Cron;

        Assert.Equal("Cron", await ClaimAsync(settings));
    }

    // ---- The fixture and the harness --------------------------------------------------------------

    /// <summary>
    /// The shipped table as a plain array. <c>ImmutableArray&lt;T&gt;</c> compares by the identity
    /// of the array underneath it, so two equal tables are unequal to an equality assertion — and
    /// the failure it prints is two identical-looking lists, which is a bad half hour.
    /// </summary>
    private static readonly CronBinding[] Shipped = [.. CronPresets.Bindings];

    private const string Wrapper = """
        {"cron":{"bindings":[{
            "containingType": "Application.Scheduler",
            "memberName": "Enqueue",
            "cronIndex": 1
        }]}}
        """;

    /// <summary>
    /// A scheduler with no library behind it and no name the pack could have guessed — the only
    /// shape configuration exists for.
    /// </summary>
    private const string Source = """
        namespace Application
        {
            public sealed class Scheduler
            {
                public void Enqueue(string name, string when) { }
            }

            public sealed class Startup
            {
                public void Configure(Scheduler scheduler)
                {
                    scheduler.Enqueue("nightly", "0 3 * * *");
                }
            }
        }
        """;

    private static async Task<string?> ClaimAsync(CronSettings settings)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();

        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId, VersionStamp.Default, "Application", "Application", LanguageNames.CSharp,
                metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]))
            .AddDocument(
                DocumentId.CreateNewId(projectId), "Startup.cs", Source,
                filePath: @"C:\src\Startup.cs");

        var document = solution.GetProject(projectId)!.Documents.Single();
        string text = (await document.GetTextAsync(default)).ToString();
        var model = await document.GetSemanticModelAsync(default);
        var root = await document.GetSyntaxRootAsync(default);

        int index = text.IndexOf("\"0 3 * * *\"", StringComparison.Ordinal);
        Assert.True(index >= 0);

        return await new CronLanguage(settings)
            .DetectAsync(document, root!.FindToken(index + 1), model!, default);
    }

    private static EffectiveSettings Resolve(string json) => Resolve(json, out _);

    private static EffectiveSettings Resolve(string json, out List<string> warnings) =>
        EffectiveSettings.Resolve(
            [],
            JsonSerializer.Deserialize<RoslynSenseConfig>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                }),
            out warnings);
}
