using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages;
using RoslynMCP.Languages.WebConfig;
using RoslynMCP.Languages.WebConfig.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using Xunit;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;
using LspRange = RoslynMCP.Lsp.Protocol.Range;

namespace RoslynMCP.Tests;

/// <summary>
/// The reader half of the web.config pack: text in, entries with spans out. No workspace.
/// </summary>
public class WebConfigReaderTests
{
    private static SourceText Text(string xml) => SourceText.From(xml);

    [Fact]
    public void ReadsBothSectionsWithTheirValues()
    {
        var entries = WebConfigReader.Read(Text("""
            <configuration>
              <appSettings>
                <add key="CdnRoot" value="https://cdn.example.test" />
              </appSettings>
              <connectionStrings>
                <add name="Main" connectionString="Server=." providerName="System.Data.SqlClient" />
              </connectionStrings>
            </configuration>
            """), "web.config");

        var setting = Assert.Single(entries, e => e.Section == WebConfigSection.AppSettings);
        Assert.Equal("CdnRoot", setting.Name);
        Assert.Equal("https://cdn.example.test", setting.Value);

        var connection = Assert.Single(entries, e => e.Section == WebConfigSection.ConnectionStrings);
        Assert.Equal("Main", connection.Name);
        Assert.Equal("Server=.", connection.Value);
        Assert.Equal("System.Data.SqlClient", connection.Provider);
    }

    [Fact]
    public void NameSpanCoversTheNameWithoutQuotes()
    {
        const string xml = """<configuration><appSettings><add key="Title" value="x" /></appSettings></configuration>""";

        var entry = Assert.Single(WebConfigReader.Read(Text(xml), "web.config"));

        Assert.Equal("Title", xml.Substring(entry.NameSpan.Start, entry.NameSpan.Length));
    }

    [Fact]
    public void AHalfTypedDocumentStillAnswersWithTheReadableEntries()
    {
        // The state of a file being edited. XDocument stops at the break and answers with
        // nothing; a full-fidelity parse answers with what it could read.
        var entries = WebConfigReader.Read(Text("""
            <configuration>
              <appSettings>
                <add key="Done" value="1" />
                <add key="Typing
            """), "web.config");

        Assert.Contains(entries, e => e.Name == "Done");
    }

    [Fact]
    public void AnEntityInTheNameIsDecodedAndItsSpanWithheld()
    {
        var entry = Assert.Single(WebConfigReader.Read(Text(
            """<configuration><appSettings><add key="A&amp;B" value="x" /></appSettings></configuration>"""),
            "web.config"));

        // Decoded, because this is what a C# literal is compared against — and unspanned,
        // because the written form is four characters longer than the decoded one.
        Assert.Equal("A&B", entry.Name);
        Assert.Equal(default, entry.NameSpan);
    }

    [Fact]
    public void EntriesInsideALocationStillCount()
    {
        var entries = WebConfigReader.Read(Text("""
            <configuration>
              <location path="Admin">
                <appSettings>
                  <add key="Scoped" value="1" />
                </appSettings>
              </location>
            </configuration>
            """), "web.config");

        Assert.Contains(entries, e => e.Name == "Scoped");
    }

}

/// <summary>
/// The override chain over real files: nearer declarations win, and the whole chain is still
/// reachable as declarations.
/// </summary>
public class WebConfigChainTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "roslynsense-webconfig-" + Guid.NewGuid().ToString("N"));

    public WebConfigChainTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Admin"));

        File.WriteAllText(Path.Combine(_root, "Site.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        File.WriteAllText(Path.Combine(_root, "web.config"), """
            <configuration>
              <appSettings>
                <add key="Theme" value="light" />
                <add key="OnlyAtRoot" value="1" />
              </appSettings>
            </configuration>
            """);

        File.WriteAllText(Path.Combine(_root, "Admin", "web.config"), """
            <configuration>
              <appSettings>
                <add key="Theme" value="dark" />
              </appSettings>
            </configuration>
            """);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void TheNearestDeclarationWins()
    {
        var merged = WebConfigSettings.Merged(
            Path.Combine(_root, "Admin", "Page.aspx"),
            Path.Combine(_root, "Site.csproj"),
            WebConfigSection.AppSettings);

        Assert.Equal("dark", WebConfigSettings.Find(merged, "Theme")!.Value.Value);

        // Inherited, not replaced: an override of one key does not hide the rest of the file.
        Assert.Equal("1", WebConfigSettings.Find(merged, "OnlyAtRoot")!.Value.Value);
    }

    [Fact]
    public void AFileAtTheRootSeesOnlyTheRoot()
    {
        var merged = WebConfigSettings.Merged(
            Path.Combine(_root, "Default.aspx"),
            Path.Combine(_root, "Site.csproj"),
            WebConfigSection.AppSettings);

        Assert.Equal("light", WebConfigSettings.Find(merged, "Theme")!.Value.Value);
    }

    [Fact]
    public void BothDeclarationsAreReachableFromTheProject()
    {
        var locations = WebConfigReferenceService.Declarations(
            Path.Combine(_root, "Site.csproj"), WebConfigSection.AppSettings, "Theme");

        Assert.Equal(2, locations.Length);
    }

    [Fact]
    public void TheRootDeclarationKnowsTheDirectoryThatReplacesIt()
    {
        var chain = WebConfigOverrides.ChainFor(
            Path.Combine(_root, "Site.csproj"), WebConfigSection.AppSettings, "Theme");

        var builder = new StringBuilder();
        ConfigOverrides.Append(builder, chain, Path.Combine(_root, "web.config"));
        string hover = builder.ToString();

        Assert.Contains("Overridden in:", hover);
        Assert.Contains(@"`Admin\web.config` → `dark`", hover);
        Assert.DoesNotContain("Overrides:", hover);
    }

    [Fact]
    public void TheNestedDeclarationKnowsWhatItReplaced()
    {
        var chain = WebConfigOverrides.ChainFor(
            Path.Combine(_root, "Site.csproj"), WebConfigSection.AppSettings, "Theme");

        var builder = new StringBuilder();
        ConfigOverrides.Append(builder, chain, Path.Combine(_root, "Admin", "web.config"));
        string hover = builder.ToString();

        Assert.Contains("Overrides:", hover);
        Assert.Contains("`web.config` → `light`", hover);
        Assert.DoesNotContain("Overridden in:", hover);
    }

    [Fact]
    public void ANameOnlyOneFileDeclaresGetsNoOverrideLens()
    {
        var chain = WebConfigOverrides.ChainFor(
            Path.Combine(_root, "Site.csproj"), WebConfigSection.AppSettings, "OnlyAtRoot");

        var range = new LspRange(new Position(0, 0), new Position(0, 0));

        Assert.Empty(ConfigOverrides.Lenses(chain, Path.Combine(_root, "web.config"), range));
    }

    [Fact]
    public void TheOverrideLensPointsDownFromTheRootAndUpFromTheOverride()
    {
        var chain = WebConfigOverrides.ChainFor(
            Path.Combine(_root, "Site.csproj"), WebConfigSection.AppSettings, "Theme");

        var range = new LspRange(new Position(0, 0), new Position(0, 0));

        var down = Assert.Single(ConfigOverrides.Lenses(chain, Path.Combine(_root, "web.config"), range));
        Assert.Equal("↓ overridden", down.Command!.Title);

        var up = Assert.Single(
            ConfigOverrides.Lenses(chain, Path.Combine(_root, "Admin", "web.config"), range));
        Assert.Equal("↑ overrides", up.Command!.Title);

        // The peek opens on the line the lens sits on and lists the other declaration beside it.
        Assert.Equal("roslynSense.showReferences", up.Command.Name);
        Assert.Single((LspLocation[])up.Command.Arguments![3]);
    }

    [Fact]
    public void BuildOutputIsNotSearchedForConfigFiles()
    {
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        File.WriteAllText(Path.Combine(_root, "bin", "web.config"), "<configuration />");

        var files = WebConfigSettings.ConfigFilesFor(Path.Combine(_root, "Site.csproj"));

        Assert.DoesNotContain(files, path => path.Contains($"bin{Path.DirectorySeparatorChar}"));
    }
}

/// <summary>
/// The joined half: the ASPX fixture's C# and markup against its <c>web.config</c> — the usage
/// index, the lens counts, and references from a C# literal.
/// </summary>
/// <remarks>
/// In the shared-state collection because the fixture project loads through
/// <see cref="RoslynMCP.Services.WorkspaceService"/>, a process-wide cache.
/// </remarks>
[Collection(SharedState.Name)]
public class WebConfigLanguageTests
{
    private static Task<WebConfigView?> ViewAsync() =>
        WebConfigWorkspace.GetAsync(FixturePaths.AspxWebConfigFile, default);

    // ---- The indexes -----------------------------------------------------------------------

    [Fact]
    public async Task TheIndexSeesEveryReadShape()
    {
        var view = await ViewAsync();
        Assert.NotNull(view);
        Assert.NotNull(view!.Project);

        // The indexer on ConfigurationManager and on WebConfigurationManager, and .Get.
        Assert.Equal(2, view.Index.UsagesFor(WebConfigSection.AppSettings, "CdnRoot").Count());
        Assert.Single(view.Index.UsagesFor(WebConfigSection.AppSettings, "RetryCount"));
        Assert.Single(view.Index.UsagesFor(WebConfigSection.ConnectionStrings, "Main"));
    }

    [Fact]
    public async Task ACollectionNamedAppSettingsOnSomeoneElsesTypeIsNotAConfigurationRead()
    {
        var view = await ViewAsync();
        Assert.NotNull(view);

        // SettingsReader.Decoy reads Local.AppSettings["CdnRoot"]. The name matches; the
        // declaring type does not, so it must not be counted as a read of this file.
        Assert.DoesNotContain(
            view!.Index.UsagesFor(WebConfigSection.AppSettings, "CdnRoot"),
            usage => usage.FilePath.EndsWith("SettingsReader.cs", StringComparison.OrdinalIgnoreCase)
                && usage.LineSpan.Start.Line
                    == LineOf(FixturePaths.AspxSettingsReaderFile, "Local.AppSettings"));
    }

    [Fact]
    public async Task MarkupExpressionBuildersCountAsReads()
    {
        var view = await ViewAsync();
        Assert.NotNull(view);

        var markup = view!.MarkupUsages
            .Where(u => u.FilePath.EndsWith("Settings.aspx", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Contains(markup, u => u.Section == WebConfigSection.AppSettings && u.Name == "CdnRoot");

        // `Main` and `Main.ProviderName` are two reads of one entry, not two entries.
        Assert.Equal(2, markup.Count(u =>
            u.Section == WebConfigSection.ConnectionStrings && u.Name == "Main"));
    }

    // ---- CodeLens --------------------------------------------------------------------------

    [Fact]
    public async Task ASettingCountsItsCSharpAndItsMarkupReads()
    {
        // Two C# reads plus one <%$ AppSettings: CdnRoot %>.
        Assert.Equal("3 references", await LensTitleAsync("CdnRoot"));
    }

    [Fact]
    public async Task AConnectionStringCountsItsProviderReadToo()
    {
        // One C# read, plus `Main` and `Main.ProviderName` in markup.
        Assert.Equal("3 references", await LensTitleAsync("Main"));
    }

    [Fact]
    public async Task ASettingNothingReadsSaysZero()
    {
        // The one read of it goes through Config.GetLocal, which reads a collection of its own —
        // so the wrapper pass must not turn a lookalike into a reference.
        Assert.Equal("0 references", await LensTitleAsync("DeadSetting"));
    }

    [Fact]
    public async Task ASettingReadThroughTheProjectsOwnMethodStillCountsItsCallers()
    {
        // WrappedSetting is named nowhere near ConfigurationManager: the only mention of it in the
        // solution is Config.GetSetting("WrappedSetting").
        Assert.Equal("1 reference", await LensTitleAsync("WrappedSetting"));
    }

    [Fact]
    public async Task AConnectionStringReadThroughTheProjectsOwnMethodCountsToo()
    {
        Assert.Equal("1 reference", await LensTitleAsync("WrappedConnection"));
    }

    /// <summary>
    /// The count arrives with the lens, not one resolve later.
    /// </summary>
    /// <remarks>
    /// Not an optimisation — the lens is unclickable until it is commanded, in a way that is
    /// invisible. VS Code keeps drawing the previous list's anchors while a refreshed list is being
    /// resolved, and the key behind those anchors dies with the list they came in; a click in that
    /// window reports a command that does not exist. During a solution load, when a refresh goes
    /// out every few seconds and each resolve waits on the project gate, that window is most of the
    /// time the file is open. The tests above still go through <c>ResolveCodeLensAsync</c>, which
    /// is what holds the two paths to the same answer for a client that resolves anyway.
    /// </remarks>
    [Fact]
    public async Task AConfigLensIsClickableBeforeAnyoneResolvesIt()
    {
        var pack = new WebConfigLanguage();
        string uri = LspConverters.PathToUri(FixturePaths.AspxWebConfigFile);

        var lenses = await pack.CodeLensAsync(new CodeLensParams(new TextDocumentIdentifier(uri)), default);
        var lens = lenses.Single(l => l.Data is { } d && LensName(d) == "CdnRoot");

        Assert.Equal("3 references", lens.Command?.Title);
        Assert.Equal("roslynSense.showReferences", lens.Command?.Name);

        // The peek's own payload, which is the part VS Code drops when the list it came in is
        // replaced — its presence here is what a commanded lens buys.
        var locations = Assert.IsType<LspLocation[]>(lens.Command!.Arguments![3]);
        Assert.Equal(3, locations.Length);
    }

    private static async Task<string?> LensTitleAsync(string name)
    {
        var pack = new WebConfigLanguage();
        string uri = LspConverters.PathToUri(FixturePaths.AspxWebConfigFile);

        var lenses = await pack.CodeLensAsync(new CodeLensParams(new TextDocumentIdentifier(uri)), default);
        var lens = lenses.Single(l => l.Data is { } d && LensName(d) == name);

        return (await pack.ResolveCodeLensAsync(lens, default)).Command!.Title;
    }

    private static string? LensName(CodeLensData data)
    {
        if (WebConfigDocumentCache.Get(FixturePaths.AspxWebConfigFile) is not { } document)
            return null;

        int offset = LspConverters.ToOffset(document.Text, new Position(data.Line, data.Character));
        return document.EntryAt(offset)?.Name;
    }

    // ---- References from the C# side -------------------------------------------------------

    [Fact]
    public async Task FindReferencesOnALiteralAnswersWithTheEntryAndEveryReader()
    {
        var view = await ViewAsync();
        Assert.NotNull(view!.Project);

        var pack = new WebConfigLanguage();

        var locations = await pack.ReferencesAsync(
            FixturePaths.AspxSettingsReaderFile,
            OffsetOf(FixturePaths.AspxSettingsReaderFile, "\"CdnRoot\"") + 1,
            view.Project,
            default);

        Assert.NotNull(locations);

        // The declaration in web.config, both C# reads, and the markup one.
        Assert.Contains(locations!, l => l.Uri.EndsWith("web.config", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(locations!, l => l.Uri.EndsWith("Settings.aspx", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(4, locations!.Count);
    }

    [Fact]
    public async Task ALiteralThatIsNotASettingNameIsNotOwned()
    {
        var view = await ViewAsync();
        Assert.NotNull(view!.Project);

        var pack = new WebConfigLanguage();

        // Inside the decoy read, whose collection is someone else's property. Null, not empty:
        // the request has to fall through to the ordinary handlers.
        var locations = await pack.ReferencesAsync(
            FixturePaths.AspxSettingsReaderFile,
            OffsetOf(FixturePaths.AspxSettingsReaderFile, "Local.AppSettings[\"CdnRoot\"") + 20,
            view.Project,
            default);

        Assert.Null(locations);
    }

    // ---- F12 from the C# side ----------------------------------------------------------------

    [Fact]
    public async Task F12OnASettingLiteralLandsOnTheEntryInWebConfig()
    {
        var (pack, context) = await EmbeddedAsync("\"CdnRoot\"");

        var location = Assert.Single(
            await pack.DefinitionAsync(context, typeDefinition: false, default));

        Assert.EndsWith("web.config", location.Uri, StringComparison.OrdinalIgnoreCase);

        // On the key attribute's value, not on the head of the file.
        var document = WebConfigDocumentCache.Get(FixturePaths.AspxWebConfigFile);
        var entry = document!.Find(WebConfigSection.AppSettings, "CdnRoot");
        Assert.Equal(
            LspConverters.ToRange(document.Text.Lines, entry!.Value.NameSpan), location.Range);
    }

    [Fact]
    public async Task F12OnAConnectionStringLiteralLandsOnItsEntry()
    {
        var (pack, context) = await EmbeddedAsync("\"Main\"");

        var location = Assert.Single(
            await pack.DefinitionAsync(context, typeDefinition: false, default));

        var document = WebConfigDocumentCache.Get(FixturePaths.AspxWebConfigFile);
        var entry = document!.Find(WebConfigSection.ConnectionStrings, "Main");
        Assert.Equal(
            LspConverters.ToRange(document.Text.Lines, entry!.Value.NameSpan), location.Range);
    }

    [Fact]
    public async Task ALiteralOnSomeoneElsesCollectionIsNotClaimed()
    {
        var document = await CSharpDocumentAsync();
        var model = await document.GetSemanticModelAsync(default);
        var root = await document.GetSyntaxRootAsync(default);
        var text = await document.GetTextAsync(default);

        // The decoy read: Local.AppSettings["CdnRoot"].
        int index = text.ToString().IndexOf("Local.AppSettings[\"CdnRoot\"", StringComparison.Ordinal);
        var token = root!.FindToken(index + "Local.AppSettings[\"".Length);

        Assert.Null(await new WebConfigLanguage().DetectAsync(document, token, model!, default));
    }

    [Fact]
    public async Task F12FromALiteralPassedToOurOwnReaderLandsOnTheEntry()
    {
        // Config.GetSetting("WrappedSetting") — nothing at the call site names a configuration
        // API, and the literal is still a reference to the <add> that declares it.
        var (pack, context) = await EmbeddedAsync("\"WrappedSetting\"");

        var location = Assert.Single(
            await pack.DefinitionAsync(context, typeDefinition: false, default));

        var document = WebConfigDocumentCache.Get(FixturePaths.AspxWebConfigFile);
        var entry = document!.Find(WebConfigSection.AppSettings, "WrappedSetting");

        Assert.Equal(
            LspConverters.ToRange(document.Text.Lines, entry!.Value.NameSpan), location.Range);
    }

    /// <summary>The pack and a context over the literal, the way the embedded detector builds
    /// one.</summary>
    private static async Task<(WebConfigLanguage Pack, EmbeddedStringContext Context)> EmbeddedAsync(
        string literal)
    {
        var document = await CSharpDocumentAsync();
        var text = await document.GetTextAsync(default);
        var model = await document.GetSemanticModelAsync(default);
        var root = await document.GetSyntaxRootAsync(default);

        int index = text.ToString().IndexOf(literal, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{literal}' is not in {document.Name}");

        var token = root!.FindToken(index + 1);
        var pack = new WebConfigLanguage();

        // Detected the same way the caret path detects it, so the test covers the claim as well
        // as the answer.
        Assert.Equal("WebConfigSetting", await pack.DetectAsync(document, token, model!, default));

        return (pack, new EmbeddedStringContext(
            pack, "WebConfigSetting", [], document, model!, token, token.SpanStart + 1));
    }

    private static async Task<Document> CSharpDocumentAsync()
    {
        var view = await ViewAsync();
        Assert.NotNull(view?.Project);

        return view!.Project!.Documents.Single(d =>
            string.Equals(d.FilePath, FixturePaths.AspxSettingsReaderFile, StringComparison.OrdinalIgnoreCase));
    }

    // ---- Hover -----------------------------------------------------------------------------

    [Fact]
    public async Task HoverOverAnEntryShowsItsValue()
    {
        var pack = new WebConfigLanguage();

        var hover = await pack.HoverAsync(
            new TextDocumentPositionParams(
                new TextDocumentIdentifier(LspConverters.PathToUri(FixturePaths.AspxWebConfigFile)),
                PositionOf(FixturePaths.AspxWebConfigFile, "CdnRoot")),
            default);

        Assert.NotNull(hover);
        Assert.Contains("https://cdn.example.test", hover!.Contents.Value);
    }

    // ---- Completion ------------------------------------------------------------------------

    [Fact]
    public async Task AKeyTheCodeReadsAndTheFileNeverDeclaresIsOffered()
    {
        var list = await CompletionAsync("CdnRoot");
        var items = list.Items.ToDictionary(i => i.Label, i => i.Detail);

        // SettingsReader.Missing reads PageSize; the file has no such entry.
        Assert.Contains("PageSize", items.Keys);
        Assert.Equal("read by this solution", items["PageSize"]);

        // Already declared, so offering it would only duplicate what is on the line.
        Assert.DoesNotContain("CdnRoot", items.Keys);
        Assert.DoesNotContain("RetryCount", items.Keys);

        // The decoy read is on a collection of someone else's; it names no setting.
        Assert.DoesNotContain("Decoy", items.Keys);
    }

    [Fact]
    public async Task TheConnectionStringsSectionIsOfferedItsOwnNames()
    {
        var list = await CompletionAsync("Main");

        // Main is declared and nothing else names a connection string, so there is nothing to
        // add — and certainly not the appSettings key that is missing.
        Assert.DoesNotContain(list.Items, item => item.Label == "PageSize");
    }

    [Fact]
    public async Task AValueAttributeIsNotAPlaceToNameASetting()
    {
        var list = await CompletionAsync("https://cdn.example.test");

        Assert.Empty(list.Items);
    }

    private static Task<CompletionList> CompletionAsync(string needle) =>
        new WebConfigLanguage().CompletionAsync(
            new CompletionParams(
                new TextDocumentIdentifier(LspConverters.PathToUri(FixturePaths.AspxWebConfigFile)),
                PositionOf(FixturePaths.AspxWebConfigFile, needle)),
            new LspResolveCache(), default);

    // ---- Helpers ---------------------------------------------------------------------------

    private static int OffsetOf(string path, string needle)
    {
        string text = File.ReadAllText(path);
        int index = text.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{needle}' is not in {Path.GetFileName(path)}");
        return index;
    }

    private static int LineOf(string path, string needle) =>
        SourceText.From(File.ReadAllText(path)).Lines.GetLinePosition(OffsetOf(path, needle)).Line;

    private static Position PositionOf(string path, string needle)
    {
        var line = SourceText.From(File.ReadAllText(path)).Lines
            .GetLinePosition(OffsetOf(path, needle));

        return new Position(line.Line, line.Character);
    }
}
