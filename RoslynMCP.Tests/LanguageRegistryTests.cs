using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynMCP.Config;
using RoslynMCP.Languages;
using RoslynMCP.Languages.Mediator;
using RoslynMCP.Languages.MsBuild;
using RoslynMCP.Languages.Proto;
using RoslynMCP.Languages.Razor;
using RoslynMCP.Languages.Resources;
using RoslynMCP.Languages.WebForms;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The registration gate: which packs a process has at all, and which document each one owns.
/// </summary>
/// <remarks>
/// There are two gates and they answer different questions, so a test about "a pack is off" has to
/// pick the right one. Registration — this file — is settled once per process by
/// <c>roslynsense.json</c> and the <c>--no-*</c> flags, before anything starts, and it governs the
/// MCP tool surface as well as the editor's; a pack it excludes was never constructed. Activation
/// is per LSP connection (<c>roslynSense.languages.*</c> from one editor's initialization options),
/// lives on <see cref="LanguageSession"/>, and deliberately leaves the registry alone — see
/// <see cref="LanguageSessionTests.EditorSettingsDoNotReachTheMcpToolSurface"/>, which asserts
/// exactly that, because one window's preference must not strip tools from an AI session attached
/// to the same daemon.
/// </remarks>
public class LanguageRegistryTests
{
    [Theory]
    [InlineData(".aspx")]
    [InlineData(".ascx")]
    [InlineData(".master")]
    [InlineData(".asax")]
    [InlineData(".ashx")]
    [InlineData(".asmx")]
    public void EveryMarkupExtensionThePackDeclaresResolvesToIt(string extension) =>
        Assert.IsType<WebFormsLanguage>(Registry().Resolve($@"C:\site\Page{extension}"));

    [Theory]
    [InlineData(@"C:\site\Default.aspx.cs")]   // the code-behind is C#, and Roslyn owns it
    [InlineData(@"C:\site\Web.config")]
    [InlineData(@"C:\site\readme")]            // no extension at all
    [InlineData("")]
    [InlineData(null)]
    public void ADocumentNoPackDeclaredIsLeftToTheCSharpHandlers(string? path) =>
        // Null from Resolve is how the caller spells "Roslyn answers this", so an unknown
        // extension and a missing path have to reach the same answer as a .cs file.
        Assert.Null(Registry().Resolve(path));

    [Fact]
    public void AMarkupDocumentResolvesTheSameWhetherItArrivesAsAPathOrAFileUri()
    {
        var registry = Registry();

        // The two front-ends speak different dialects — the LSP handlers hold URIs and the MCP
        // tools hold system paths — and both hand what they have straight to Resolve. A pack that
        // matched only one form would serve the editor and not the AI session, or the reverse.
        const string path = @"C:\my site\Controls\Menu.ascx";
        string uri = LspConverters.PathToUri(path);

        Assert.StartsWith("file:///", uri);
        Assert.IsType<WebFormsLanguage>(registry.Resolve(path));
        Assert.IsType<WebFormsLanguage>(registry.Resolve(uri));

        // The space in the directory is escaped on the way through, which is the part of a URI
        // that would defeat matching on anything wider than the extension.
        Assert.Contains("%20", uri);
    }

    [Fact]
    public void AnUpperCaseExtensionIsStillTheSamePack() =>
        // Windows file systems are case-insensitive, so a project can name a file .ASPX and the
        // editor will send it back exactly as written.
        Assert.IsType<WebFormsLanguage>(Registry().Resolve(@"C:\site\LEGACY.ASPX"));

    [Fact]
    public void ADocumentOnAVirtualSchemeBelongsToNoPackEvenWhenItsPathLooksLikeMarkup()
    {
        var registry = Registry();

        // Generated output and decompiled metadata have no file behind them and their content is
        // C#, so the scheme has to settle ownership before the extension is ever looked at.
        Assert.Null(registry.Resolve($"{VirtualDocumentHandler.GeneratedScheme}:/Default.aspx"));
        Assert.Null(registry.Resolve($"{VirtualDocumentHandler.MetadataScheme}:/Menu.ascx"));

        // The same names as URIs a client really receives, owner and all.
        Assert.Null(registry.Resolve(VirtualDocumentHandler.UriFor(
            VirtualDocumentHandler.GeneratedScheme, @"C:\site\Site.csproj", "Default.aspx")));
        Assert.Null(registry.Resolve(VirtualDocumentHandler.UriFor(
            VirtualDocumentHandler.MetadataScheme, @"C:\site\bin\Site.dll", "Menu.ascx")));

        // And the control: the identical tail on a file URI is the pack's, so it is the scheme
        // doing the excluding rather than these names being unowned.
        Assert.IsType<WebFormsLanguage>(registry.Resolve("file:///C:/site/Default.aspx"));
    }

    [Fact]
    public void APackClaimsItsOwnProjectionsWithoutClaimingThemAsMarkup()
    {
        var registry = Registry();

        // "Do you own this file type" and "did you invent this file" are separate questions. The
        // projection of a markup file is a .cs document Roslyn compiles, so answering the second
        // by extension would route requests about it back into the pack that generated it.
        const string markup = @"C:\site\Default.aspx";
        const string projection = markup + ".aspx-inline.g.cs";

        Assert.True(registry.IsProjectionPath(projection));
        Assert.Null(registry.Resolve(projection));

        Assert.IsType<WebFormsLanguage>(registry.Resolve(markup));
        Assert.False(registry.IsProjectionPath(markup));

        // The path is null wherever a document has no file, which is most of the callers.
        Assert.False(registry.IsProjectionPath(null));
        Assert.False(registry.IsProjectionPath(@"C:\site\Default.aspx.designer.cs"));
    }

    [Fact]
    public void TheToolHandlersAreServedFromTheRegisteredPacksInRegistrationOrder()
    {
        var registry = Registry();

        // The MCP tools take IEnumerable<I*Handler> and know nothing about packs, so these lists
        // are the whole of what an AI session can reach — and their order is the order the packs
        // were registered in, which is what decides who gets asked first.
        Assert.Collection(
            registry.Packs,
            pack => Assert.IsType<WebFormsLanguage>(pack),
            pack => Assert.IsType<RazorLanguage>(pack),
            pack => Assert.IsType<ProtoLanguage>(pack),
            pack => Assert.IsType<MediatorLanguage>(pack),
            pack => Assert.IsType<ResourcesLanguage>(pack),
            pack => Assert.IsType<MsBuildLanguage>(pack));

        foreach (var handlers in new IEnumerable<object>[]
        {
            registry.GoToDefinitionHandlers,
            registry.OutlineHandlers,
            registry.DiagnosticsHandlers,
        })
        {
            Assert.Collection(
                handlers,
                handler => Assert.IsType<WebFormsLanguage>(handler),
                handler => Assert.IsType<RazorLanguage>(handler),
                handler => Assert.IsType<ProtoLanguage>(handler));
        }

        // A pack contributes to a list only by implementing its interface, so the lists are not
        // interchangeable. Razor has no find-usages of its own, so its files fall through to
        // Roslyn for that one tool; the resources pack — registered, but serving the editor
        // rather than the tools — is in none of them.
        Assert.Collection(
            registry.FindUsagesHandlers,
            handler => Assert.IsType<WebFormsLanguage>(handler),
            handler => Assert.IsType<ProtoLanguage>(handler));

        // Rename is the one the proto pack deliberately stays out of. IRenameHandler has no
        // CanHandle, so every registered handler runs on every C# rename — and a proto name is
        // the wire contract, which no rename may rewrite on the user's behalf. Its absence here
        // is the decision, not an oversight.
        Assert.Collection(
            registry.RenameHandlers,
            handler => Assert.IsType<WebFormsLanguage>(handler),
            handler => Assert.IsType<RazorLanguage>(handler));
    }

    [Fact]
    public void TheMediatorPackIsRegisteredWithoutJoiningAnyToolHandlerList()
    {
        var registry = Registry();

        Assert.Contains(registry.Packs, pack => pack is MediatorLanguage);

        // It owns no files, so it can answer none of the path-routed MCP handlers — and claiming
        // one would hijack every C# request in the process, because that dispatch is
        // first-match-wins with no fall-through. It reaches the tools by an explicit call instead.
        foreach (var handlers in new IEnumerable<object>[]
        {
            registry.GoToDefinitionHandlers,
            registry.FindUsagesHandlers,
            registry.OutlineHandlers,
            registry.RenameHandlers,
            registry.DiagnosticsHandlers,
        })
        {
            Assert.DoesNotContain(handlers, handler => handler is MediatorLanguage);
        }
    }

    [Fact]
    public void MediatorOffLeavesTheOtherPacksAlone()
    {
        var registry = Registry("--no-mediator");

        Assert.DoesNotContain(registry.Packs, pack => pack is MediatorLanguage);
        Assert.Collection(
            registry.Packs,
            pack => Assert.IsType<WebFormsLanguage>(pack),
            pack => Assert.IsType<RazorLanguage>(pack),
            pack => Assert.IsType<ProtoLanguage>(pack),
            pack => Assert.IsType<ResourcesLanguage>(pack),
            pack => Assert.IsType<MsBuildLanguage>(pack));
    }

    [Fact]
    public void TheTypedResolveDeclinesAPackThatCannotAnswerThatRequest()
    {
        var registry = Registry();

        // Resolve<T> is how a call site asks "whose file is this, and can it do T?" in one step.
        // Owning the extension is not the same as answering the request.
        Assert.IsType<RazorLanguage>(registry.Resolve(@"C:\app\Counter.razor"));
        Assert.NotNull(registry.Resolve<IGoToDefinitionHandler>(@"C:\app\Counter.razor"));
        Assert.Null(registry.Resolve<IFindUsagesHandler>(@"C:\app\Counter.razor"));

        Assert.NotNull(registry.Resolve<IFindUsagesHandler>(@"C:\site\Default.aspx"));
        Assert.Null(registry.Resolve<IFindUsagesHandler>(@"C:\site\Default.aspx.cs"));
    }

    [Fact]
    public void WebFormsOffLeavesNothingBehindForMarkup()
    {
        // Driven through the real gate rather than by hand-picking packs, because --no-webforms is
        // the switch a user actually has. It removes the pack from the process, which is a
        // different thing from an editor switching it off for one window.
        var registry = Registry("--no-webforms");

        Assert.Null(registry.Resolve(@"C:\site\Default.aspx"));
        Assert.False(registry.IsProjectionPath(@"C:\site\Default.aspx.aspx-inline.g.cs"));
        // Absence of WebForms rather than emptiness: another pack answering the same tool is not
        // this flag's business, and asserting the list is bare would fail the next time one does.
        Assert.DoesNotContain(registry.FindUsagesHandlers, handler => handler is WebFormsLanguage);
        Assert.DoesNotContain(registry.GoToDefinitionHandlers, handler => handler is WebFormsLanguage);

        // The other packs are untouched: one flag is one language.
        Assert.Collection(
            registry.Packs,
            pack => Assert.IsType<RazorLanguage>(pack),
            pack => Assert.IsType<ProtoLanguage>(pack),
            pack => Assert.IsType<MediatorLanguage>(pack),
            pack => Assert.IsType<ResourcesLanguage>(pack),
            pack => Assert.IsType<MsBuildLanguage>(pack));
        Assert.IsType<RazorLanguage>(registry.Resolve(@"C:\app\Counter.razor"));
    }

    [Fact]
    public void ARegistryWithNoPacksServesNoHandlersAtAll()
    {
        // Pure C#, which is what every --no-* flag together produces and what the tools see in a
        // host that registered nothing. Empty rather than null, so no tool has to special-case it.
        var registry = LanguageRegistry.Empty;

        Assert.Empty(registry.Packs);
        Assert.Empty(registry.GoToDefinitionHandlers);
        Assert.Empty(registry.FindUsagesHandlers);
        Assert.Empty(registry.OutlineHandlers);
        Assert.Empty(registry.RenameHandlers);
        Assert.Empty(registry.DiagnosticsHandlers);

        Assert.Null(registry.Resolve(@"C:\site\Default.aspx"));
        Assert.False(registry.IsProjectionPath(@"C:\site\Default.aspx.aspx-inline.g.cs"));
    }

    /// <summary>
    /// A whole file name beats an extension, which is the point of declaring one.
    /// </summary>
    /// <remarks>
    /// The case that forced this: <c>packages.config</c> and <c>nuget.config</c> belong to whoever
    /// reads NuGet state, while <c>web.config</c> and <c>app.config</c> beside them belong to
    /// <see cref="BindingRedirectHandler"/> — and a pack that claimed <c>.config</c> to reach the
    /// first two would take all four.
    /// </remarks>
    [Theory]
    [InlineData(@"C:\src\packages.config")]
    [InlineData(@"C:\src\PACKAGES.CONFIG")]     // matched case-insensitively, like extensions
    [InlineData(@"C:\src\nuget.config")]
    [InlineData(@"C:\src\NuGet.Config")]        // the casing NuGet itself writes
    public void ADeclaredFileNameResolvesToThePackThatDeclaredIt(string path) =>
        Assert.IsType<NamedFilePack>(new LanguageRegistry([new NamedFilePack()]).Resolve(path));

    [Theory]
    [InlineData(@"C:\src\web.config")]
    [InlineData(@"C:\src\Web.config")]
    [InlineData(@"C:\src\app.config")]
    public void ConfigFilesThePackDidNotNameAreLeftAlone(string path)
    {
        // Not merely unclaimed by this pack — unclaimed full stop, because the binding-redirect
        // handler sits in front of pack dispatch and answers these itself. A pack that swallowed
        // them would take the diagnostics and quick fixes with it.
        Assert.Null(new LanguageRegistry([new NamedFilePack()]).Resolve(path));
        Assert.Null(Registry().Resolve(path));
    }

    [Fact]
    public void AFileNameResolvesTheSameThroughASessionAsThroughTheRegistry()
    {
        // The two carry separate maps over different pack sets — every registered pack, and the
        // ones one editor connection switched on — so a routing rule added to one and not the other
        // would serve the AI session and not the editor, or the reverse.
        const string path = @"C:\src\packages.config";

        Assert.IsType<NamedFilePack>(new LanguageRegistry([new NamedFilePack()]).Resolve(path));
        Assert.IsType<NamedFilePack>(new LanguageSession([new NamedFilePack()]).Resolve(path));

        // And a pack the connection switched off answers nothing, by either route.
        Assert.Null(new LanguageSession([new NamedFilePack()], _ => false).Resolve(path));
    }

    /// <summary>A pack that owns files by name as well as by extension, which no real pack did
    /// until project files needed both.</summary>
    private sealed class NamedFilePack : ILanguagePack
    {
        public string Id => "namedfile";

        public string DisplayName => "Named File Test Pack";

        public ImmutableArray<string> FileExtensions { get; } = [".namedtest"];

        public ImmutableArray<string> FileNames { get; } = ["packages.config", "nuget.config"];

        public LanguageCapabilities Capabilities => LanguageCapabilities.None;

        public ImmutableArray<string> WellKnownTypeNames { get; } = [];

        public ImmutableArray<SymbolKind> InterestingSymbolKinds { get; } = [];

        public bool IsProjectionPath(string? filePath) => false;
    }

    /// <summary>
    /// The packs these command-line arguments leave registered, through the same gate the three
    /// hosts go through.
    /// </summary>
    private static LanguageRegistry Registry(params string[] args) =>
        new(LanguagePackRegistration.Create(
            EffectiveSettings.Resolve(args, null, out _), new MarkdownFormatter()));
}
