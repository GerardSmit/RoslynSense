using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMCP.Languages.WebForms;
using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Languages.WebForms.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Server-side includes at the parse level: <c>&lt;!--#include file="..." --&gt;</c> inlines the
/// target into the including file's parse, in the including file's scope — its registered
/// prefixes, its open tags — with every diagnostic located in the file that actually contains
/// the offending markup.
/// </summary>
/// <remarks>
/// These tests write real files: the include directive is resolved against the file system, which
/// is the point — the in-memory markup patterns other Aspx tests use cannot include anything.
/// </remarks>
public class AspxIncludeTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("aspx-include-tests-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A straggling handle on Windows; the temp cleaner gets it later.
        }
    }

    private string Write(string relativePath, string content)
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static Compilation Compilation()
    {
        string stubs = File.ReadAllText(Path.Combine(FixturePaths.AspxProjectDir, "SystemWebStubs.cs"));
        string runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

        return CSharpCompilation.Create("IncludeTests",
            [CSharpSyntaxTree.ParseText(stubs)],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private AspxParseResult Parse(string path) =>
        AspxSourceMappingService.Parse(
            path, File.ReadAllText(path), Compilation(), rootDirectory: _root);

    [Fact]
    public void AnIncludedFragmentIsInlinedIntoTheIncludingParse()
    {
        string footer = Write(
            Path.Combine("inc", "footer.ascx"),
            """<asp:Label ID="FooterLabel" runat="server" />""");
        string page = Write("default.ascx", """
            <div>
                <!--#include file="inc/footer.ascx" -->
            </div>
            """);

        var parse = Parse(page);

        Assert.Contains(parse.Controls, c => c.Id == "FooterLabel");

        var include = Assert.Single(parse.ParseTree!.IncludeFiles);
        Assert.Equal(Path.GetFullPath(footer), include.FullPath);
        Assert.NotNull(include.Hash);
    }

    [Fact]
    public void ADiagnosticInsideAnIncludeIsLocatedInTheIncludeFileAtItsOwnOffsets()
    {
        string footer = Write(
            Path.Combine("inc", "footer.ascx"),
            """
            <p>footer</p>
            <asp:Nope runat="server" />
            """);
        string page = Write("default.ascx", """
            <div>some page content, longer than the footer</div>
            <div>so that a parent-relative offset could never land on the tag</div>
            <!--#include file="inc/footer.ascx" -->
            """);

        var parse = Parse(page);

        var diagnostic = Assert.Single(parse.RawDiagnostics, d => d.Descriptor.Id == "WFC0007");
        Assert.Equal(Path.GetFullPath(footer), diagnostic.FileLineSpan.Path);

        string footerText = File.ReadAllText(footer);
        Assert.Equal(footerText.IndexOf("Nope", StringComparison.Ordinal), diagnostic.TextSpan.Start);
    }

    [Fact]
    public void ThePageRegistrationsApplyInsideTheInclude()
    {
        Write(
            Path.Combine("inc", "footer.ascx"),
            """<pfx:Label runat="server" />""");
        string page = Write("default.aspx", """
            <%@ Register TagPrefix="pfx" Namespace="System.Web.UI.WebControls" %>
            <!--#include file="inc/footer.ascx" -->
            """);

        var parse = Parse(page);

        Assert.DoesNotContain(parse.RawDiagnostics, d => d.Descriptor.Id == "WFC0007");
    }

    [Fact]
    public void TheSameFragmentAloneCannotResolveThePagePrefix()
    {
        // The counterpart of ThePageRegistrationsApplyInsideTheInclude: judged standalone, the
        // fragment reports the very control its includer resolves — which is why include targets
        // must not be scanned standalone.
        string footer = Write(
            Path.Combine("inc", "footer.ascx"),
            """<pfx:Label runat="server" />""");

        var parse = Parse(footer);

        Assert.Contains(parse.RawDiagnostics, d => d.Descriptor.Id == "WFC0007");
    }

    [Fact]
    public void AnIncludeMayCloseATagThePageOpened()
    {
        Write(Path.Combine("inc", "footer.ascx"), "</div>");
        string page = Write("default.ascx", """
            <div>
                <!--#include file="inc/footer.ascx" -->
            """);

        var parse = Parse(page);

        Assert.DoesNotContain(
            parse.RawDiagnostics, d => d.Descriptor.Id is "WFC0006" or "WFC0011");
    }

    [Fact]
    public void AStrayCloseInAnIncludeGetsTheSameToleranceAFragmentGets()
    {
        // Tag balance is judged per file: a close that matches nothing this file opened may be
        // finishing a wrapper the including page opened, so it stays quiet — the same tolerance
        // AspxTagBalanceTests.AFragmentMayCloseATagAnotherFileOpened documents for fragments.
        // What matters for includes is the absence of false positives in either direction.
        Write(Path.Combine("inc", "footer.ascx"), "</incorrect>");
        string page = Write("default.ascx", """
            <div>
                <!--#include file="inc/footer.ascx" -->
            </div>
            """);

        var parse = Parse(page);

        Assert.DoesNotContain(
            parse.RawDiagnostics, d => d.Descriptor.Id is "WFC0006" or "WFC0011");
    }

    [Fact]
    public void AMissingIncludeTargetIsReported()
    {
        string page = Write("default.ascx", """
            <!--#include file="inc/missing.ascx" -->
            """);

        var parse = Parse(page);

        var reported = Assert.Single(parse.RawDiagnostics, d => d.Descriptor.Id == "WFC0012");
        Assert.Equal(page, reported.FileLineSpan.Path);

        Microsoft.CodeAnalysis.Diagnostic diagnostic = reported;
        Assert.Contains("inc/missing.ascx", diagnostic.GetMessage());

        // Recorded even though unreadable, so a consumer can notice the file appearing later.
        var include = Assert.Single(parse.ParseTree!.IncludeFiles);
        Assert.Null(include.Hash);
    }

    [Fact]
    public void MutuallyIncludingFilesParseWithoutRecursing()
    {
        // Before the cycle guard this was not a failing test but a stack overflow.
        string a = Write("a.ascx", """
            <span>a</span>
            <!--#include file="b.ascx" -->
            """);
        Write("b.ascx", """
            <span>b</span>
            <!--#include file="a.ascx" -->
            """);

        var parse = Parse(a);

        Assert.NotNull(parse.ParseTree);
        Assert.Equal(2, parse.ParseTree!.IncludeFiles.Count);
    }

    [Fact]
    public void RootedAndTildeIncludePathsResolveAgainstTheRootDirectory()
    {
        Write(
            Path.Combine("inc", "footer.ascx"),
            """<asp:Label ID="RootedLabel" runat="server" />""");
        string page = Write(Path.Combine("pages", "sub", "deep.aspx"), """
            <!--#include virtual="/inc/footer.ascx" -->
            <!--#include virtual="~/inc/footer.ascx" -->
            """);

        var parse = Parse(page);

        // Both directives resolve to the same file: inlined twice, recorded once.
        Assert.Equal(2, parse.Controls.Count(c => c.Id == "RootedLabel"));
        Assert.Single(parse.ParseTree!.IncludeFiles);
    }
}

/// <summary>
/// The include graph the diagnostics side consults before any parse: which files are include
/// targets, and which page-level files' scope they are judged in.
/// </summary>
public class AspxIncludeGraphTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("aspx-include-graph-tests-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Write(string relativePath, string content)
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Include(string path) =>
        $"""<!--#include file="{path}" -->""";

    [Fact]
    public void NestedIncludesResolveToTheOutermostPages()
    {
        // a.aspx → b.ascx → c.ascx, and d.aspx → c.ascx. Only c's roots are a and d; b is a
        // target itself and never a scope of its own.
        string c = Write("c.ascx", "<span>c</span>");
        string b = Write("b.ascx", Include("c.ascx"));
        string a = Write("a.aspx", Include("b.ascx"));
        string d = Write("d.aspx", Include("c.ascx"));

        // c is deliberately not in the list: targets are discovered by following the edges.
        var graph = AspxIncludeService.Build([a, b, d], _root);

        Assert.True(graph.IsIncludeTarget(b));
        Assert.True(graph.IsIncludeTarget(c));
        Assert.False(graph.IsIncludeTarget(a));

        Assert.Equal(new[] { a, d }, graph.RootIncluders(c));
        Assert.Equal(new[] { a }, graph.RootIncluders(b));
        Assert.Empty(graph.RootIncluders(a));

        Assert.Equal(new[] { a, b, c, d }, graph.Closure(c));
        Assert.Equal(new[] { a, b, c }, graph.Closure(a));
        Assert.Equal(new[] { c, d }, graph.Closure(d));
    }

    [Fact]
    public void APureIncludeCycleHasNoRootAndFallsBackToStandalone()
    {
        string a = Write("a.ascx", Include("b.ascx"));
        string b = Write("b.ascx", Include("a.ascx"));

        var graph = AspxIncludeService.Build([a, b], _root);

        Assert.True(graph.IsIncludeTarget(a));
        Assert.Empty(graph.RootIncluders(a));
        Assert.Empty(graph.RootIncluders(b));
    }

    [Fact]
    public void AFileWithoutIncludeEdgesHasATrivialClosure()
    {
        string page = Write("plain.aspx", "<div>nothing included</div>");

        var graph = AspxIncludeService.Build([page], _root);

        Assert.False(graph.IsIncludeTarget(page));
        Assert.Empty(graph.RootIncluders(page));
        Assert.Equal(new[] { page }, graph.Closure(page));
    }
}

/// <summary>
/// The user-visible half, through the real workspace: a fragment that exists only to be included
/// is reported from its includer's scope — the includer's registrations resolving its controls,
/// its findings landing at the fragment's own positions — and the includer's report never carries
/// the fragment's diagnostics.
/// </summary>
[Collection(SharedState.Name)]
public class AspxIncludeLspTests
{
    /// <summary>
    /// Writes a page and the fragment it includes into the fixture project, runs the body, and
    /// cleans up. The page deliberately names no <c>Inherits</c> — the designer regeneration
    /// tests watch this directory (see <c>WebFormsIndexTests.WithTemporaryPageAsync</c>).
    /// </summary>
    private static async Task WithPageAndFragmentAsync(
        string pageMarkup, string fragmentMarkup, Func<string, string, Task> body)
    {
        string page = Path.Combine(FixturePaths.AspxProjectDir, "IncludeHost.aspx");
        string fragment = Path.Combine(FixturePaths.AspxProjectDir, "IncludeFragment.ascx");

        await File.WriteAllTextAsync(page, pageMarkup);
        await File.WriteAllTextAsync(fragment, fragmentMarkup);

        // The 30-second directory listing cache must see the files this test just created.
        AspxReferenceService.ResetFileListCache();

        try
        {
            await body(page, fragment);
        }
        finally
        {
            AspxDocumentService.Invalidate(page);
            AspxDocumentService.Invalidate(fragment);
            File.Delete(page);
            File.Delete(fragment);
            AspxReferenceService.ResetFileListCache();
        }
    }

    [Fact]
    public async Task AnIncludeOnlyFragmentIsJudgedInItsIncluderScope()
    {
        await WithPageAndFragmentAsync(
            """
            <%@ Page Language="C#" %>
            <%@ Register TagPrefix="pfx" Namespace="System.Web.UI.WebControls" %>
            <div class="wrapper">
                <!--#include file="IncludeFragment.ascx" -->
            """,
            """
                <pfx:Label runat="server" />
                <pfx:Doesnotexist runat="server" />
            </div>
            """,
            async (page, fragment) =>
            {
                var fragmentDiagnostics = await AspxLanguageHandler.DiagnosticsAsync(fragment, default);

                // In the page's scope the pfx prefix resolves, so pfx:Label is fine and only the
                // genuinely unknown control is reported. A standalone parse of the fragment would
                // have reported both — seeing exactly one is what proves the scope.
                var unknown = Assert.Single(fragmentDiagnostics, d => d.Code == "WFC0007");
                Assert.Contains("Doesnotexist", unknown.Message);
                Assert.DoesNotContain(fragmentDiagnostics, d => d.Code is "WFC0006" or "WFC0011");

                // ...and it lands where the tag is in the fragment's own text.
                string text = await File.ReadAllTextAsync(fragment);
                var source = Microsoft.CodeAnalysis.Text.SourceText.From(text);
                var expected = source.Lines.GetLinePosition(
                    text.IndexOf("Doesnotexist", StringComparison.Ordinal));
                Assert.Equal(expected.Line, unknown.Range.Start.Line);
                Assert.Equal(expected.Character, unknown.Range.Start.Character);

                // The page's report carries none of the fragment's findings.
                var pageDiagnostics = await AspxLanguageHandler.DiagnosticsAsync(page, default);
                Assert.DoesNotContain(pageDiagnostics, d => d.Code == "WFC0007");
            });
    }

    [Fact]
    public async Task EditingAFragmentReparsesThePageThatIncludesIt()
    {
        await WithPageAndFragmentAsync(
            """
            <%@ Page Language="C#" %>
            <!--#include file="IncludeFragment.ascx" -->
            """,
            """<asp:Doesnotexist runat="server" />""",
            async (page, fragment) =>
            {
                var document = await AspxDocumentService.GetAsync(page, default);
                Assert.Contains(
                    document!.Parse.RawDiagnostics, d => d.Descriptor.Id == "WFC0007");

                // The page's own text has not moved — only the fragment's. The memoized parse
                // must notice through the include hash it recorded.
                await File.WriteAllTextAsync(fragment, """<asp:Label runat="server" />""");

                document = await AspxDocumentService.GetAsync(page, default);
                Assert.DoesNotContain(
                    document!.Parse.RawDiagnostics, d => d.Descriptor.Id == "WFC0007");
            });
    }

    // ---- workspace/diagnostic ----------------------------------------------------------------

    private static async Task<IReadOnlyList<object>> SweepAsync(
        Project project, IReadOnlyDictionary<string, string>? previous = null) =>
        await new WebFormsLanguage(new MarkdownFormatter())
            .DiagnoseProjectAsync(project, previous ?? new Dictionary<string, string>(), default);

    private static Dictionary<string, string> ResultIds(IEnumerable<object> reports) =>
        reports
            .OfType<WorkspaceFullDocumentDiagnosticReport>()
            .Where(r => r.ResultId is not null)
            .ToDictionary(r => r.Uri, r => r.ResultId!);

    private static WorkspaceFullDocumentDiagnosticReport Full(IEnumerable<object> reports, string fileName) =>
        Assert.Single(
            reports.OfType<WorkspaceFullDocumentDiagnosticReport>(),
            r => Uri.UnescapeDataString(r.Uri).EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task TheSweepJudgesFragmentsInIncluderScopeAndSeesFragmentEdits()
    {
        await WithPageAndFragmentAsync(
            """
            <%@ Page Language="C#" %>
            <%@ Register TagPrefix="pfx" Namespace="System.Web.UI.WebControls" %>
            <div class="wrapper">
                <!--#include file="IncludeFragment.ascx" -->
            """,
            """
                <pfx:Label runat="server" />
            </div>
            """,
            async (page, fragment) =>
            {
                var project = await RoslynTestHelpers.OpenProjectAsync(FixturePaths.AspxProjectFile);
                var reports = await SweepAsync(project);

                // The unopened fragment is swept in its includer's scope: the page-registered
                // prefix resolves, so the file the user never opens stays clean in Problems.
                Assert.Empty(Full(reports, "IncludeFragment.ascx").Items);
                Assert.Empty(Full(reports, "IncludeHost.aspx").Items);

                // Now break the fragment. Neither the page's own text nor the fixture moved —
                // only the fragment — yet both reports must come back full: the fragment with
                // the finding, the page re-judged because its parse inlines the fragment.
                var previous = ResultIds(reports);
                await File.WriteAllTextAsync(fragment, """
                        <pfx:Doesnotexist runat="server" />
                    </div>
                    """);

                var second = await SweepAsync(project, previous);

                var fragmentReport = Full(second, "IncludeFragment.ascx");
                var finding = Assert.Single(fragmentReport.Items, d => d.Code == "WFC0007");
                Assert.Contains("Doesnotexist", finding.Message);

                Assert.Empty(Full(second, "IncludeHost.aspx").Items);
            });
    }
}
