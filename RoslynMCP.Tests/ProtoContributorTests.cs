using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages;
using RoslynMCP.Languages.Proto;
using RoslynMCP.Languages.Proto.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Tests;

/// <summary>
/// The half of the pack that runs inside a C# request: F12 and Shift+F12 on generated code also
/// reaching the <c>.proto</c> line protoc built it from.
/// </summary>
/// <remarks>
/// <para>
/// Driven through <see cref="NavigationHandlers"/> rather than by calling the pack directly,
/// because a contribution only means anything folded into Roslyn's own answer, and because the two
/// gates in front of it — the symbol kind, and whether the project resolves the protobuf runtime
/// at all — only run on this path. Every registered pack is asked about every C# caret in the
/// solution, so those gates are the difference between a pack that costs nothing and one that
/// walks a directory tree per keystroke.
/// </para>
/// <para>
/// Calling the handler directly means no host has built a registry, so each test that needs the
/// contribution publishes one, the way the WebForms tests do.
/// </para>
/// </remarks>
[Collection(SharedState.Name)]
public class ProtoContributorTests
{
    private static TextDocumentIdentifier Doc(string path) =>
        new(LspConverters.PathToUri(path));

    /// <summary>The position of <paramref name="needle"/> in the file, as an LSP position.</summary>
    private static Position PositionOf(string path, string needle, int offsetIntoNeedle = 0)
    {
        string text = File.ReadAllText(path);
        int index = text.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{needle}' is not in {Path.GetFileName(path)}");

        var source = SourceText.From(text);
        var line = source.Lines.GetLinePosition(index + offsetIntoNeedle);
        return new Position(line.Line, line.Character);
    }

    private static void PublishProtoPack() =>
        new LanguageRegistry([new ProtoLanguage(new MarkdownFormatter())]).Publish();

    private static bool IsFile(LspLocation location, string path) =>
        string.Equals(
            Path.GetFullPath(LspConverters.UriToPath(location.Uri)),
            Path.GetFullPath(path),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>The source the location's range actually covers. Asserting on this rather than on
    /// the line number is what proves the contribution points at the declaration's name and not
    /// merely at the right file.</summary>
    private static string TextAt(LspLocation location)
    {
        var text = SourceText.From(File.ReadAllText(LspConverters.UriToPath(location.Uri)));
        return text.ToString(LspConverters.ToTextSpan(text, location.Range));
    }

    /// <summary>The one <c>.proto</c> location among the results. Single, because a C# symbol is
    /// generated from exactly one declaration — two would mean the binder matched twice.</summary>
    private static LspLocation ProtoLocation(IEnumerable<LspLocation> locations) =>
        Assert.Single(locations, location =>
            LspConverters.UriToPath(location.Uri).EndsWith(".proto", StringComparison.OrdinalIgnoreCase));

    private static Task<LspLocation[]> DefinitionAsync(
        string path, string needle, int offsetIntoNeedle, bool typeDefinition = false) =>
        NavigationHandlers.DefinitionAsync(
            new TextDocumentPositionParams(Doc(path), PositionOf(path, needle, offsetIntoNeedle)),
            typeDefinition,
            default);

    private static Task<LspLocation[]> ReferencesAsync(string path, string needle, int offsetIntoNeedle) =>
        NavigationHandlers.ReferencesAsync(
            new ReferenceParams(
                Doc(path),
                PositionOf(path, needle, offsetIntoNeedle),
                new ReferenceContext(IncludeDeclaration: true)),
            default);

    // ---- Go to definition ---------------------------------------------------------------------

    [Fact]
    public async Task DefinitionOnAGeneratedServiceBaseAlsoOffersTheServiceThatProducedIt()
    {
        PublishProtoPack();

        var locations = await DefinitionAsync(
            FixturePaths.WidgetGrpcServiceFile, "WidgetService.WidgetServiceBase", 16);

        // Roslyn's own answer goes: the generated class states the same declaration the .proto
        // does, in a file the next build rewrites, so offering it beside the real one makes F12 a
        // pick-one-of-two whose second entry nobody wants.
        Assert.DoesNotContain(locations, location => IsFile(location, FixturePaths.WidgetsGrpcGeneratedFile));

        var declaration = ProtoLocation(locations);
        Assert.True(IsFile(declaration, FixturePaths.WidgetsProtoFile));
        Assert.Equal("WidgetService", TextAt(declaration));
    }

    [Fact]
    public async Task DefinitionOnAGeneratedMessageClassAlsoOffersItsProtoMessage()
    {
        PublishProtoPack();

        // The parameter's type, rather than the type named in a `new` expression: a caret inside
        // `new GetWidgetsByIdRequest()` resolves to the constructor protoc declares, which is a
        // different symbol from the class and stands for no proto declaration of its own.
        var locations = await DefinitionAsync(
            FixturePaths.WidgetGrpcServiceFile, "GetWidgetsByIdRequest request", 4);

        Assert.DoesNotContain(locations, location => IsFile(location, FixturePaths.WidgetsGeneratedFile));

        var declaration = ProtoLocation(locations);
        Assert.True(IsFile(declaration, FixturePaths.WidgetsProtoFile));
        Assert.Equal("GetWidgetsByIdRequest", TextAt(declaration));
    }

    [Fact]
    public async Task DefinitionOnAGeneratedPropertyAlsoOffersTheFieldItWasBuiltFrom()
    {
        PublishProtoPack();

        // The field is declared in widgets/types.proto, which is not the .proto the service is in:
        // the binder has to name the declaring file rather than whichever one it looked at first.
        var locations = await DefinitionAsync(FixturePaths.WidgetClientCallerFile, "widget.Label", 9);

        var declaration = ProtoLocation(locations);
        Assert.True(IsFile(declaration, FixturePaths.WidgetTypesProtoFile));
        Assert.Equal("label", TextAt(declaration));
    }

    [Fact]
    public async Task DefinitionOnAGeneratedClientCallAlsoOffersTheRpcItInvokes()
    {
        PublishProtoPack();

        // The client's `…Async` overload, which is a different symbol from the base's virtual
        // method the server overrides. Both stand for the one rpc, so both have to reach it.
        var locations = await DefinitionAsync(
            FixturePaths.WidgetClientCallerFile, "_client.GetWidgetsByIdAsync(request)", 10);

        var declaration = ProtoLocation(locations);
        Assert.True(IsFile(declaration, FixturePaths.WidgetsProtoFile));
        Assert.Equal("GetWidgetsById", TextAt(declaration));
    }

    [Fact]
    public async Task DefinitionOnAnOverrideInAHandWrittenServiceOffersTheRpcItImplements()
    {
        PublishProtoPack();

        // The symbol here is the override, not the generated virtual it hides, so the exact
        // symbol-to-declaration map cannot see it. To the person holding the caret this is the rpc.
        var locations = await DefinitionAsync(
            FixturePaths.WidgetGrpcServiceFile, "GetWidgetsById(GetWidgetsByIdRequest request", 2);

        var declaration = ProtoLocation(locations);
        Assert.True(IsFile(declaration, FixturePaths.WidgetsProtoFile));
        Assert.Equal("GetWidgetsById", TextAt(declaration));
    }

    [Fact]
    public async Task TypeDefinitionOnALocalReachesTheProtoWhilePlainDefinitionStaysOnTheLocal()
    {
        PublishProtoPack();

        // One caret, two gestures. F12 is about the local, whose kind the pack declines outright
        // before touching a compilation — most carets in a solution are on something like this.
        var definition = await DefinitionAsync(
            FixturePaths.WidgetClientCallerFile, "var widget in reply", 6);
        Assert.DoesNotContain(definition, location =>
            LspConverters.UriToPath(location.Uri).EndsWith(".proto", StringComparison.OrdinalIgnoreCase));

        // Ctrl+F12 is about the type the local holds, which is a generated message.
        var typeDefinition = await DefinitionAsync(
            FixturePaths.WidgetClientCallerFile, "var widget in reply", 6, typeDefinition: true);

        var declaration = ProtoLocation(typeDefinition);
        Assert.True(IsFile(declaration, FixturePaths.WidgetTypesProtoFile));
        Assert.Equal("Widget", TextAt(declaration));
    }

    // ---- Find references ----------------------------------------------------------------------

    [Fact]
    public async Task FindReferencesOnGeneratedCodeIncludesTheProtoDeclarationSpan()
    {
        PublishProtoPack();

        // The same four carets go-to-definition covers. Both directions have to agree, or the same
        // pair of files reports a relationship one way and not the other.
        (string File, string Needle, int Offset, string Proto, string Name)[] carets =
        [
            (FixturePaths.WidgetGrpcServiceFile, "WidgetService.WidgetServiceBase", 16,
                FixturePaths.WidgetsProtoFile, "WidgetService"),
            (FixturePaths.WidgetGrpcServiceFile, "GetWidgetsByIdRequest request", 4,
                FixturePaths.WidgetsProtoFile, "GetWidgetsByIdRequest"),
            (FixturePaths.WidgetClientCallerFile, "widget.Label", 9,
                FixturePaths.WidgetTypesProtoFile, "label"),
            (FixturePaths.WidgetGrpcServiceFile, "GetWidgetsById(GetWidgetsByIdRequest request", 2,
                FixturePaths.WidgetsProtoFile, "GetWidgetsById"),
        ];

        foreach (var (file, needle, offset, proto, name) in carets)
        {
            var locations = await ReferencesAsync(file, needle, offset);

            var declaration = ProtoLocation(locations);
            Assert.True(
                IsFile(declaration, proto),
                $"'{needle}' pointed at {LspConverters.UriToPath(declaration.Uri)} rather than {proto}");
            Assert.Equal(name, TextAt(declaration));

            // Roslyn's hand-written C# results are still there — the proto line is added to them,
            // never substituted for them. Only what protoc wrote is withdrawn, which is why this
            // asks for a .cs the pack does not call its own output.
            Assert.Contains(locations, location =>
                LspConverters.UriToPath(location.Uri) is var path
                && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                && !ProtoGeneratedIndex.IsKnownGenerated(path));
        }
    }

    // ---- The cost of having the pack registered -----------------------------------------------

    [Fact]
    public async Task AProjectWithNoProtobufInItIsDeclinedWithoutAnIndexEverBeingBuilt()
    {
        var pack = new ProtoLanguage(new MarkdownFormatter());

        var project = await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);
        var calculator = await RoslynTestHelpers.GetNamedTypeAsync(
            FixturePaths.SampleProjectFile, "SampleProject.Calculator");

        Assert.Empty(await ((ILanguageDefinitionContributor)pack).DefinitionsAsync(calculator, project, default));
        Assert.Empty(await ((ILanguageReferenceContributor)pack).ReferencesAsync(calculator, project, default));

        // Empty is the easy half. The point of ILanguagePack.WellKnownTypeNames is that the pack
        // gets to that answer from one metadata lookup on a compilation the request already forced:
        // no protobuf runtime means no generated code, so nothing enumerates a directory.
        Assert.False(await ProtoReferenceService.HostsProtobufAsync(project, default));

        // And the proof that nothing was read: the shared empty instance is what a project with no
        // .proto under it gets back, and it is handed out before any document is touched.
        Assert.Same(ProtoGeneratedIndex.Empty, await ProtoGeneratedIndex.GetAsync(project, default));
    }

    // ---- Rename ------------------------------------------------------------------------------

    [Fact]
    public async Task RenamingAGeneratedPropertyRewritesTheCSharpAndNoProtoFile()
    {
        PublishProtoPack();

        var edit = await RenameHandler.RenameAsync(
            new RenameParams(
                Doc(FixturePaths.WidgetClientCallerFile),
                PositionOf(FixturePaths.WidgetClientCallerFile, "widget.Label", 9),
                "Caption"),
            default);

        Assert.NotNull(edit);

        var touched = edit!.Changes.Keys.Select(LspConverters.UriToPath).ToList();

        // The C# half really did move, so the absence below is a refusal and not an empty rename.
        Assert.Contains(touched, path =>
            Path.GetFileName(path).Equals("WidgetClientCaller.cs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(touched, path =>
            string.Equals(Path.GetFullPath(path), Path.GetFullPath(FixturePaths.WidgetTypesGeneratedFile),
                StringComparison.OrdinalIgnoreCase));

        // A field name is the wire contract — it is what JSON mapping serialises and what every
        // other language generated from this .proto is compiled against — so following a C# rename
        // into it would change what the service answers to with nothing in the C# diff to show it.
        // Rewriting only the generated C# is the other half of the trap: the next build puts it
        // back. F2 therefore behaves exactly as it does with the pack switched off.
        Assert.DoesNotContain(touched, path =>
            path.EndsWith(".proto", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ThePackRegistersNothingThatCouldRewriteADeclaration()
    {
        var registry = new LanguageRegistry([new ProtoLanguage(new MarkdownFormatter())]);

        // Nothing to route a rename through on either front-end. IRenameHandler in particular has
        // no CanHandle — every registered handler runs on every rename in the solution — so
        // implementing it would put this pack in the path of every F2 to do nothing.
        Assert.Empty(registry.RenameHandlers);
        Assert.Empty(registry.Contributors<ILanguageRenameContributor>());
        Assert.Empty(registry.Contributors<ISymbolFreeRenameProvider>());

        // The four read-only surfaces are all present, so the above is a decision rather than a
        // pack that failed to register.
        Assert.Single(registry.GoToDefinitionHandlers);
        Assert.Single(registry.FindUsagesHandlers);
        Assert.Single(registry.OutlineHandlers);
        Assert.Single(registry.DiagnosticsHandlers);
    }
}
