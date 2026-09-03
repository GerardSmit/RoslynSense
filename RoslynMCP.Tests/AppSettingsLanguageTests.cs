using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages;
using RoslynMCP.Languages.AppSettings;
using RoslynMCP.Languages.AppSettings.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using Xunit;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Tests;

/// <summary>
/// The reader half of the appsettings pack: pure text in, keys with spans out. No workspace.
/// </summary>
public class AppSettingsReaderTests
{
    [Fact]
    public void ReadsNestedKeysWithConfigurationPaths()
    {
        var keys = AppSettingsReader.Read("""
            {
              "App": {
                "Title": "Demo",
                "Retries": 3
              },
              "Flat": true
            }
            """);

        Assert.Contains(keys, k => k.Path == "App" && k.Kind == AppSettingsValueKind.Object);
        Assert.Contains(keys, k => k.Path == "App:Title" && k.Kind == AppSettingsValueKind.String);
        Assert.Contains(keys, k => k.Path == "App:Retries" && k.Kind == AppSettingsValueKind.Number);
        Assert.Contains(keys, k => k.Path == "Flat" && k.Kind == AppSettingsValueKind.Boolean);
    }

    [Fact]
    public void NameSpanCoversTheNameWithoutQuotes()
    {
        const string text = """{ "Title": "x" }""";

        var key = Assert.Single(AppSettingsReader.Read(text));

        Assert.Equal("Title", text.Substring(key.NameSpan.Start, key.NameSpan.Length));
    }

    [Fact]
    public void ToleratesCommentsAndTrailingCommas()
    {
        var keys = AppSettingsReader.Read("""
            {
              // line comment
              "A": 1, /* block */ "B": 2,
            }
            """);

        Assert.Equal(["A", "B"], keys.Select(k => k.Path).Order().ToArray());
    }

    [Fact]
    public void ArrayElementsFlattenToIndexedPaths()
    {
        var keys = AppSettingsReader.Read("""
            { "Hosts": [ { "Name": "a" }, { "Name": "b" } ] }
            """);

        Assert.Contains(keys, k => k.Path == "Hosts:0:Name");
        Assert.Contains(keys, k => k.Path == "Hosts:1:Name");
    }

    [Fact]
    public void AHalfTypedDocumentStillAnswersWithTheRecognizableKeys()
    {
        var keys = AppSettingsReader.Read("""
            {
              "Done": 1,
              "Typing
            """);

        Assert.Contains(keys, k => k.Path == "Done");
    }

    [Theory]
    [InlineData("appsettings.json", true, null)]
    [InlineData("appsettings.Development.json", true, "Development")]
    [InlineData("APPSETTINGS.STAGING.JSON", true, "STAGING")]
    [InlineData("secrets.json", true, null)]
    [InlineData("package.json", false, null)]
    [InlineData("settings.json", false, null)]
    public void ClassifiesConfigurationFileNames(string name, bool owned, string? environment)
    {
        Assert.Equal(owned, AppSettingsFile.IsConfigurationPath(name));

        if (owned)
            Assert.Equal(environment, AppSettingsFile.Environment(name));
    }

    [Fact]
    public void TheFileMapRoutesByNameShape()
    {
        var map = new LanguageFileMap([new AppSettingsLanguage()]);

        Assert.NotNull(map.Resolve(@"C:\app\appsettings.json"));
        Assert.NotNull(map.Resolve(@"C:\app\appsettings.Production.json"));
        Assert.NotNull(map.Resolve(@"C:\users\x\AppData\Roaming\Microsoft\UserSecrets\id\secrets.json"));
        Assert.Null(map.Resolve(@"C:\app\package.json"));
        Assert.Null(map.Resolve(@"C:\app\Program.cs"));
    }
}

/// <summary>
/// The joined half: the fixture project's C# against its settings files — the usage index, the
/// lens counts, completion from the bound options type, and references from a C# literal.
/// </summary>
/// <remarks>
/// In the shared-state collection because the fixture project loads through
/// <see cref="RoslynMCP.Services.WorkspaceService"/>, a process-wide cache.
/// </remarks>
[Collection(SharedState.Name)]
public class AppSettingsLanguageTests
{
    private static Task<AppSettingsView?> ViewAsync() =>
        AppSettingsWorkspace.GetAsync(FixturePaths.ConfigAppSettingsFile, default);

    private static Position PositionOf(string path, string needle, int offsetIntoNeedle = 0)
    {
        string text = File.ReadAllText(path);
        int index = text.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{needle}' is not in {Path.GetFileName(path)}");

        var line = SourceText.From(text).Lines.GetLinePosition(index + offsetIntoNeedle);
        return new Position(line.Line, line.Character);
    }

    // ---- The index -------------------------------------------------------------------------

    [Fact]
    public async Task TheIndexSeesEveryReadShape()
    {
        var view = await ViewAsync();
        Assert.NotNull(view);
        Assert.NotNull(view!.Project);

        var paths = view.Index.Usages.Select(u => u.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("App:Title", paths);                 // indexer on the root
        Assert.Contains("Example:Retries", paths);           // GetValue<T>
        Assert.Contains("Example", paths);                   // GetSection
        Assert.Contains("Example:Nested:Name", paths);       // chained sections + indexer
        Assert.Contains("ConnectionStrings:Main", paths);    // GetConnectionString
    }

    [Fact]
    public async Task EveryBindingSpellingTiesTheSectionToTheOptionsType()
    {
        var view = await ViewAsync();
        Assert.NotNull(view);

        var bindings = view!.Index.BindingsFor("Example").ToList();

        // Configure<T>(section), AddOptions<T>().BindConfiguration, section.Bind(instance),
        // section.Get<T>() — four spellings, one section, one type.
        Assert.True(bindings.Count >= 4, $"Expected all four binding shapes, saw {bindings.Count}.");
        Assert.All(bindings, b => Assert.Equal("ExampleOptions", b.Type.Name));
    }

    [Fact]
    public async Task AKeyUnderABoundSectionResolvesToItsProperty()
    {
        var view = await ViewAsync();
        Assert.NotNull(view);

        Assert.Equal("Retries", view!.Index.BoundProperty("Example:Retries")?.Name);
        Assert.Equal("Name", view.Index.BoundProperty("Example:Nested:Name")?.Name);
        Assert.Equal("NestedOptions", view.Index.BoundType("Example:Nested")?.Name);
        Assert.Null(view.Index.BoundProperty("Example:NoSuch"));
    }

    // ---- CodeLens --------------------------------------------------------------------------

    [Fact]
    public async Task ABoundKeyCountsItsLiteralAndItsPropertyReferences()
    {
        var pack = new AppSettingsLanguage();
        string uri = LspConverters.PathToUri(FixturePaths.ConfigAppSettingsFile);

        var lenses = await pack.CodeLensAsync(new CodeLensParams(new TextDocumentIdentifier(uri)), default);
        var lens = lenses.Single(l =>
            l.Data is { } d && LensKeyPath(d) == "Example:Retries");

        var resolved = await pack.ResolveCodeLensAsync(lens, default);

        // GetValue<int>("Example:Retries") plus options.Retries in Consumer.Read.
        Assert.Equal("2 references", resolved.Command!.Title);
    }

    [Fact]
    public async Task AKeyNothingReadsSaysZero()
    {
        var pack = new AppSettingsLanguage();
        string uri = LspConverters.PathToUri(FixturePaths.ConfigAppSettingsFile);

        var lenses = await pack.CodeLensAsync(new CodeLensParams(new TextDocumentIdentifier(uri)), default);
        var lens = lenses.Single(l => l.Data is { } d && LensKeyPath(d) == "Orphan:Dead");

        var resolved = await pack.ResolveCodeLensAsync(lens, default);

        Assert.Equal("0 references", resolved.Command!.Title);
    }

    [Fact]
    public async Task AKeyReadThroughTheProjectsOwnMethodStillCountsItsCallers()
    {
        // Wrapped:Timeout is named nowhere near IConfiguration: the only mention of it in the
        // solution is Config.GetSetting("Wrapped:Timeout").
        Assert.Equal("1 reference", await LensTitleAsync("Wrapped:Timeout"));

        // And Config.GetWrapped("Deep") reads inside the section its own body names.
        Assert.Equal("1 reference", await LensTitleAsync("Wrapped:Deep"));
    }

    private static async Task<string?> LensTitleAsync(string path)
    {
        var pack = new AppSettingsLanguage();
        string uri = LspConverters.PathToUri(FixturePaths.ConfigAppSettingsFile);

        var lenses = await pack.CodeLensAsync(new CodeLensParams(new TextDocumentIdentifier(uri)), default);
        var lens = lenses.Single(l => l.Data is { } d && LensKeyPath(d) == path);

        return (await pack.ResolveCodeLensAsync(lens, default)).Command?.Title;
    }

    private static string? LensKeyPath(CodeLensData data)
    {
        var document = AppSettingsDocumentCache.Get(FixturePaths.ConfigAppSettingsFile);
        if (document is null)
            return null;

        int offset = LspConverters.ToOffset(
            document.Text, new Position(data.Line, data.Character));

        return document.KeyAt(offset)?.Path;
    }

    // ---- Overrides -------------------------------------------------------------------------

    [Fact]
    public async Task ABaseValueAnOverlayReplacesSaysSoOnHover()
    {
        string markdown = await HoverAsync(FixturePaths.ConfigAppSettingsFile, "\"Retries\"");

        // Example:Retries is 3 here and 5 in the Development overlay — the line shows only the 3.
        Assert.Contains("Overridden in:", markdown);
        Assert.Contains("`appsettings.Development.json` → `5`", markdown);
        Assert.DoesNotContain("Overrides:", markdown);
    }

    [Fact]
    public async Task AnOverlayValueSaysWhatItReplaced()
    {
        string markdown = await HoverAsync(
            FixturePaths.ConfigAppDevelopmentSettingsFile, "\"Retries\"");

        Assert.Contains("Overrides:", markdown);
        Assert.Contains("`appsettings.json` → `3`", markdown);
        Assert.DoesNotContain("Overridden in:", markdown);
    }

    [Fact]
    public async Task AKeyOnlyOneFileDeclaresSaysNothingAboutOverrides()
    {
        string markdown = await HoverAsync(FixturePaths.ConfigAppSettingsFile, "\"Title\"");

        Assert.Contains("App:Title", markdown);
        Assert.DoesNotContain("Overridden in:", markdown);
        Assert.DoesNotContain("Overrides:", markdown);
    }

    [Fact]
    public async Task TheOverriddenKeyGetsALensPointingAtTheOverlay()
    {
        var pack = new AppSettingsLanguage();
        string uri = LspConverters.PathToUri(FixturePaths.ConfigAppSettingsFile);

        var lenses = await pack.CodeLensAsync(new CodeLensParams(new TextDocumentIdentifier(uri)), default);

        var lens = Assert.Single(lenses.Where(l =>
            l.Command?.Title.StartsWith("↓", StringComparison.Ordinal) == true));

        Assert.Equal("↓ overridden", lens.Command!.Title);

        // The lens sits on the base file's own key, and the peek lists the overlay's.
        var target = Assert.Single((LspLocation[])lens.Command.Arguments![3]);
        Assert.EndsWith("appsettings.Development.json", LspConverters.UriToPath(target.Uri));
    }

    private static async Task<string> HoverAsync(string filePath, string needle)
    {
        var hover = await new AppSettingsLanguage().HoverAsync(
            new TextDocumentPositionParams(
                new TextDocumentIdentifier(LspConverters.PathToUri(filePath)),
                PositionOf(filePath, needle, 2)),
            default);

        Assert.NotNull(hover);
        return hover!.Contents.Value;
    }

    // ---- Completion ------------------------------------------------------------------------

    [Fact]
    public async Task InsideABoundSectionTheOptionsPropertiesComplete()
    {
        var pack = new AppSettingsLanguage();

        var list = await pack.CompletionAsync(
            new CompletionParams(
                new TextDocumentIdentifier(LspConverters.PathToUri(FixturePaths.ConfigAppSettingsFile)),
                PositionOf(FixturePaths.ConfigAppSettingsFile, "\"Enabled\"", 3)),
            new LspResolveCache(), default);

        var labels = list.Items.Select(i => i.Label).ToList();

        // Mode is the one ExampleOptions property the file does not declare yet; the ones
        // already present would be duplicates and are withheld.
        Assert.Contains("Mode", labels);
        Assert.DoesNotContain("Retries", labels);
    }

    [Fact]
    public async Task InsideAnUnboundSectionTheKeysTheCodeReadsComplete()
    {
        var pack = new AppSettingsLanguage();

        var list = await pack.CompletionAsync(
            new CompletionParams(
                new TextDocumentIdentifier(LspConverters.PathToUri(FixturePaths.ConfigAppSettingsFile)),
                PositionOf(FixturePaths.ConfigAppSettingsFile, "\"Title\"", 3)),
            new LspResolveCache(), default);

        var items = list.Items.ToDictionary(i => i.Label, i => i.Detail);

        // App is not bound to a type, so the answer comes from the reads: config["App:Region"]
        // names a key this file never declares.
        Assert.Contains("Region", items.Keys);
        Assert.Equal("read by this solution", items["Region"]);

        // Already declared, and one level up rather than inside App.
        Assert.DoesNotContain("Title", items.Keys);
        Assert.DoesNotContain("Example", items.Keys);
    }

    [Fact]
    public async Task ABoundBooleanCompletesItsValues()
    {
        var pack = new AppSettingsLanguage();

        var list = await pack.CompletionAsync(
            new CompletionParams(
                new TextDocumentIdentifier(LspConverters.PathToUri(FixturePaths.ConfigAppSettingsFile)),
                PositionOf(FixturePaths.ConfigAppSettingsFile, "true,", 2)),
            new LspResolveCache(), default);

        var labels = list.Items.Select(i => i.Label).ToList();

        Assert.Contains("true", labels);
        Assert.Contains("false", labels);
    }

    // ---- F12 from the C# side ----------------------------------------------------------------

    [Fact]
    public async Task F12OnAConfigurationLiteralLandsOnTheKeyInEverySettingsFileDeclaringIt()
    {
        var (pack, context) = await EmbeddedAsync("\"Example:Retries\"");

        var locations = await pack.DefinitionAsync(context, typeDefinition: false, default);

        // The base file and the Development overlay both declare it, and which one wins depends
        // on an environment the editor cannot know.
        Assert.Equal(2, locations.Length);
        Assert.Contains(locations, l => l.Uri.EndsWith("appsettings.json", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(locations, l =>
            l.Uri.EndsWith("appsettings.Development.json", StringComparison.OrdinalIgnoreCase));

        // On the key itself, not on the head of the file.
        var document = AppSettingsDocumentCache.Get(FixturePaths.ConfigAppSettingsFile);
        var key = document!.Find("Example:Retries");
        Assert.Equal(
            LspConverters.ToRange(document.Text.Lines, key!.NameSpan),
            locations.Single(l =>
                l.Uri.EndsWith("appsettings.json", StringComparison.OrdinalIgnoreCase)).Range);
    }

    [Fact]
    public async Task F12FollowsAChainedSectionIntoItsFullPath()
    {
        // config.GetSection("Example").GetSection("Nested")["Name"] — the caret is on "Name",
        // and the key it declares is Example:Nested:Name.
        var (pack, context) = await EmbeddedAsync("\"Name\"");

        var locations = await pack.DefinitionAsync(context, typeDefinition: false, default);

        var document = AppSettingsDocumentCache.Get(FixturePaths.ConfigAppSettingsFile);
        Assert.Equal(
            LspConverters.ToRange(document!.Text.Lines, document.Find("Example:Nested:Name")!.NameSpan),
            Assert.Single(locations).Range);
    }

    [Fact]
    public async Task ALiteralThatIsNotAConfigurationPathIsNotClaimed()
    {
        var document = await CSharpDocumentAsync();
        var model = await document.GetSemanticModelAsync(default);
        var root = await document.GetSyntaxRootAsync(default);

        // The type argument list of Configure<ExampleOptions> holds no literal; the nearest
        // string that is not a path at all is the one inside the JSON-free parts of the file.
        var token = root!.FindToken(
            (await document.GetTextAsync(default)).ToString().IndexOf("ExampleOptions", StringComparison.Ordinal));

        Assert.Null(await new AppSettingsLanguage().DetectAsync(document, token, model!, default));
    }

    /// <summary>The pack and a context over the literal, the way the embedded detector builds
    /// one.</summary>
    private static async Task<(AppSettingsLanguage Pack, EmbeddedStringContext Context)> EmbeddedAsync(
        string literal)
    {
        var document = await CSharpDocumentAsync();
        var text = await document.GetTextAsync(default);
        var model = await document.GetSemanticModelAsync(default);
        var root = await document.GetSyntaxRootAsync(default);

        int index = text.ToString().IndexOf(literal, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{literal}' is not in {document.Name}");

        var token = root!.FindToken(index + 1);
        var pack = new AppSettingsLanguage();

        // Detected the same way the caret path detects it, so the test covers the claim as well
        // as the answer.
        Assert.Equal("ConfigurationPath", await pack.DetectAsync(document, token, model!, default));

        return (pack, new EmbeddedStringContext(
            pack, "ConfigurationPath", [], document, model!, token, token.SpanStart + 1));
    }

    private static async Task<Document> CSharpDocumentAsync()
    {
        var view = await ViewAsync();
        Assert.NotNull(view?.Project);

        return view!.Project!.Documents.Single(d =>
            string.Equals(d.FilePath, FixturePaths.ConfigAppProgramFile, StringComparison.OrdinalIgnoreCase));
    }

    // ---- References from the C# side -------------------------------------------------------

    [Fact]
    public async Task ACaretInAKeyLiteralAnswersWithTheJsonDeclarations()
    {
        var view = await ViewAsync();
        Assert.NotNull(view?.Project);

        string program = FixturePaths.ConfigAppProgramFile;
        string text = await File.ReadAllTextAsync(program);
        int offset = text.IndexOf("Example:Retries", StringComparison.Ordinal) + 3;

        var pack = new AppSettingsLanguage();
        var locations = await pack.ReferencesAsync(program, offset, view!.Project, default);

        Assert.NotNull(locations);
        var files = locations!.Select(l => Path.GetFileName(LspConverters.UriToPath(l.Uri)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The key is declared in the base file and overridden for Development; both answer.
        Assert.Contains("appsettings.json", files);
        Assert.Contains("appsettings.Development.json", files);
    }

    [Fact]
    public async Task ACaretInAnOrdinaryLiteralDeclines()
    {
        var view = await ViewAsync();
        Assert.NotNull(view?.Project);

        string program = FixturePaths.ConfigAppProgramFile;
        string text = await File.ReadAllTextAsync(program);
        int offset = text.IndexOf("ConfigApp", StringComparison.Ordinal);

        var pack = new AppSettingsLanguage();

        Assert.Null(await pack.ReferencesAsync(program, offset, view!.Project, default));
    }
}
