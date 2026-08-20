using System.Text.Json;
using Microsoft.CodeAnalysis;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// What the settings page asks the solution: which values a setting can take here, and what a
/// configured call shape actually selects.
/// </summary>
/// <remarks>
/// The reason these answers exist at all is that the three fields naming a call shape fail
/// silently. A misspelled class, a method that is not there and a signature matching no overload
/// all look exactly like a correct entry — they bind nothing, quietly, and the only symptom is a
/// feature that does not work. The page's whole job is to say so while the entry is being written,
/// so these tests are about the saying.
/// </remarks>
public class SettingsAssistTests
{
    private const string Source = """
        namespace Contoso.Web
        {
            public class ModuleBase
            {
                public string GetString(string key) => key;

                public string GetString(string key, params object[] arguments) => key;

                public string GetString(bool local, string key) => key;

                public string Unrelated(int count) => "";
            }

            public class ProductModule : ModuleBase
            {
            }
        }
        """;

    // ---- The call shape ---------------------------------------------------------------------

    [Fact]
    public async Task TheOverloadsAMethodNameSelectsAreAllReported()
    {
        var result = await ShapeAsync(new MemberShapeParams(
            "Contoso.Web.ModuleBase", "GetString"));

        Assert.Null(result.Problem);
        Assert.Equal(3, result.Matches.Length);

        // With no signature given, every overload matches — which is exactly the thing that makes
        // a lookup written this way bind three different calls.
        Assert.All(result.Matches, match => Assert.True(match.Matched));
    }

    [Fact]
    public async Task ASignatureSaysWhichOverloadIsMeantAndWhichAreNot()
    {
        var result = await ShapeAsync(new MemberShapeParams(
            "Contoso.Web.ModuleBase", "GetString", ["System.Boolean", "System.String"]));

        var matched = Assert.Single(result.Matches, match => match.Matched);

        Assert.Equal("GetString(bool local, string key)", matched.Signature);

        // The others are still listed. "One of three" is the fact the page exists to show, and it
        // cannot be shown by returning only the one.
        Assert.Equal(3, result.Matches.Length);
    }

    /// <summary>
    /// The parameters, named, so choosing which one carries the key is a click rather than
    /// counting commas in a signature nobody has open.
    /// </summary>
    [Fact]
    public async Task AMatchedOverloadNamesItsParametersInOrder()
    {
        var result = await ShapeAsync(new MemberShapeParams(
            "Contoso.Web.ModuleBase", "GetString", ["System.Boolean", "System.String"]));

        var matched = result.Matches.Single(match => match.Matched);

        Assert.Equal(["local", "key"], matched.Parameters.Select(p => p.Name));
        Assert.Equal(["bool", "string"], matched.Parameters.Select(p => p.Type));
    }

    /// <summary>Both spellings of a built-in, the same as the binding rules accept.</summary>
    [Theory]
    [InlineData("string")]
    [InlineData("System.String")]
    public async Task AParameterTypeIsAcceptedInEitherSpelling(string spelling)
    {
        var result = await ShapeAsync(new MemberShapeParams(
            "Contoso.Web.ModuleBase", "GetString", [spelling]));

        var matched = result.Matches.Single(match => match.Matched);

        Assert.Equal("GetString(string key)", matched.Signature);
    }

    [Fact]
    public async Task AWildcardStandsForOneParameterOfAnyType()
    {
        var result = await ShapeAsync(new MemberShapeParams(
            "Contoso.Web.ModuleBase", "GetString", ["*", "System.String"]));

        var matched = result.Matches.Single(match => match.Matched);

        Assert.Equal("GetString(bool local, string key)", matched.Signature);
    }

    /// <summary>
    /// A lookup naming a base class is reached from every type that derives from it, so someone
    /// who typed the derived one should still be shown the member they meant.
    /// </summary>
    [Fact]
    public async Task AMemberInheritedFromABaseClassIsFoundOnTheDerivedOne()
    {
        var result = await ShapeAsync(new MemberShapeParams(
            "Contoso.Web.ProductModule", "GetString", ["System.String"]));

        Assert.Contains(result.Matches, match => match.Matched);
        Assert.Equal("Contoso.Web.ModuleBase", result.Matches[0].DeclaredBy);
    }

    [Fact]
    public async Task AMisspelledClassIsSaidToBeMisspelledRatherThanBindingNothing()
    {
        var result = await ShapeAsync(new MemberShapeParams("Contoso.Web.ModuleBse", "GetString"));

        Assert.Empty(result.Matches);
        Assert.NotNull(result.Problem);

        // And the near miss is offered, because the failure is almost always a typo or a namespace.
        Assert.Contains("Contoso.Web.ModuleBase", result.TypeSuggestions);
    }

    [Fact]
    public async Task AMethodThatIsNotThereIsSaidToBeMissing()
    {
        var result = await ShapeAsync(new MemberShapeParams("Contoso.Web.ModuleBase", "GetSring"));

        Assert.Empty(result.Matches);
        Assert.Contains("GetSring", result.Problem!, StringComparison.Ordinal);

        // With the names it does declare, which is what the field's completion list shows.
        Assert.Contains("GetString", result.MemberSuggestions);
        Assert.Contains("Unrelated", result.MemberSuggestions);
    }

    [Fact]
    public async Task AClassOnItsOwnOffersTheMembersItDeclares()
    {
        var result = await ShapeAsync(new MemberShapeParams("Contoso.Web.ModuleBase"));

        Assert.Equal("Contoso.Web.ModuleBase", result.ResolvedType);
        Assert.Contains("GetString", result.MemberSuggestions);
        Assert.Empty(result.Matches);
    }

    /// <summary>
    /// Leaving the class empty is the documented escape hatch, not a mistake, so it is explained
    /// rather than reported.
    /// </summary>
    [Fact]
    public async Task NoClassAtAllIsExplainedRatherThanTreatedAsAnError()
    {
        var result = await ShapeAsync(new MemberShapeParams(ContainingType: "", MemberName: "GetString"));

        Assert.Contains("any type", result.Problem!, StringComparison.OrdinalIgnoreCase);
    }

    // ---- The values a setting can take -------------------------------------------------------

    /// <summary>
    /// Fallbacks name root conventions, and which ones exist is the preset plus whatever the file
    /// adds — an answer per solution, which is why the schema cannot carry it.
    /// </summary>
    [Fact]
    public void TheFallbacksOfferedAreThePresetSPlusTheFileSOwn()
    {
        var config = JsonDocument.Parse("""
            {
                "resources": {
                    "preset": "dnn",
                    "conventions": [
                        { "id": "custom", "siblingFolder": "App_CustomResources" }
                    ]
                }
            }
            """).RootElement;

        var choices = SettingsAssistHandler.Choices(
            new SettingChoicesParams("resources.lookups[].fallbacks", config)).Items;

        var ids = choices.Select(choice => choice.Value).ToList();

        Assert.Contains("custom", ids);          // the file's own
        Assert.Contains("local", ids);           // the preset's
        Assert.Contains("localShared", ids);

        // Named by where they look, since the id alone says nothing to whoever did not write it.
        Assert.Contains(
            "App_CustomResources", choices.Single(c => c.Value == "custom").Detail!,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A half-written file is the normal state of one being edited. The preset's conventions are
    /// still worth offering.
    /// </summary>
    [Fact]
    public void AConfigThatDoesNotParseStillOffersThePresetSConventions()
    {
        var config = JsonDocument.Parse("""{ "resources": { "conventions": "not a list" } }""")
            .RootElement;

        var choices = SettingsAssistHandler.Choices(
            new SettingChoicesParams("resources.lookups[].fallbacks", config)).Items;

        Assert.NotEmpty(choices);
    }

    [Fact]
    public void ASettingWithNoDynamicValuesAnswersWithNothing()
    {
        var choices = SettingsAssistHandler.Choices(
            new SettingChoicesParams("tools.webForms")).Items;

        Assert.Empty(choices);
    }

    // ---- Building the pieces ------------------------------------------------------------------

    private static Task<MemberShapeResult> ShapeAsync(MemberShapeParams p)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();

        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId, VersionStamp.Default, "Application", "Application", LanguageNames.CSharp,
                metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]))
            .AddDocument(
                DocumentId.CreateNewId(projectId), "Modules.cs", Source,
                filePath: @"C:\src\Modules.cs");

        return SettingsAssistHandler.MemberShapeAsync(solution.GetProject(projectId)!.Solution, p, default);
    }
}
