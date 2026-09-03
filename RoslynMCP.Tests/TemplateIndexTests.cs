using RoslynMCP.Languages.Templates.Core;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// What a folder of template files says once it has been merged: which entry is declared where,
/// what sits under what, and which file renders each screen.
/// </summary>
/// <remarks>
/// Almost all of it is pure — a file path and a string of YAML — because the merge is the part
/// with rules worth pinning and none of them need a workspace. The two that touch the disk are the
/// two that are about the disk: finding the folders, and resolving a control path to a file.
/// </remarks>
public class TemplateIndexTests
{
    private const string First = @"C:\src\Web\App_Data\Templates\1-first.yml";

    private const string Second = @"C:\src\Web\App_Data\Templates\2-second.yml";

    /// <summary>
    /// The range is the key's own, so clicking a row lands on the name that was clicked rather
    /// than at the top of a two-hundred-line file.
    /// </summary>
    [Fact]
    public void AnEntryPointsAtTheLineThatDeclaresIt()
    {
        var document = TemplateYaml.Read(First, """
            tabs:
              Root:
                name: Wortel
            """);

        var entry = Assert.Single(document.Entries);

        Assert.Equal("Root", entry.Key);
        Assert.Equal(First, entry.Site.FilePath);
        Assert.Equal(1, entry.Site.Range.Start.Line);
        Assert.Equal(2, entry.Site.Range.Start.Character);
    }

    /// <summary>
    /// The name is written in the customer's languages, so the row is read in the one asked for —
    /// and in something rather than nothing when that one is missing.
    /// </summary>
    [Fact]
    public void ARowIsNamedInTheLanguageThatWasAskedFor()
    {
        var document = TemplateYaml.Read(First, """
            tabs:
              Parent:
                name:
                  nl-NL: Ouder
                  en-US: Parent
              Beta:
                name:
                  en-US: Beta
              Plain:
                name: The only name
              Naamloos:
                parent: Parent
            """);

        Assert.Equal("Ouder", Entry(document, "Parent").Label("nl-NL"));
        Assert.Equal("Parent", Entry(document, "Parent").Label("en-US"));

        // No Dutch name written: any name beats no name, and the row still reads as a screen.
        Assert.Equal("Beta", Entry(document, "Beta").Label("nl-NL"));

        // One plain name is filed under no language at all, and answers whoever asks.
        Assert.Equal("The only name", Entry(document, "Plain").Label("nl-NL"));

        // Nothing written anywhere: the key, which is at least what the files call it.
        Assert.Equal("Naamloos", Entry(document, "Naamloos").Label("nl-NL"));
    }

    /// <summary>
    /// With no language asked for, the row reads the one the file wrote first.
    /// </summary>
    /// <remarks>
    /// The names are written in a fixed order across a whole folder, and the first is the language
    /// the application was written for — so it is the one a developer reading the tree recognises
    /// from the screen. Ordering the tags instead would put whichever sorts first in front, which
    /// is a language nobody chose.
    /// </remarks>
    [Fact]
    public void AnUnaskedRowReadsTheNameTheFileWroteFirst()
    {
        var document = TemplateYaml.Read(First, """
            tabs:
              Parent:
                name:
                  nl-NL: Ouder
                  de-DE: Elternteil
                  en-US: Parent
            """);

        Assert.Equal("Ouder", Entry(document, "Parent").Label(null));
        Assert.Equal("Ouder", Entry(document, "Parent").Label(string.Empty));

        // And a language that was asked for and is not there falls back the same way.
        Assert.Equal("Ouder", Entry(document, "Parent").Label("fr-FR"));
    }

    [Fact]
    public void AnEntrySitsUnderTheEntryItNames()
    {
        var set = Set("""
            tabs:
              Root:
                name: Wortel
              Parent:
                name: Ouder
                parent: Root
              Detail:
                name: Alpha page
                parent: Parent
            """);

        var root = Assert.Single(set.Roots);
        Assert.Equal("Root", root.Key);

        var products = Assert.Single(set.Children("Root"));
        Assert.Equal("Parent", products.Key);
        Assert.Equal("Detail", Assert.Single(set.Children("Parent")).Key);
    }

    /// <summary>
    /// A parent nothing declares is a broken reference, and the tree is where somebody would
    /// notice it. Dropping the row would hide the only evidence there is.
    /// </summary>
    [Fact]
    public void AnEntryWhoseParentNobodyDeclaredIsShownAtTheTop()
    {
        var set = Set("""
            tabs:
              Beta:
                name: Beta
                parent: NiemandDeclareertDit
            """);

        Assert.Equal("Beta", Assert.Single(set.Roots).Key);
    }

    /// <summary>
    /// Two entries naming each other is a mistake somebody will make, and it is the kind that
    /// costs the whole view rather than one row.
    /// </summary>
    [Fact]
    public void EntriesThatNameEachOtherDoNotDisappear()
    {
        var set = Set("""
            tabs:
              Left:
                parent: Right
              Right:
                parent: Left
            """);

        Assert.Equal(["Left", "Right"], set.Roots.Select(entry => entry.Key).Order());
    }

    /// <summary>
    /// The merge the application performs, and the reason a single file is a misleading thing to
    /// read: the file that introduces an entry decides its name and its place, and every later one
    /// adds to it.
    /// </summary>
    [Fact]
    public void AlaterFileAddsToAnEntryWithoutTakingItOver()
    {
        var set = TemplateSet.Build(@"C:\src\Web",
        [
            TemplateYaml.Read(First, """
                tabs:
                  Beta:
                    name: Beta
                    parent: Root
                    modules:
                      - type: Alpha
                  Root:
                    name: Wortel
                """),
            TemplateYaml.Read(Second, """
                tabs:
                  Beta:
                    name: A different name
                    parent: Elders
                    modules:
                      - type: Gamma
                """),
        ]);

        var orders = set.Entry("Beta");

        Assert.NotNull(orders);
        Assert.Equal("Beta", orders.Label(null));
        Assert.Equal("Root", orders.Parent);
        Assert.Equal(First, orders.Site.FilePath);

        // The one thing that accumulates: a later file adding a module to a screen is the whole
        // point of the arrangement.
        Assert.Equal(["Alpha", "Gamma"], orders.Modules.Select(module => module.Type));
    }

    /// <summary>
    /// A folder of two hundred files has a bad one in it sooner or later. Losing the other
    /// hundred and ninety-nine over it would be the wrong trade.
    /// </summary>
    [Fact]
    public void AFileThatCannotBeParsedCostsOnlyItsOwnDeclarations()
    {
        var set = TemplateSet.Build(@"C:\src\Web",
        [
            TemplateYaml.Read(First, "tabs:\n  Beta:\n    name: Beta\n"),
            TemplateYaml.Read(Second, "tabs:\n\tBeta_Broken:\n"),
        ]);

        Assert.Equal("Beta", Assert.Single(set.Roots).Key);
        Assert.Contains("2-second.yml", Assert.Single(set.Errors), StringComparison.Ordinal);
    }

    /// <summary>
    /// The control a reader means by "the implementation" is the one an ordinary visitor sees, and
    /// it is rarely the first one written — an editor is as often declared above it.
    /// </summary>
    [Fact]
    public void TheViewControlIsThePreferredImplementationHoweverLateItIsWritten()
    {
        var set = Set("""
            modules:
              Alpha:
                name: Alpha module
                controls:
                  edit:
                    level: edit
                    path: Modules/Alpha/Edit.ascx
                  default:
                    level: view
                    path: Modules/Alpha/View.ascx
            """);

        Assert.Equal("Modules/Alpha/View.ascx", set.Module("Alpha")?.View?.Path);
    }

    /// <summary>A module declaring only an editor still has something to open.</summary>
    [Fact]
    public void AModuleWithNoViewFallsBackToTheControlItDoesDeclare()
    {
        var set = Set("""
            modules:
              Epsilon:
                controls:
                  edit:
                    level: edit
                    path: Modules/Epsilon/Edit.ascx
            """);

        Assert.Equal("Modules/Epsilon/Edit.ascx", set.Module("Epsilon")?.View?.Path);
    }

    /// <summary>
    /// A module named by the package it ships in resolves to the module of that name, and one
    /// named by nothing at all resolves to nothing rather than to whatever was nearest.
    /// </summary>
    [Fact]
    public void AQualifiedModuleNameStillFindsTheModule()
    {
        var set = Set("""
            modules:
              Alpha:
                name: Alpha module
            """);

        Assert.NotNull(set.Module("Widgets.Alpha"));
        Assert.Null(set.Module("Widgets.SomethingElse"));
        Assert.Null(set.Module("Alphaing"));
    }

    // ---- The two that are about the disk ---------------------------------------------------

    /// <summary>
    /// A control path is written relative to the root the application serves, which is where the
    /// template folder lives.
    /// </summary>
    [Fact]
    public void AControlPathResolvesAgainstTheApplicationRoot()
    {
        using var web = new Sandbox();

        web.Write(@"Modules\Alpha\View.ascx", "<%@ Control %>");

        var set = TemplateSet.Build(web.Root, []);

        Assert.Equal(
            Path.Combine(web.Root, "Modules", "Alpha", "View.ascx"),
            set.Resolve("Modules/Alpha/View.ascx"));

        Assert.Null(set.Resolve("Modules/Alpha/Missing.ascx"));
    }

    /// <summary>
    /// These files are read from a workspace and are not always the reader's own, so a path that
    /// climbs out of the root it is resolved against is refused rather than followed.
    /// </summary>
    [Fact]
    public void APathThatClimbsOutOfTheApplicationIsRefused()
    {
        using var web = new Sandbox();

        web.Write(@"Secret.ascx", "<%@ Control %>");

        var set = TemplateSet.Build(Path.Combine(web.Root, "App"), []);

        Assert.Null(set.Resolve("../Secret.ascx"));
        Assert.Null(set.Resolve(@"C:\Windows\System32\drivers\etc\hosts"));
    }

    /// <summary>
    /// The folder is found beside the project file, and the files in it come back in the order the
    /// application reads them — which is the numeric prefix, not the file name. Ordered by name,
    /// <c>100-</c> sorts before <c>2-</c> and the wrong file gets credit for a declaration.
    /// </summary>
    [Fact]
    public void TheFolderIsFoundBesideTheProjectAndReadInItsOwnOrder()
    {
        using var web = new Sandbox();

        web.Write(@"Web.csproj", "<Project />");
        web.Write(@"App_Data\Templates\100-late.yml", "tabs:\n");
        web.Write(@"App_Data\Templates\2-early.yml", "tabs:\n");
        web.Write(@"App_Data\Templates\nested\3-middle.yml", "tabs:\n");

        var roots = TemplateRoots.Of(
            [(Path.Combine(web.Root, "Web.csproj"), "Web")],
            ["App_Data/Templates", "App_Data/TemplatesCustom"]);

        var root = Assert.Single(roots);
        Assert.Equal(web.Root, root.ContentRoot);
        Assert.Equal("Web", root.ProjectName);

        Assert.Equal(
            ["2-early.yml", "3-middle.yml", "100-late.yml"],
            TemplateRoots.Files(root).Select(Path.GetFileName));
    }

    /// <summary>
    /// A quarter of the modules a template folder hosts are named by it and described somewhere
    /// else entirely, so the row for those screens would have a Definition and no Implementation.
    /// The folder named after the module is where the missing half is.
    /// </summary>
    [Fact]
    public void AModuleTheTemplatesNeverDeclaredIsFoundByTheFolderItLivesIn()
    {
        using var web = new Sandbox();

        web.Write(@"DesktopModules\Widgets\Alpha\Alpha_View.ascx", "<%@ Control %>");
        web.Write(@"DesktopModules\Widgets\Alpha\Alpha_Edit.ascx", "<%@ Control %>");

        var set = Controls(web);

        Assert.Equal(
            Path.Combine(web.Root, "DesktopModules", "Widgets", "Alpha", "Alpha_View.ascx"),
            set.Control("Widgets.Alpha"));

        Assert.Equal(set.Control("Widgets.Alpha"), set.Control("Alpha"));
        Assert.Null(set.Control("SomethingNobodyInstalled"));
    }

    /// <summary>
    /// A folder is named after the registration and its control after the thing being rendered, so
    /// the two do not always agree. One candidate is not a guess.
    /// </summary>
    [Fact]
    public void AFolderNamedForTheRegistrationStillFindsItsOneView()
    {
        using var web = new Sandbox();

        web.Write(@"DesktopModules\Widgets\Beta_Registered\Beta_View.ascx", "<%@ Control %>");
        web.Write(@"DesktopModules\Widgets\Beta_Registered\Beta_Settings.ascx", "<%@ Control %>");

        Assert.Equal(
            Path.Combine(web.Root, "DesktopModules", "Widgets", "Beta_Registered", "Beta_View.ascx"),
            Controls(web).Control("Beta_Registered"));
    }

    /// <summary>Two candidates is a guess, and opening the wrong screen is worse than opening none.</summary>
    [Fact]
    public void TwoViewsInAFolderAreNotAnAnswer()
    {
        using var web = new Sandbox();

        web.Write(@"DesktopModules\Widgets\Beta_Registered\Beta_View.ascx", "<%@ Control %>");
        web.Write(@"DesktopModules\Widgets\Beta_Registered\Second_View.ascx", "<%@ Control %>");

        Assert.Null(Controls(web).Control("Beta_Registered"));
    }

    /// <summary>A project with no such folder is not a root, and nothing was read to find out.</summary>
    [Fact]
    public void AProjectWithNoTemplateFolderIsNotARoot()
    {
        using var web = new Sandbox();

        web.Write(@"Library.csproj", "<Project />");

        Assert.Empty(TemplateRoots.Of(
            [(Path.Combine(web.Root, "Library.csproj"), "Library")], ["App_Data/Templates"]));
    }

    // ---- The harness -------------------------------------------------------------------------

    private static TemplateSet Controls(Sandbox web) =>
        TemplateSet.Build(web.Root, [], ["DesktopModules"]);

    private static TemplateSet Set(string yaml) =>
        TemplateSet.Build(@"C:\src\Web", [TemplateYaml.Read(First, yaml)]);

    private static TemplateEntry Entry(TemplateDocument document, string key) =>
        document.Entries.Single(entry => entry.Key == key);

    private sealed class Sandbox : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), "roslyn-sense-tests", $"templates-{Guid.NewGuid():N}");

        public void Write(string relativePath, string content)
        {
            string path = Path.Combine(
                Root,
                relativePath.Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar));

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
