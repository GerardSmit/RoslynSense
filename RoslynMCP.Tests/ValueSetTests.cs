using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynMCP.Config;
using RoslynMCP.Languages;
using RoslynMCP.Languages.Values;
using RoslynMCP.Languages.Values.Core;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services.Database;
using Xunit;
using LspDiagnostic = RoslynMCP.Lsp.Protocol.Diagnostic;

namespace RoslynMCP.Tests;

/// <summary>
/// Strings whose allowed values live in a database, and the places in C# that have to be one of
/// them.
/// </summary>
/// <remarks>
/// Everything below compiles and runs. A code the table does not have is not a type error, not an
/// analyzer warning and not something review catches — it is a branch that is simply never taken,
/// which is why the interesting half of these tests is about <i>finding</i> the literals at all:
/// they are scattered across an <c>is</c> pattern, a <c>switch</c> label and an <c>Equals</c> call,
/// nowhere near the member they are about.
/// </remarks>
public class ValueSetTests
{
    // ---- Finding the literals -------------------------------------------------------------------

    /// <remarks>
    /// The <c>is</c> cases go through <c>status?.Code</c> on purpose. A conditional access binds to
    /// nothing itself, so the member only resolves once the binding inside it is reached — and it
    /// is how most code of this kind is actually written.
    /// </remarks>
    [Theory]
    [InlineData("order_rejected")]        // an argument of a configured call
    [InlineData("order_ispat_one")]       // `status?.Code is X or Y`, first branch
    [InlineData("order_ispat_two")]       // and second
    [InlineData("order_bogus")]           // `==`
    [InlineData("order_equals")]          // `.Equals(x, StringComparison…)`
    [InlineData("order_subpat")]          // `is { Code: … }`
    [InlineData("order_caselabel")]       // `case "…":`
    [InlineData("order_switcharm")]       // `… switch { "…" => … }`
    [InlineData("order_assigned")]        // `x.Code = "…"`
    public async Task EveryShapeThatMeansIsThisCodeThatCodeIsFound(string literal)
    {
        Assert.True(await ClaimsAsync(literal));
    }

    /// <summary>A plain string beside a bound one is still a plain string.</summary>
    [Fact]
    public async Task AnUnrelatedLiteralIsLeftAlone()
    {
        Assert.False(await ClaimsAsync("not_a_code"));
    }

    /// <summary>
    /// The signature is what tells one overload from another, and the two-argument one carries no
    /// code at index 0 that this set is about.
    /// </summary>
    [Fact]
    public async Task AnOverloadTheSignatureDoesNotNameIsNotClaimed()
    {
        Assert.False(await ClaimsAsync("order_two_args"));
    }

    [Fact]
    public async Task AMemberOnAnotherTypeIsNotClaimed()
    {
        var settings = Standard() with
        {
            Bindings = [MemberBinding with { ContainingType = "Contoso.Shop.Data.Somewhere" }],
        };

        Assert.False(await ClaimsAsync("order_bogus", settings));
    }

    /// <summary>
    /// The escape hatch, and the one that matters for generated data layers: no type named, so any
    /// class declaring a <c>Code</c> is bound.
    /// </summary>
    [Fact]
    public async Task ABindingWithNoTypeMatchesTheMemberWhereverItIsDeclared()
    {
        var settings = Standard() with
        {
            Bindings = [MemberBinding with { ContainingType = null }],
        };

        Assert.True(await ClaimsAsync("order_bogus", settings));
    }

    // ---- The diagnostic ---------------------------------------------------------------------------

    [Fact]
    public async Task ACodeTheTableHasIsQuiet()
    {
        Assert.Empty(await DiagnosticsAsync("order_rejected"));
    }

    [Fact]
    public async Task ACodeTheTableDoesNotHaveIsReported()
    {
        var found = Assert.Single(await DiagnosticsAsync("order_bogus"));

        Assert.Equal("VAL0001", found.Code);
        Assert.Equal(1, found.Severity);
        Assert.Contains("'order_bogus' is not one of the", found.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The half that saves the time. These codes are long, lowercase and full of underscores, which
    /// is exactly the shape nobody diffs correctly by eye.
    /// </summary>
    [Fact]
    public async Task ATypoIsToldWhatItWasProbablyMeantToBe()
    {
        var found = Assert.Single(await DiagnosticsAsync("order_rejcted"));

        Assert.Contains("Did you mean 'order_rejected'?", found.Message, StringComparison.Ordinal);
    }

    /// <summary>The message has to make sense in the Problems panel, where the code is not visible.</summary>
    [Fact]
    public async Task TheMessageSaysWhereTheValuesComeFrom()
    {
        var set = new ValueSetDefinition
        {
            Id = "orderStatus",
            Connection = "shop",
            Query = "SELECT [Code] FROM Shop_OrderStatus",
        };

        var connections = new DbConnectionRegistry([Answering("shop", Rows(["order_rejected"]))]);
        var found = Assert.Single(
            await DiagnosticsAsync("order_bogus", Standard(set), connections));

        Assert.Contains("shop: SELECT [Code] FROM Shop_OrderStatus", found.Message, StringComparison.Ordinal);
    }

    /// <summary>The comparison the code does is usually case-insensitive, so the default is too.</summary>
    [Fact]
    public async Task CasingIsIgnoredUnlessTheSetAsksForIt()
    {
        Assert.Empty(await DiagnosticsAsync("ORDER_REJECTED"));
    }

    [Fact]
    public async Task ACaseSensitiveSetReportsTheCasingAndSaysWhatItShouldBe()
    {
        var found = Assert.Single(
            await DiagnosticsAsync("ORDER_REJECTED", Standard(InlineSet(caseSensitive: true))));

        Assert.Contains("Did you mean 'order_rejected'?", found.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rule that keeps this usable. "Not a valid code" is a claim about every code there is, so
    /// a database that did not answer has to say nothing rather than say something wrong.
    /// </summary>
    [Fact]
    public async Task ASetThatFailedToLoadReportsNothingAtAll()
    {
        var set = new ValueSetDefinition { Id = "orderStatus", Query = "SELECT [Code] FROM x" };
        var connections = new DbConnectionRegistry([Throwing("shop")]);

        Assert.Empty(await DiagnosticsAsync("order_bogus", Standard(set), connections));
    }

    /// <summary>Same rule, quieter cause: the row cap cut the answer short, so it is not the set.</summary>
    [Fact]
    public async Task ATruncatedSetOffersItsValuesAndJudgesNothing()
    {
        var set = new ValueSetDefinition { Id = "orderStatus", Query = "SELECT [Code] FROM x" };
        var connections = new DbConnectionRegistry(
            [Answering("shop", Rows(["order_rejected"], truncated: true))]);

        Assert.Empty(await DiagnosticsAsync("order_bogus", Standard(set), connections));
        Assert.NotEmpty(await CompletionAsync("order_bogus", Standard(set), connections));
    }

    [Fact]
    public async Task TheDiagnosticCanBeTurnedOffWithoutLosingCompletion()
    {
        var settings = Standard() with { UnknownValueDiagnostic = false };

        Assert.Empty(await DiagnosticsAsync("order_bogus", settings));
        Assert.NotEmpty(await CompletionAsync("order_bogus", settings));
    }

    [Fact]
    public async Task TheSeverityIsWhatTheConfigurationSaid()
    {
        var settings = Standard() with { Severity = DiagnosticSeverity.Warning };

        Assert.Equal(2, Assert.Single(await DiagnosticsAsync("order_bogus", settings)).Severity);
    }

    // ---- Completion and hover ---------------------------------------------------------------------

    [Fact]
    public async Task CompletionOffersTheValuesInTheOrderTheQueryReturnedThem()
    {
        var set = new ValueSetDefinition { Id = "orderStatus", Query = "SELECT [Code], [Name] FROM x" };
        var connections = new DbConnectionRegistry(
            [Answering("shop", Rows(["b_second", "a_first"], labels: ["Second", "First"]))]);

        var items = await CompletionAsync("order_bogus", Standard(set), connections);

        Assert.Equal(["b_second", "a_first"], items.Select(item => item.Label));
        Assert.Equal(["Second", "First"], items.Select(item => item.Detail));
        Assert.True(
            string.CompareOrdinal(items[0].SortText, items[1].SortText) < 0,
            "the query's own order has to survive the client's sort");
    }

    /// <summary>
    /// Completion replaces the whole literal rather than splicing into it, which is what makes
    /// fixing a wrong code one keystroke instead of a select-and-retype.
    /// </summary>
    [Fact]
    public async Task CompletingReplacesWhatIsAlreadyWritten()
    {
        var (items, text) = await CompletionWithTextAsync("order_bogus");
        var edit = Assert.Single(items, item => item.Label == "order_rejected").TextEdit;

        Assert.NotNull(edit);
        Assert.Equal("order_bogus", At(text, edit.Range));
    }

    [Fact]
    public async Task HoverNamesTheMemberTheValueIsAboutAndWhereTheValuesLive()
    {
        var hover = await HoverAsync("order_rejected");

        Assert.NotNull(hover);
        Assert.Contains("OrderController.OrderStatus_Get", hover.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("roslynsense.json", hover.Contents.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HoverOnAWrongCodeSaysSoRatherThanShowingNothing()
    {
        var hover = await HoverAsync("order_bogus");

        Assert.NotNull(hover);
        Assert.Contains("Not one of the", hover.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one place the pack can be honest about not knowing. The diagnostic has to stay silent
    /// when the set failed; hover was asked a question and a blank tooltip reads as "off".
    /// </summary>
    [Fact]
    public async Task HoverSaysWhyASetCouldNotBeLoaded()
    {
        var set = new ValueSetDefinition { Id = "orderStatus", Query = "SELECT [Code] FROM x" };
        var connections = new DbConnectionRegistry([Throwing("shop")]);

        var hover = await HoverAsync("order_rejected", Standard(set), connections);

        Assert.NotNull(hover);
        Assert.Contains("could not be loaded", hover.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("the database is not there", hover.Contents.Value, StringComparison.Ordinal);
    }

    // ---- The catalog ------------------------------------------------------------------------------

    [Fact]
    public async Task TheQueryRunsOnceHoweverOftenTheSetIsAskedFor()
    {
        var provider = Answering("shop", Rows(["order_rejected"]));
        var catalog = new ValueSetCatalog(new DbConnectionRegistry([provider]));
        var set = new ValueSetDefinition { Id = "orderStatus", Query = "SELECT [Code] FROM x" };

        await catalog.ContentsAsync(set, default);
        await catalog.ContentsAsync(set, default);
        await catalog.ContentsAsync(set, default);

        Assert.Equal(1, provider.Queries);
    }

    [Fact]
    public async Task RefreshingIsWhatMakesAMigrationVisible()
    {
        var codes = new List<string> { "order_rejected" };
        var provider = Answering("shop", () => Rows(codes));
        var catalog = new ValueSetCatalog(new DbConnectionRegistry([provider]));
        var set = new ValueSetDefinition { Id = "orderStatus", Query = "SELECT [Code] FROM x" };

        Assert.False((await catalog.ContentsAsync(set, default)).Contains("order_new"));

        codes.Add("order_new");
        catalog.Refresh(set.Id);

        Assert.True((await catalog.ContentsAsync(set, default)).Contains("order_new"));
    }

    [Fact]
    public async Task AProviderThatThrowsBecomesAnUnavailableSetRatherThanAFailedRequest()
    {
        var catalog = new ValueSetCatalog(new DbConnectionRegistry([Throwing("shop")]));
        var set = new ValueSetDefinition { Id = "orderStatus", Query = "SELECT [Code] FROM x" };

        var contents = await catalog.ContentsAsync(set, default);

        Assert.Equal(ValueSetState.Unavailable, contents.State);
        Assert.False(contents.Decides);
        Assert.Contains("the database is not there", contents.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASetNamingAConnectionThatIsNotThereSaysWhichOne()
    {
        var catalog = new ValueSetCatalog(new DbConnectionRegistry([Answering("other", Rows([]))]));
        var set = new ValueSetDefinition
        {
            Id = "orderStatus",
            Connection = "shop",
            Query = "SELECT [Code] FROM x",
        };

        var contents = await catalog.ContentsAsync(set, default);

        Assert.Contains("No connection named 'shop'", contents.Problem!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Naming the connection is optional only while there is nothing to be wrong about. Guessing
    /// which of several databases a set of codes lives in is not a guess worth making silently.
    /// </summary>
    [Fact]
    public async Task ASetWithNoConnectionUsesTheOnlyOneAndRefusesToPickBetweenTwo()
    {
        var set = new ValueSetDefinition { Id = "orderStatus", Query = "SELECT [Code] FROM x" };

        var single = new ValueSetCatalog(
            new DbConnectionRegistry([Answering("shop", Rows(["order_rejected"]))]));
        Assert.Equal(ValueSetState.Ready, (await single.ContentsAsync(set, default)).State);

        var several = new ValueSetCatalog(new DbConnectionRegistry(
            [Answering("shop", Rows(["order_rejected"])), Answering("other", Rows([]))]));
        Assert.Contains(
            "has to name one", (await several.ContentsAsync(set, default)).Problem!,
            StringComparison.Ordinal);
    }

    /// <summary>A NULL code is not something any C# literal can be, so it is not a value.</summary>
    [Fact]
    public async Task NullAndDuplicateRowsAreDroppedRatherThanOffered()
    {
        var catalog = new ValueSetCatalog(new DbConnectionRegistry(
            [Answering("shop", Rows(["order_rejected", "(null)", "order_rejected", ""]))]));

        var contents = await catalog.ContentsAsync(
            new ValueSetDefinition { Id = "orderStatus", Query = "SELECT [Code] FROM x" }, default);

        Assert.Equal(["order_rejected"], contents.Values.Select(entry => entry.Value));
    }

    // ---- Reading the configuration ----------------------------------------------------------------

    /// <summary>
    /// The one thing worth being strict about: a binding naming a set that is not there would
    /// otherwise be silently inert, which is the exact failure the pack exists to remove.
    /// </summary>
    [Fact]
    public void ABindingNamingASetThatIsNotThereIsDroppedWithAWarning()
    {
        var warnings = new List<string>();

        var settings = ValueSettings.Resolve(
            enabled: true,
            new ValueSetsConfig
            {
                Sets = [new ValueSetEntry { Id = "orderStatus", Values = ["a"] }],
                Bindings =
                [
                    new ValueBindingEntry { Set = "orderStatus", MemberName = "Code" },
                    new ValueBindingEntry { Set = "typo", MemberName = "Code" },
                ],
            },
            warnings);

        Assert.Single(settings.Bindings);
        Assert.Contains(warnings, warning => warning.Contains("'typo'", StringComparison.Ordinal));
    }

    [Fact]
    public void ASetWithNeitherAQueryNorValuesIsDroppedWithAWarning()
    {
        var warnings = new List<string>();

        var settings = ValueSettings.Resolve(
            enabled: true,
            new ValueSetsConfig { Sets = [new ValueSetEntry { Id = "empty" }] },
            warnings);

        Assert.Empty(settings.Sets);
        Assert.Contains(
            warnings,
            warning => warning.Contains("neither a query nor a list", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnknownSeveritySaysSoAndFallsBackToAnError()
    {
        var warnings = new List<string>();

        var settings = ValueSettings.Resolve(
            enabled: true, new ValueSetsConfig { Severity = "shout" }, warnings);

        Assert.Equal(DiagnosticSeverity.Error, settings.Severity);
        Assert.Contains(warnings, warning => warning.Contains("'shout'", StringComparison.Ordinal));
    }

    // ---- Suggestions --------------------------------------------------------------------------------

    [Theory]
    [InlineData("order_rejcted", "order_rejected")]
    [InlineData("order_rejectedd", "order_rejected")]
    [InlineData("ORDER_REJECTED", "order_rejected")]
    [InlineData("completely_different", null)]
    [InlineData("", null)]
    public void TheNearestValueIsOfferedOnlyWhenItIsNear(string written, string? expected)
    {
        var set = InlineSet();
        var contents = ValueSetContents.Loaded(set, set.Inline, complete: true);

        Assert.Equal(expected, ValueSuggestion.Nearest(contents, written, StringComparer.Ordinal));
    }

    // ---- Fixture ------------------------------------------------------------------------------------

    /// <summary>
    /// Every shape a status code is written in, in one method. Deliberately mundane: nothing here
    /// is unusual code, which is the point — none of it is checked by anything today.
    /// </summary>
    private const string Source = """
        using System;

        namespace Contoso.Shop.Data
        {
            public class OrderStatus
            {
                public string Code { get; set; }
                public string Name { get; set; }
            }
        }

        namespace Contoso.Shop
        {
            using Contoso.Shop.Data;

            public class OrderController
            {
                public OrderStatus OrderStatus_Get(string code) => null;

                public OrderStatus OrderStatus_Get(string code, int portalId) => null;

                public void Uses(OrderStatus status, string other)
                {
                    var found = OrderStatus_Get("order_rejected");
                    var typo = OrderStatus_Get("order_rejcted");
                    var shouted = OrderStatus_Get("ORDER_REJECTED");
                    var two = OrderStatus_Get("order_two_args", 1);

                    if (status?.Code is "order_ispat_one" or "order_ispat_two") { }
                    if (status.Code == "order_bogus") { }
                    if (status.Code.Equals("order_equals", StringComparison.OrdinalIgnoreCase)) { }
                    if (status is { Code: "order_subpat" }) { }
                    if (other == "not_a_code") { }

                    switch (status.Code)
                    {
                        case "order_caselabel":
                            break;
                    }

                    var arm = status.Code switch
                    {
                        "order_switcharm" => 1,
                        _ => 0,
                    };

                    status.Code = "order_assigned";
                }
            }
        }
        """;

    private static readonly string[] s_codes =
    [
        "order_rejected",
        "order_wait_for_login",
        "order_two_args",
        "order_ispat_one",
        "order_ispat_two",
        "order_equals",
        "order_subpat",
        "order_caselabel",
        "order_switcharm",
        "order_assigned",
    ];

    private static ValueSetDefinition InlineSet(bool caseSensitive = false) => new()
    {
        Id = "orderStatus",
        Inline = [.. s_codes.Select(code => new ValueEntry(code, null))],
        CaseSensitive = caseSensitive,
    };

    /// <summary>The method that takes a code, discriminated by signature from its overload.</summary>
    private static ValueBinding ArgumentBinding { get; } = new()
    {
        SetId = "orderStatus",
        ContainingType = "Contoso.Shop.OrderController",
        MemberName = "OrderStatus_Get",
        ParameterTypes = ["string"],
        ValueIndex = 0,
    };

    /// <summary>The property that holds one. No parameter position, so it is every comparison.</summary>
    private static ValueBinding MemberBinding { get; } = new()
    {
        SetId = "orderStatus",
        ContainingType = "Contoso.Shop.Data.OrderStatus",
        MemberName = "Code",
    };

    private static ValueSettings Standard(ValueSetDefinition? set = null) => new()
    {
        Enabled = true,
        Sets = [set ?? InlineSet()],
        Bindings = [ArgumentBinding, MemberBinding],
        UnknownValueDiagnostic = true,
        Severity = DiagnosticSeverity.Error,
    };

    private static async Task<bool> ClaimsAsync(string value, ValueSettings? settings = null)
    {
        var (pack, document, token, model, _) = await SetupAsync(value, settings);

        return await pack.DetectAsync(document, token, model, default) is not null;
    }

    private static async Task<IReadOnlyList<LspDiagnostic>> DiagnosticsAsync(
        string value, ValueSettings? settings = null, DbConnectionRegistry? connections = null)
    {
        var (pack, context, _) = await EmbeddedAsync(value, settings, connections);
        return await pack.DiagnosticsAsync(context, default);
    }

    private static async Task<IReadOnlyList<CompletionItem>> CompletionAsync(
        string value, ValueSettings? settings = null, DbConnectionRegistry? connections = null)
    {
        var (items, _) = await CompletionWithTextAsync(value, settings, connections);
        return items;
    }

    private static async Task<(IReadOnlyList<CompletionItem> Items, string Text)> CompletionWithTextAsync(
        string value, ValueSettings? settings = null, DbConnectionRegistry? connections = null)
    {
        var (pack, context, text) = await EmbeddedAsync(value, settings, connections);

        var list = await pack.CompletionAsync(
            context,
            new CompletionParams(new TextDocumentIdentifier(""), new Position(0, 0), null),
            default);

        return (list.Items, text);
    }

    private static async Task<Hover?> HoverAsync(
        string value, ValueSettings? settings = null, DbConnectionRegistry? connections = null)
    {
        var (pack, context, _) = await EmbeddedAsync(value, settings, connections);
        return await pack.HoverAsync(context, default);
    }

    private static async Task<(ValuesLanguage Pack, EmbeddedStringContext Context, string Text)> EmbeddedAsync(
        string value, ValueSettings? settings, DbConnectionRegistry? connections)
    {
        var (pack, document, token, model, text) = await SetupAsync(value, settings, connections);

        // Through DetectAsync, so every feature test covers the claim as well as the answer.
        Assert.Equal("ValueSet", await pack.DetectAsync(document, token, model, default));

        return (pack, new EmbeddedStringContext(
            pack, "ValueSet", [], document, model, token, token.SpanStart + 1), text);
    }

    private static async Task<(ValuesLanguage Pack, Document Document, SyntaxToken Token, SemanticModel Model, string Text)>
        SetupAsync(string value, ValueSettings? settings = null, DbConnectionRegistry? connections = null)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();

        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId, VersionStamp.Default, "Application", "Application", LanguageNames.CSharp,
                metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]))
            .AddDocument(
                DocumentId.CreateNewId(projectId), "Orders.cs", Source, filePath: @"C:\src\Orders.cs");

        var document = solution.GetProject(projectId)!.Documents.Single();
        string text = (await document.GetTextAsync(default)).ToString();
        var model = await document.GetSemanticModelAsync(default);
        var root = await document.GetSyntaxRootAsync(default);

        string literal = $"\"{value}\"";
        int index = text.IndexOf(literal, StringComparison.Ordinal);
        Assert.True(index >= 0, $"{literal} is not in the fixture");

        return (
            new ValuesLanguage(settings ?? Standard(), connections),
            document,
            root!.FindToken(index + 1),
            model!,
            text);
    }

    /// <summary>The text a range covers, for asserting where an edit or a squiggle landed.</summary>
    private static string At(string text, RoslynMCP.Lsp.Protocol.Range range)
    {
        var lines = Microsoft.CodeAnalysis.Text.SourceText.From(text).Lines;

        int start = lines[range.Start.Line].Start + range.Start.Character;
        int end = lines[range.End.Line].Start + range.End.Character;

        return text[start..end];
    }

    // ---- A database that answers whatever the test says ---------------------------------------------

    private static DbQueryResult Rows(
        IReadOnlyList<string> values, IReadOnlyList<string>? labels = null, bool truncated = false)
    {
        var rows = new List<string[]>(values.Count);

        for (int i = 0; i < values.Count; i++)
        {
            rows.Add(labels is null ? [values[i]] : [values[i], labels[i]]);
        }

        return new DbQueryResult(
            labels is null ? ["Code"] : ["Code", "Name"], rows, truncated, TimeSpan.Zero);
    }

    private static FakeProvider Answering(string alias, DbQueryResult result) =>
        new(alias, () => result);

    private static FakeProvider Answering(string alias, Func<DbQueryResult> result) =>
        new(alias, result);

    private static FakeProvider Throwing(string alias) =>
        new(alias, () => throw new InvalidOperationException("the database is not there"));

    private sealed class FakeProvider(string alias, Func<DbQueryResult> answer) : IDbProvider
    {
        public string Alias { get; } = alias;

        public string ProviderName => "fake";

        public PlanFormat? PlanFormat => null;

        /// <summary>How many times the set was actually fetched, which is the cache's whole claim.</summary>
        public int Queries { get; private set; }

        public Task<DbQueryResult> ExecuteQueryAsync(
            string sql, Dictionary<string, object?>? parameters, int maxRows, bool capturePlan,
            CancellationToken ct)
        {
            Queries++;
            return Task.FromResult(answer());
        }

        public Task<int> ExecuteNonQueryAsync(
            string sql, Dictionary<string, object?>? parameters, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<DbSchemaResult> GetTablesAsync(string? schema, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<DbSchemaResult> DescribeTableAsync(string tableName, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
