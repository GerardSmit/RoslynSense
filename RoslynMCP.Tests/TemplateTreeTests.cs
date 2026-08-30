using RoslynMCP.Languages.Templates;
using RoslynMCP.Languages.Templates.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The rows of the Templates section: what each one says, and where its two buttons point.
/// </summary>
/// <remarks>
/// Pure, from a merged set to a row, so every decision the section makes is checkable without a
/// workspace or a solution behind it. The one thing that needs a disk is the implementation
/// target, because the question it answers is whether the file the template names is really there.
/// </remarks>
public class TemplateTreeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "roslyn-sense-tests", $"template-tree-{Guid.NewGuid():N}");

    /// <summary>
    /// The example the section exists for: a screen, the line that declares it, and the control
    /// that renders it — two files in two languages that nothing but a name joins.
    /// </summary>
    [Fact]
    public void AScreenWithOneModuleCarriesTheImplementationItself()
    {
        Write(@"Modules\Alpha\View.ascx", "<%@ Control %>");

        var set = Set("""
            modules:
              Alpha:
                name: Alpha module
                controls:
                  default:
                    level: view
                    path: Modules/Alpha/View.ascx
            tabs:
              Alpha_Page:
                name:
                  nl-NL: Alpha page
                parent: Parent
                modules:
                  - type: Alpha
              Parent:
                name:
                  nl-NL: Ouder
            """);

        var row = TemplatesLanguage.Node(set, set.Entry("Alpha_Page")!, "nl-NL");

        Assert.Equal("Alpha page", row.Label);
        Assert.Equal(SolutionNodeKind.TemplateEntry, row.Kind);

        // The module it hosts, dimmed on the right: the fact that tells two screens with similar
        // names apart.
        Assert.Equal("Alpha", row.Description);

        // One module, so no row underneath naming it — the screen is the leaf.
        Assert.False(row.HasChildren);

        // Definition: the line the screen is declared on.
        Assert.NotNull(row.GoTo);
        Assert.EndsWith("1-first.yml", row.GoTo.Uri, StringComparison.OrdinalIgnoreCase);

        // Implementation: the control that renders it.
        Assert.Equal(
            LspConverters.PathToUri(Path.Combine(_root, "Modules", "Alpha", "View.ascx")),
            row.GoToSecondary?.Uri);

        // And the menu is told there is somewhere to go, which is what puts the button there.
        Assert.EndsWith(SolutionNodeKind.SecondaryTargetSuffix, row.ContextValue, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two modules is a choice, and a choice needs rows. Each one carries its own pair of targets,
    /// in the order the screen hosts them — which is the order they render in.
    /// </summary>
    [Fact]
    public void AScreenWithTwoModulesGrowsARowForEach()
    {
        Write(@"Modules\Two\View.ascx", "<%@ Control %>");
        Write(@"Modules\One\View.ascx", "<%@ Control %>");

        var set = Set("""
            modules:
              ModuleOne:
                name: Module one
                controls:
                  default:
                    path: Modules/One/View.ascx
              ModuleTwo:
                name: Module two
                controls:
                  default:
                    path: Modules/Two/View.ascx
            tabs:
              Mixed:
                name: Allebei
                modules:
                  - type: ModuleOne
                  - type: ModuleTwo
            """);

        var entry = set.Entry("Mixed")!;
        var row = TemplatesLanguage.Node(set, entry, null);

        Assert.True(row.HasChildren);
        Assert.Equal("2 modules", row.Description);

        // The screen itself has no implementation to offer once there is more than one.
        Assert.Null(row.GoToSecondary);
        Assert.Equal(SolutionNodeKind.TemplateEntry, row.ContextValue);

        var first = TemplatesLanguage.ModuleNode(set, entry, 0);
        var second = TemplatesLanguage.ModuleNode(set, entry, 1);

        Assert.Equal("Module one", first.Label);
        Assert.Equal("Module two", second.Label);

        Assert.Equal(
            LspConverters.PathToUri(Path.Combine(_root, "Modules", "One", "View.ascx")),
            first.GoToSecondary?.Uri);

        // Definition on a module row is the module's own declaration, not the screen's.
        Assert.Equal(set.Module("ModuleOne")!.Site.Range, first.GoTo?.Range);
    }

    /// <summary>
    /// A control whose file this checkout does not contain — one shipping inside a package — still
    /// leaves the button something to say: the line that names it is the next best answer to "what
    /// renders this".
    /// </summary>
    [Fact]
    public void AControlWithNoFileFallsBackToTheModuleDeclaration()
    {
        var set = Set("""
            modules:
              Delta:
                controls:
                  default:
                    path: Modules/Delta/View.ascx
            tabs:
              Screen:
                modules:
                  - type: Delta
            """);

        var row = TemplatesLanguage.Node(set, set.Entry("Screen")!, null);

        Assert.Equal(set.Module("Delta")!.Site.Range, row.GoToSecondary?.Range);
        Assert.EndsWith(SolutionNodeKind.SecondaryTargetSuffix, row.ContextValue, StringComparison.Ordinal);
    }

    /// <summary>
    /// A module these files never declare has nowhere to go, and the row says so by not offering
    /// the button. A button whose only outcome is an apology is worse than no button.
    /// </summary>
    [Fact]
    public void AModuleNobodyDeclaredOffersNoImplementation()
    {
        var set = Set("""
            tabs:
              Screen:
                modules:
                  - type: SomethingTheApplicationAlreadyHad
            """);

        var row = TemplatesLanguage.Node(set, set.Entry("Screen")!, null);

        Assert.Null(row.GoToSecondary);
        Assert.Equal(SolutionNodeKind.TemplateEntry, row.ContextValue);
    }

    /// <summary>
    /// A module the templates name and never describe still reaches its control, because the
    /// folder named after it holds one. Without this a quarter of the screens have a Definition
    /// and nothing beside it.
    /// </summary>
    [Fact]
    public void AModuleDescribedNowhereIsStillReachedThroughItsFolder()
    {
        Write(@"DesktopModules\Widgets\Alpha\Alpha_View.ascx", "<%@ Control %>");

        var set = Set("""
            tabs:
              Screen:
                modules:
                  - type: Widgets.Alpha
            """);

        var row = TemplatesLanguage.Node(set, set.Entry("Screen")!, null);

        Assert.Equal(
            LspConverters.PathToUri(
                Path.Combine(_root, "DesktopModules", "Widgets", "Alpha", "Alpha_View.ascx")),
            row.GoToSecondary?.Uri);
    }

    /// <summary>A heading says how much is under it, the way every other section's rows do.</summary>
    [Fact]
    public void AHeadingSaysHowManyScreensAreUnderIt()
    {
        var set = Set("""
            tabs:
              Root:
                name: Wortel
              Parent:
                name: Ouder
                parent: Root
              Beta:
                name: Beta
                parent: Root
            """);

        var row = TemplatesLanguage.Node(set, set.Entry("Root")!, null);

        Assert.Equal("2 pages", row.Description);
        Assert.True(row.HasChildren);
        Assert.Null(row.GoToSecondary);
    }

    /// <summary>
    /// A level is read down the left-hand column, so it is ordered by the word a reader is looking
    /// for rather than by the order some years of changes happened to write them in.
    /// </summary>
    [Fact]
    public void ALevelIsOrderedByTheNameOnTheRow()
    {
        var set = Set("""
            tabs:
              Omega:
                name: Omega
              Alpha:
                name: Alpha
            """);

        Assert.Equal(
            ["Alpha", "Omega"],
            TemplatesLanguage.Rows(set, set.Roots, null).Select(row => row.Label));
    }

    /// <summary>
    /// The key is on the hover rather than on the row: the row shows the words the customer uses,
    /// and the key is what somebody searches for the moment they have to change anything.
    /// </summary>
    [Fact]
    public void TheHoverNamesTheKeyAndTheLineItIsDeclaredOn()
    {
        var set = Set("""
            tabs:
              Alpha_Page:
                name: Alpha page
            """);

        Assert.Equal(
            "Alpha_Page — 1-first.yml:2",
            TemplatesLanguage.Node(set, set.Entry("Alpha_Page")!, null).Tooltip);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---- The harness ---------------------------------------------------------------------------

    private TemplateSet Set(string yaml) =>
        TemplateSet.Build(
            _root,
            [TemplateYaml.Read(Path.Combine(_root, "App_Data", "Templates", "1-first.yml"), yaml)],
            ["DesktopModules"]);

    private void Write(string relativePath, string content)
    {
        string path = Path.Combine(_root, relativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
