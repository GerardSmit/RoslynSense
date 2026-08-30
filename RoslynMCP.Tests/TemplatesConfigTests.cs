using System.Text.Json;
using RoslynMCP.Config;
using RoslynMCP.Languages.Templates;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The configuration chain behind the templates pack: what an unconfigured solution gets, what a
/// configured one adds, and what a bad entry costs.
/// </summary>
/// <remarks>
/// Less to configure here than in the packs that read C#, because there is no table of names to
/// extend — the folder has a shape of its own. What varies between one solution and the next is
/// where the folder is and whose language the names are in.
/// </remarks>
public class TemplatesConfigTests
{
    /// <summary>
    /// No <c>templates</c> section at all is not a disabled pack: a solution that never mentions
    /// RoslynSense still gets the conventional folders read.
    /// </summary>
    [Fact]
    public void NothingConfiguredIsTheConventionalFolders()
    {
        var templates = Resolve("{}").Templates;

        Assert.True(templates.Enabled);
        Assert.Equal(TemplatesSettings.ConventionalFolders, templates.Folders.ToArray());
        Assert.Equal(TemplatesSettings.ConventionalControlFolders, templates.ControlFolders.ToArray());
        Assert.Null(templates.Locale);
    }

    [Fact]
    public void TheToolsGateTurnsThePackOff()
    {
        Assert.False(Resolve("""{"tools":{"templates":false}}""").Templates.Enabled);
    }

    [Fact]
    public void TheFlagTurnsThePackOff()
    {
        Assert.False(EffectiveSettings.Resolve(["--no-templates"], null, out _).Templates.Enabled);
    }

    /// <summary>A disabled pack looks nowhere, so nothing downstream has to check both.</summary>
    [Fact]
    public void ADisabledPackHasNoFoldersToLookIn()
    {
        var templates = Resolve("""{"tools":{"templates":false},"templates":{"folders":["Content/Screens"]}}""")
            .Templates;

        Assert.False(templates.Enabled);
        Assert.Empty(templates.Folders);
    }

    /// <summary>
    /// A configured folder is an addition rather than a replacement, for the reason every other
    /// pack keeps its shipped table in front of the user's: naming one more place to look is not
    /// asking for the conventional ones to stop being looked at.
    /// </summary>
    [Fact]
    public void AConfiguredFolderIsAdded()
    {
        var templates = Resolve("""{"templates":{"folders":["Content/Screens"]}}""").Templates;

        Assert.Equal([.. TemplatesSettings.ConventionalFolders, "Content/Screens"], templates.Folders.ToArray());
    }

    /// <summary>
    /// Naming a conventional folder again would read it twice, which for a template folder means
    /// every declaration in it merged over itself.
    /// </summary>
    [Fact]
    public void AFolderThatIsAlreadyReadIsNotReadTwice()
    {
        var templates = Resolve("""{"templates":{"folders":["App_Data/Templates"]}}""").Templates;

        Assert.Equal(TemplatesSettings.ConventionalFolders, templates.Folders.ToArray());
    }

    /// <summary>
    /// Every folder here is joined to a project directory, so one naming a drive or climbing out
    /// of the project would take the pack out of the solution — which is not a thing a setting in
    /// a checked-in file should be able to do.
    /// </summary>
    [Fact]
    public void AFolderThatIsNotRelativeToAProjectIsRefused()
    {
        var templates = Resolve(
            """{"templates":{"folders":["C:/Windows","../../elsewhere","  "]}}""",
            out var warnings).Templates;

        Assert.Equal(TemplatesSettings.ConventionalFolders, templates.Folders.ToArray());
        Assert.Equal(3, warnings.Count(warning => warning.StartsWith("templates.", StringComparison.Ordinal)));
    }

    /// <summary>The names in the tree are the customer's words, so which language is a setting.</summary>
    [Fact]
    public void TheLanguageIsWhicheverWasNamed()
    {
        Assert.Equal("nl-NL", Resolve("""{"templates":{"locale":" nl-NL "}}""").Templates.Locale);
        Assert.Null(Resolve("""{"templates":{"locale":"   "}}""").Templates.Locale);
    }

    /// <summary>The other folder list, and the same rules — it is joined to a project too.</summary>
    [Fact]
    public void AConfiguredModuleFolderIsAddedAndCheckedTheSameWay()
    {
        var templates = Resolve(
            """{"templates":{"controlFolders":["Portals/Modules","/etc"]}}""",
            out var warnings).Templates;

        Assert.Equal(
            [.. TemplatesSettings.ConventionalControlFolders, "Portals/Modules"],
            templates.ControlFolders.ToArray());

        Assert.Contains(warnings, warning => warning.Contains("controlFolders", StringComparison.Ordinal));
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
