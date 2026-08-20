using RoslynMCP.Languages;
using RoslynMCP.Languages.WebForms;
using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Lsp.Search;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// A pasted <c>ClientID</c> or <c>UniqueID</c>, resolved back to the control the markup declares.
/// </summary>
/// <remarks>
/// The gate is tested apart from the resolution and with no workspace at all, because it is the
/// cost contract: everything the user types in the picker reaches it, and what it lets through
/// walks control trees. A test that only proved the resolution right would leave the expensive
/// half unpinned.
/// </remarks>
public class WebFormsClientIdQueryTests
{
    [Theory]
    // The two forms as the runtime writes them.
    [InlineData("dnn_ctr1848_OrderIntake_View_btnProcess")]
    [InlineData("dnn$ctr1848$OrderIntake_View$list$ctl00$ctl06$Amount")]
    [InlineData("form1_rptItems_ctl00_lblName")]
    [InlineData("ctl00_ContentPlaceHolder1_txtName")]
    public void AGeneratedIdIsRecognised(string query) =>
        Assert.True(ClientIdQuery.LooksLikeClientId(query));

    [Theory]
    // Underscored, but nothing in it was generated: a constant, an identifier, a file name.
    [InlineData("MAX_BUFFER_SIZE")]
    [InlineData("snake_case_helper_name")]
    [InlineData("Order_Item_Total")]
    // Two segments is not a nesting.
    [InlineData("form1_lblName")]
    // `ctl` without digits is somebody's own control.
    [InlineData("form1_ctlSaveButton_lblName")]
    // The ordinary query syntax already means something by these.
    [InlineData("Shop.ctl00_Amount")]
    [InlineData("Customer.cs:851")]
    [InlineData("ctl00_a b")]
    // Too short to be worth leaving the ordinary search for.
    [InlineData("a$ctl0")]
    public void AnOrdinaryQueryIsNot(string query) =>
        Assert.False(ClientIdQuery.LooksLikeClientId(query));

    [Fact]
    public void TheGeneratedSegmentsAreDroppedAndTheWrittenOnesKept()
    {
        var segments = ClientIdQuery.Parse("dnn_ctr1848_OrderIntake_View_list_ctl00_ctl06_Amount");

        Assert.NotNull(segments);
        Assert.False(segments!.Exact);
        Assert.Equal(["OrderIntake", "View", "list", "Amount"], segments.Kept.ToArray());
    }

    /// <summary>
    /// The <c>$</c> form is the one that does not have to guess.
    /// </summary>
    /// <remarks>
    /// An <c>ID</c> may contain an underscore and may not contain a <c>$</c>, so a
    /// <c>UniqueID</c> segments exactly where a <c>ClientID</c> can only offer candidates —
    /// <c>OrderIntake_View</c> is one segment here and two in the test above.
    /// </remarks>
    [Fact]
    public void TheUniqueIdFormSegmentsExactly()
    {
        var segments = ClientIdQuery.Parse("dnn$ctr1848$OrderIntake_View$list$ctl00$ctl06$Amount");

        Assert.NotNull(segments);
        Assert.True(segments!.Exact);
        Assert.Equal(["OrderIntake_View", "list", "Amount"], segments.Kept.ToArray());
    }

    /// <summary>A control someone called <c>dnn</c> is theirs; only the leading one is DNN's.</summary>
    [Fact]
    public void OnlyALeadingDnnIsDropped()
    {
        var segments = ClientIdQuery.Parse("dnn_ctr9_panel_dnn_ctl00_lbl");

        Assert.Equal(["panel", "dnn", "lbl"], segments!.Kept.ToArray());
    }
}

/// <summary>The resolution itself, against real control trees in the fixture project.</summary>
[Collection(SharedState.Name)]
public class WebFormsClientIdSearchTests
{
    private static async Task<IReadOnlyList<SearchHit>> ResolveAsync(string query)
    {
        var project = await RoslynTestHelpers.OpenProjectAsync(FixturePaths.AspxProjectFile);

        return await new WebFormsLanguage(new MarkdownFormatter())
            .SearchAsync(query, project.Solution, default);
    }

    /// <summary>
    /// The answer is the <c>ID</c> attribute in the markup.
    /// </summary>
    /// <remarks>
    /// Not the code-behind field, and for a control in a template there is no field to offer: the
    /// designer declares one per markup control at page level and nothing at all for the contents
    /// of an <c>&lt;ItemTemplate&gt;</c>, which is exactly the shape a pasted ClientID has.
    /// </remarks>
    [Fact]
    public async Task ANestedControlResolvesToItsIdInTheMarkup()
    {
        var hits = await ResolveAsync("form1_rptItems_ctl00_lblName");

        var hit = Assert.Single(hits);
        Assert.Equal(SearchItemKind.Member, hit.Kind);
        Assert.Equal(LspSymbolKind.Field, hit.SymbolKind);
        Assert.Equal("lblName", hit.Name);
        Assert.Equal(FixturePaths.RepeaterAspxFile, hit.FilePath);

        // The span is the id as written, so opening the hit selects the id rather than the tag.
        string line = (await File.ReadAllLinesAsync(hit.FilePath))[hit.Line];
        Assert.Equal("lblName", line[hit.Character..hit.EndCharacter]);
    }

    /// <summary>
    /// The generated item segments are skipped rather than matched.
    /// </summary>
    /// <remarks>
    /// <c>ctl00</c> is the repeater item, which exists only at runtime and has nothing in the
    /// markup to correspond to. Any rule that required the ClientID's segments to line up one for
    /// one with the markup ancestors would resolve nothing on a data-bound page — which is most of
    /// a WebForms site.
    /// </remarks>
    [Fact]
    public async Task TheRuntimeItemContainersAreSkipped()
    {
        var byUniqueId = await ResolveAsync("form1$rptItems$ctl00$ctl02$btnAction");

        var hit = Assert.Single(byUniqueId);
        Assert.Equal("btnAction", hit.Name);
        Assert.Equal(FixturePaths.RepeaterAspxFile, hit.FilePath);
    }

    /// <summary>The two segmentations of the same control agree.</summary>
    [Fact]
    public async Task TheClientIdAndTheUniqueIdOfOneControlAnswerTheSame()
    {
        var byClientId = await ResolveAsync("form1_rptItems_ctl00_btnAction");
        var byUniqueId = await ResolveAsync("form1$rptItems$ctl00$btnAction");

        var one = Assert.Single(byClientId);
        var other = Assert.Single(byUniqueId);

        Assert.Equal(one.FilePath, other.FilePath);
        Assert.Equal(one.Line, other.Line);
        Assert.Equal(one.Character, other.Character);
    }

    /// <summary>
    /// The container is what tells two controls of the same name apart.
    /// </summary>
    /// <remarks>
    /// <c>NamingScope.aspx</c> declares <c>lblDup</c> twice, once under each of two repeaters —
    /// which is legal precisely because each repeater is a naming scope, and is the case a search
    /// on the id alone cannot answer.
    /// </remarks>
    [Fact]
    public async Task TheContainerPicksBetweenTwoControlsOfTheSameName()
    {
        var inA = await ResolveAsync("form1_rptA_ctl00_lblDup");
        var inB = await ResolveAsync("form1_rptB_ctl00_lblDup");

        var a = Assert.Single(inA);
        var b = Assert.Single(inB);

        Assert.Equal(FixturePaths.NamingScopeAspxFile, a.FilePath);
        Assert.Equal(FixturePaths.NamingScopeAspxFile, b.FilePath);
        Assert.NotEqual(a.Line, b.Line);
    }

    /// <summary>
    /// An id whose containers name no control anywhere resolves to nothing at all.
    /// </summary>
    /// <remarks>
    /// Empty rather than "the closest thing": the whole value of a pasted id is that it is exact,
    /// and a picker that offers a same-named control from another page has answered a question
    /// nobody asked.
    /// </remarks>
    [Fact]
    public async Task AnIdWhoseContainersDoNotMatchResolvesToNothing()
    {
        Assert.Empty(await ResolveAsync("form1_rptNotHere_ctl00_lblDup"));
        Assert.Empty(await ResolveAsync("form1_rptItems_ctl00_lblNoSuchControl"));
    }

    /// <summary>An id that stopped at a container names the file, which is still an answer.</summary>
    [Fact]
    public async Task AnIdThatStopsAtAContainerNamesTheFile()
    {
        var hits = await ResolveAsync("dnn_ctr1848_NamingScope_ctl00");

        var hit = Assert.Single(hits);
        Assert.Equal(SearchItemKind.File, hit.Kind);
        Assert.Equal(FixturePaths.NamingScopeAspxFile, hit.FilePath);
    }

    /// <summary>
    /// The containers an id names are spread across files, and the match follows them out.
    /// </summary>
    /// <remarks>
    /// This is the ordinary shape of a real page rather than an exotic one: a module writes a user
    /// control, that control writes another, and the button in front of the user is three files
    /// from the page whose id it carries. A match confined to one file sees only the innermost run
    /// of segments, finds a leftover it cannot explain, and rejects the one right answer.
    /// <para>
    /// <c>lnkDeep</c> is the awkward half of it: the id names <c>ucInner</c>, and the file that
    /// declares the button is the one <em>writing</em> that tag rather than the one behind it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AControlUnderTwoUserControlsResolvesThroughBoth()
    {
        var hits = await ResolveAsync("dnn_ctr7_pageForm_ucOuter_ucInner_lnkDeep");

        var hit = Assert.Single(hits);
        Assert.Equal("lnkDeep", hit.Name);
        Assert.Equal(FixturePaths.OuterPanelAscxFile, hit.FilePath);
    }

    /// <summary>And the same id continues into the control's own markup.</summary>
    [Fact]
    public async Task AControlInsideTheInnerFileResolvesThroughItsHosts()
    {
        var hits = await ResolveAsync("dnn_ctr7_pageForm_ucOuter_ucInner_lblInner");

        var hit = Assert.Single(hits);
        Assert.Equal("lblInner", hit.Name);
        Assert.Equal(FixturePaths.InnerPanelAscxFile, hit.FilePath);
    }

    /// <summary>
    /// The hosts are matched, not merely walked past.
    /// </summary>
    /// <remarks>
    /// Walking out of a file is what makes the leftover segments explainable, so it is also the
    /// thing that could explain anything if it did not check. A container the markup never wrote
    /// still has to fail.
    /// </remarks>
    [Fact]
    public async Task AnIdNamingAHostThatWroteNoSuchTagResolvesToNothing()
    {
        Assert.Empty(await ResolveAsync("dnn_ctr7_pageForm_ucNotHere_ucInner_lblInner"));
        Assert.Empty(await ResolveAsync("dnn_ctr7_pageForm_ucOuter_ucNotHere_lblInner"));
    }

    /// <summary>An ordinary query is not this pack's business and it says so without looking.</summary>
    [Fact]
    public async Task AnOrdinaryQueryIsDeclined() =>
        Assert.Empty(await ResolveAsync("MAX_BUFFER_SIZE"));

    /// <summary>
    /// And the panel still answers it.
    /// </summary>
    /// <remarks>
    /// The guard on the seam rather than on the pack: a claimed query replaces the ordinary
    /// result outright, so a pack that claimed too widely would not add noise — it would make
    /// Ctrl+T stop finding C# altogether.
    /// </remarks>
    [Fact]
    public async Task AnOrdinaryQueryStillGetsTheOrdinaryAnswer()
    {
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.AspxProjectFile);

        var result = await SearchEverywhereHandler.SearchAsync(
            new SearchEverywhereParams("PageHelper"), default, Enabled());

        Assert.Contains(result.Items, item => item.Name == "PageHelper");
    }

    /// <summary>
    /// A control is a member, so the tab that asked for types does not get one.
    /// </summary>
    /// <remarks>
    /// The filter is on the seam rather than in the pack: the pack answers what it knows and the
    /// tab decides what it wanted. It matters here more than for an ordinary contributor, because
    /// a claim replaces the whole result — so a claim the tab has no use for has to leave the
    /// ordinary search to answer rather than blanking the panel.
    /// </remarks>
    [Fact]
    public async Task TheTabThatAskedForTypesGetsNoControls()
    {
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.AspxProjectFile);

        const string clientId = "form1_rptItems_ctl00_lblName";

        var members = await SearchEverywhereHandler.SearchAsync(
            new SearchEverywhereParams(clientId, Only: "member"), default, Enabled());
        Assert.Contains(members.Items, item => item.Name == "lblName");

        var types = await SearchEverywhereHandler.SearchAsync(
            new SearchEverywhereParams(clientId, Only: "type"), default, Enabled());
        Assert.DoesNotContain(types.Items, item => item.Name == "lblName");
    }

    private static LanguageSession Enabled() =>
        new([new WebFormsLanguage(new MarkdownFormatter())], _ => true);
}
