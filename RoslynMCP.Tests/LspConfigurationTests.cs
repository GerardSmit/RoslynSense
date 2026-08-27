using System.Text.Json;
using RoslynMCP.Config;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Services.ProjectModel;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The settings path: what the editor sends at initialize and on change, and what it changes.
/// </summary>
/// <remarks>
/// These write process-wide statics, so they restore what they found. They are in the shared
/// collection for the same reason — a parallel test computing diagnostics under a flipped
/// analyzer switch would be measuring the wrong thing.
/// </remarks>
[Collection(SharedState.Name)]
public class LspConfigurationTests
{
    [Fact]
    public void SettingsApplyFromTheClientsOwnSection()
    {
        bool analyzers = LspFeatureOptions.AnalyzerDiagnostics;
        bool codeStyle = LspFeatureOptions.CodeStyleDiagnostics;
        var timeout = LspFeatureOptions.AnalyzerTimeout;
        string scope = LspFeatureOptions.WorkspaceDiagnosticsScope;
        bool sourceLink = LspFeatureOptions.SourceLink;

        try
        {
            bool changed = ConfigurationHandler.Apply(Settings("""
                {
                  "analyzerDiagnostics": false,
                  "codeStyleDiagnostics": false,
                  "analyzerTimeoutSeconds": 42,
                  "workspaceDiagnostics": "solution",
                  "sourceLink": false
                }
                """));

            Assert.True(changed);
            Assert.False(LspFeatureOptions.AnalyzerDiagnostics);
            Assert.False(LspFeatureOptions.CodeStyleDiagnostics);
            Assert.Equal(TimeSpan.FromSeconds(42), LspFeatureOptions.AnalyzerTimeout);
            Assert.Equal("solution", LspFeatureOptions.WorkspaceDiagnosticsScope);
            Assert.False(LspFeatureOptions.SourceLink);
        }
        finally
        {
            LspFeatureOptions.AnalyzerDiagnostics = analyzers;
            LspFeatureOptions.CodeStyleDiagnostics = codeStyle;
            LspFeatureOptions.AnalyzerTimeout = timeout;
            LspFeatureOptions.WorkspaceDiagnosticsScope = scope;
            LspFeatureOptions.SourceLink = sourceLink;
        }
    }

    [Fact]
    public void SettingsApplyWhenWrappedInTheirSectionName()
    {
        // didChangeConfiguration carries the section name; initializationOptions does not.
        bool sourceLink = LspFeatureOptions.SourceLink;
        try
        {
            ConfigurationHandler.Apply(Settings("""{"roslynSense": {"sourceLink": false}}"""));
            Assert.False(LspFeatureOptions.SourceLink);
        }
        finally
        {
            LspFeatureOptions.SourceLink = sourceLink;
        }
    }

    [Fact]
    public void UnchangedSettingsDoNotInvalidateAnything()
    {
        // The return value is what decides whether every cached analyzer result is thrown away;
        // saying "changed" for a no-op change would re-run analyzers on every settings save.
        bool changed = ConfigurationHandler.Apply(Settings($$"""
            {
              "analyzerDiagnostics": {{(LspFeatureOptions.AnalyzerDiagnostics ? "true" : "false")}},
              "analyzerTimeoutSeconds": 5
            }
            """));

        Assert.False(changed);
    }

    [Fact]
    public void TheMarkupGutterIsOffUntilTheEditorAsksForIt()
    {
        bool markupLenses = LspFeatureOptions.WebFormsCodeLens;
        try
        {
            ConfigurationHandler.Apply(Settings("""{"roslynSense": {"webforms": {"codeLens": true}}}"""));
            Assert.True(LspFeatureOptions.WebFormsCodeLens);

            // A client too old to send the section leaves the value where it was, rather than
            // being read as an editor that asked for it off.
            ConfigurationHandler.Apply(Settings("""{"roslynSense": {"sourceLink": true}}"""));
            Assert.True(LspFeatureOptions.WebFormsCodeLens);

            ConfigurationHandler.Apply(Settings("""{"roslynSense": {"webforms": {"codeLens": false}}}"""));
            Assert.False(LspFeatureOptions.WebFormsCodeLens);
        }
        finally
        {
            LspFeatureOptions.WebFormsCodeLens = markupLenses;
        }
    }

    [Fact]
    public void TheEditorCanChooseTheCoreClrEngine()
    {
        var restore = DebugEngineOptions.CoreClr;
        try
        {
            ConfigurationHandler.Apply(Settings("""
                {"roslynSense": {"debugger": {"coreClrEngine": "icordebug"}}}
                """));

            // Off Windows the setting is refused on the way in, the same as it is at startup —
            // asserted here rather than skipped, because the refusal is the behaviour.
            Assert.Equal(
                OperatingSystem.IsWindows()
                    ? CoreClrDebugEngine.IcorDebug
                    : CoreClrDebugEngine.NetCoreDbg,
                DebugEngineOptions.CoreClr);

            ConfigurationHandler.Apply(Settings("""
                {"roslynSense": {"debugger": {"coreClrEngine": "netcoredbg"}}}
                """));
            Assert.Equal(CoreClrDebugEngine.NetCoreDbg, DebugEngineOptions.CoreClr);
        }
        finally
        {
            DebugEngineOptions.CoreClr = restore;
        }
    }

    [Fact]
    public void ASettingsPushThatSaysNothingAboutTheEngineLeavesItAlone()
    {
        // Every push carries the whole section, so a client too old to send this property — or a
        // user who set it in roslynsense.json and never in the editor — must not have the choice
        // reset out from under them on the next keystroke that changes some other setting.
        var restore = DebugEngineOptions.CoreClr;
        var restoreView = DebuggerViewOptions.Current;
        try
        {
            DebugEngineOptions.CoreClr = CoreClrDebugEngine.IcorDebug;

            ConfigurationHandler.Apply(Settings("""
                {"roslynSense": {"debugger": {"justMyCode": false}}}
                """));
            Assert.Equal(CoreClrDebugEngine.IcorDebug, DebugEngineOptions.CoreClr);

            // Nor does a value nobody can read.
            ConfigurationHandler.Apply(Settings("""
                {"roslynSense": {"debugger": {"coreClrEngine": "vsdbg"}}}
                """));
            Assert.Equal(CoreClrDebugEngine.IcorDebug, DebugEngineOptions.CoreClr);
        }
        finally
        {
            DebugEngineOptions.CoreClr = restore;
            DebuggerViewOptions.Current = restoreView;
        }
    }

    [Fact]
    public void MalformedSettingsAreIgnored()
    {
        Assert.False(ConfigurationHandler.Apply(null));
        Assert.False(ConfigurationHandler.Apply(Settings("\"not an object\"")));
        Assert.False(ConfigurationHandler.Apply(Settings("""{"analyzerDiagnostics": "yes please"}""")));
    }

    [Fact]
    public void CustomFileNestingRulesNestWhatTheBuiltInsDoNot()
    {
        try
        {
            int accepted = FileNestingService.SetCustomRules([
                new("*.bicep", "${capture}.bicepparam, ${capture}.parameters.json"),
            ]);
            Assert.Equal(2, accepted);

            var nested = FileNestingService.Nest([
                @"C:\repo\main.bicep",
                @"C:\repo\main.bicepparam",
                @"C:\repo\main.parameters.json",
            ]);

            var parent = Assert.Single(nested);
            Assert.Equal(@"C:\repo\main.bicep", parent.FullPath);
            Assert.Equal(2, parent.Children.Count);
        }
        finally
        {
            FileNestingService.SetCustomRules([]);
        }
    }

    [Fact]
    public void RulesThatCannotBeHonouredAreDroppedRatherThanApproximated()
    {
        try
        {
            int accepted = FileNestingService.SetCustomRules([
                new("main.bicep", "${capture}.bicepparam"),   // parent is not *.ext
                new("*.bicep", "*.bicepparam"),               // child has no ${capture}
                new("*.bicep", "${capture}"),                 // child adds no suffix
            ]);

            Assert.Equal(0, accepted);
        }
        finally
        {
            FileNestingService.SetCustomRules([]);
        }
    }

    [Fact]
    public void NestingCanBeTurnedOff()
    {
        var flat = FileNestingService.Nest(
            [@"C:\repo\Form1.cs", @"C:\repo\Form1.Designer.cs"], enabled: false);

        Assert.Equal(2, flat.Count);
        Assert.All(flat, f => Assert.Empty(f.Children));
    }

    private static JsonElement Settings(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}
