using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynMCP.Config;
using RoslynMCP.Languages;
using RoslynMCP.Languages.Proto;
using RoslynMCP.Languages.WebForms;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The initialize response is the whole of what a client reads before deciding which requests to
/// send, and toggling a language is reload-window precisely because a capability cannot be
/// withdrawn afterwards. So a markup capability that survives WebForms being switched off is a
/// menu entry and a set of keystroke triggers the server has no handler for — asserted in both
/// directions rather than only the on one.
/// </summary>
public class LanguageCapabilityTests
{
    /// <summary>What opens a tag, a prefix and an attribute value. Meaningless in C#.</summary>
    private static readonly string[] MarkupTriggers = ["<", ":", "=", "\"", "'"];

    /// <summary>
    /// The subset of those no other registered pack also asks for, so their absence really is
    /// WebForms' absence. A quote opens an attribute value and an <c>import</c> path both, and the
    /// union hands the server one list rather than one per language.
    /// </summary>
    private static readonly string[] MarkupOnlyTriggers = ["<", ":", "=", "'"];

    /// <summary>
    /// The proto triggers C# does not already ask for: the space that ends a field's label and then
    /// its type, and the quote and slash an <c>import</c> path is made of. The dot the pack also
    /// declares is C#'s own, and the union deduplicates it rather than listing it twice.
    /// </summary>
    private static readonly string[] ProtoTriggers = [" ", "\"", "/"];

    [Fact]
    public void WebFormsOnAdvertisesMarkupTriggersCommandAndFilters()
    {
        var capabilities = Capabilities();

        var completion = capabilities.CompletionProvider;
        Assert.NotNull(completion);
        foreach (string trigger in MarkupTriggers)
            Assert.Contains(trigger, completion!.TriggerCharacters);

        // The union widens C#'s set; it does not replace it.
        Assert.Contains(".", completion!.TriggerCharacters);
        Assert.Contains("[", completion.TriggerCharacters);

        Assert.NotNull(capabilities.ExecuteCommandProvider);
        Assert.Contains(
            ExecuteCommandHandler.GenerateEventHandlerCommand,
            capabilities.ExecuteCommandProvider!.Commands);

        var globs = FileOperationGlobs(capabilities);
        Assert.Contains("**/*.cs", globs);
        Assert.Contains("**/*.aspx", globs);
        Assert.Contains("**/*.ascx", globs);
        Assert.Contains("**/*.master", globs);
    }

    [Fact]
    public void WebFormsOffAdvertisesNoneOfThem()
    {
        // Driven through the real gate rather than an empty registry, because --no-webforms is
        // the switch that was doing nothing on the CLI path.
        var capabilities = Capabilities("--no-webforms");

        var completion = capabilities.CompletionProvider;
        Assert.NotNull(completion);
        foreach (string trigger in MarkupOnlyTriggers)
            Assert.DoesNotContain(trigger, completion!.TriggerCharacters);

        Assert.Contains(".", completion!.TriggerCharacters);
        Assert.Contains("[", completion.TriggerCharacters);

        Assert.NotNull(capabilities.ExecuteCommandProvider);
        Assert.DoesNotContain(
            ExecuteCommandHandler.GenerateEventHandlerCommand,
            capabilities.ExecuteCommandProvider!.Commands);

        // The commands that belong to no language are unaffected.
        Assert.Contains(ExecuteCommandHandler.BuildCommand, capabilities.ExecuteCommandProvider.Commands);

        // Exactly what is left: C#, and the resources pack, which one flag away from WebForms is
        // still on. A markup glob surviving here is the failure this asserts against.
        var globs = FileOperationGlobs(capabilities);
        Assert.Equal(new[] { "**/*.cs", "**/*.resx" }, globs);
    }

    [Fact]
    public void SemanticTokenLegendIsTheSessionsUnion()
    {
        // C# keeps the low indices whatever else is on, so a pack's offsets start past its end.
        string[] csharp = SemanticTokensHandler.TokenTypes;
        string[] union = Capabilities().SemanticTokensProvider!.Legend.TokenTypes;

        Assert.Equal(csharp, union[..csharp.Length]);

        // Switching a pack off shortens the union from the far end and leaves C#'s block exactly
        // where it was, which is what makes an offset a pack can compute from meaningful at all.
        string[] narrowed = Capabilities("--no-webforms").SemanticTokensProvider!.Legend.TokenTypes;
        Assert.Equal(csharp, narrowed[..csharp.Length]);
        Assert.True(narrowed.Length < union.Length);
    }

    [Fact]
    public void TheUnknownControlColourIsNamedInTheLegendAtThePacksOwnOffset()
    {
        // Built from the same registration the server initializes from, so that the assertion
        // below stays a real one as packs are added rather than quietly comparing a legend of one
        // pack against a legend of several.
        var packs = EnabledPacks();
        var pack = packs.OfType<WebFormsLanguage>().Single();
        var session = new LanguageSession(packs);

        // The offsets below are only worth anything if the numbering the pack asks the session for
        // is the numbering the client was handed at initialize.
        Assert.Equal(Capabilities().SemanticTokensProvider!.Legend.TokenTypes, session.Legend.TokenTypes);

        // C# owns the low indices, so the pack's block starts exactly where C#'s ends.
        Assert.Equal(SemanticTokensHandler.TokenTypes.Length, session.TokenTypeOffset(pack));

        // An index past the end of C#'s legend is not enough on its own: unless the union really
        // carries the name, the client has a number for a colour it was never told about and
        // silently drops the token.
        Assert.Contains("unknownControl", session.Legend.TokenTypes);

        // The number the pack emits, computed the way it computes it, has to land on that name.
        int emitted = session.TokenTypeOffset(pack)
            + Array.IndexOf(WebFormsLanguage.SemanticTokenTypeNames, "unknownControl");
        Assert.Equal("unknownControl", session.Legend.TokenTypes[emitted]);
    }

    [Fact]
    public void ProtoOnAdvertisesItsCompletionTriggersAndItsOwnColour()
    {
        var capabilities = Capabilities();

        var completion = capabilities.CompletionProvider;
        Assert.NotNull(completion);
        foreach (string trigger in ProtoTriggers)
            Assert.Contains(trigger, completion!.TriggerCharacters);

        Assert.Contains("unresolvedType", capabilities.SemanticTokensProvider!.Legend.TokenTypes);

        // No file-operation glob, because the pack implements nothing to answer one with. Renaming
        // a .proto should rewrite the import in every file that names it, and until something does
        // that the glob only registers the client to send notifications nobody acts on.
        Assert.DoesNotContain("**/*.proto", FileOperationGlobs(capabilities));

        // Nothing in the grammar takes an argument list — an rpc names one request type and one
        // response type — so there is no parameter position to report and the pack widens nothing.
        Assert.Equal(
            Capabilities("--no-proto").SignatureHelpProvider!.TriggerCharacters,
            capabilities.SignatureHelpProvider!.TriggerCharacters);
    }

    [Fact]
    public void ProtoOffAdvertisesNeitherTheTriggersNorTheColour()
    {
        var capabilities = Capabilities("--no-proto");

        var completion = capabilities.CompletionProvider;
        Assert.NotNull(completion);

        // The space and the slash belong to no other pack, so their absence is proto's absence.
        Assert.DoesNotContain(" ", completion!.TriggerCharacters);
        Assert.DoesNotContain("/", completion.TriggerCharacters);

        // The quote is WebForms' too, and WebForms is one flag away and still on.
        Assert.Contains("\"", completion.TriggerCharacters);

        // A colour cannot be withdrawn after initialize, so a legend entry surviving here is a
        // number the server would never emit and a theme entry the user cannot use.
        Assert.DoesNotContain("unresolvedType", capabilities.SemanticTokensProvider!.Legend.TokenTypes);
    }

    [Fact]
    public void TheUnresolvedTypeColourIsNamedInTheLegendAtThePacksOwnOffset()
    {
        var proto = new ProtoLanguage(new MarkdownFormatter());
        var webForms = new WebFormsLanguage(new MarkdownFormatter());

        // Alone, and behind another pack: the number the pack emits is computed from the session it
        // was handed, so a pack in front of it has to push its block along rather than overlap it.
        foreach (var session in new[]
        {
            new LanguageSession([proto]),
            new LanguageSession([webForms, proto]),
        })
        {
            // C# owns the low indices in both, so nothing the pack emits can land on a C# name.
            Assert.Equal(
                SemanticTokensHandler.TokenTypes,
                session.Legend.TokenTypes[..SemanticTokensHandler.TokenTypes.Length]);
            Assert.True(session.TokenTypeOffset(proto) >= SemanticTokensHandler.TokenTypes.Length);

            // An index past the end of C#'s legend is not enough on its own: unless the union
            // really carries the name, the client has a number for a colour it was never told
            // about and silently drops the token.
            Assert.Contains("unresolvedType", session.Legend.TokenTypes);

            int emitted = session.TokenTypeOffset(proto)
                + Array.IndexOf(ProtoLanguage.SemanticTokenTypeNames, "unresolvedType");
            Assert.Equal("unresolvedType", session.Legend.TokenTypes[emitted]);
        }

        // And the block really did move for the second one, rather than the two packs sharing it.
        var both = new LanguageSession([webForms, proto]);
        Assert.Equal(
            SemanticTokensHandler.TokenTypes.Length + WebFormsLanguage.SemanticTokenTypeNames.Length,
            both.TokenTypeOffset(proto));
        Assert.NotEqual(both.TokenTypeOffset(webForms), both.TokenTypeOffset(proto));

        // The numbering a session computes has to be the numbering the client was handed at
        // initialize, or every one of the offsets above is describing a legend nobody has.
        var registered = new LanguageSession(EnabledPacks());
        Assert.Equal(Capabilities().SemanticTokensProvider!.Legend.TokenTypes, registered.Legend.TokenTypes);
    }

    [Fact]
    public void APacksModifiersAreShiftedClearOfTheCSharpBits()
    {
        // WebForms declares no modifiers of its own, so nothing in the product exercises the
        // bit-shift half of the legend. A pack that does declare some is the only way to cover it,
        // and keeping WebForms alongside proves the two blocks advance independently.
        var webForms = new WebFormsLanguage(new MarkdownFormatter());
        var pack = new ModifierPack();
        var session = new LanguageSession([webForms, pack]);

        string[] expected = [.. SemanticTokensHandler.TokenModifiers, "deprecated", "experimental"];
        Assert.Equal(expected, session.Legend.TokenModifiers);

        // A pack declaring no modifiers consumes no bits: both packs start where C# ended.
        Assert.Equal(SemanticTokensHandler.TokenModifiers.Length, session.TokenModifierOffset(webForms));
        Assert.Equal(SemanticTokensHandler.TokenModifiers.Length, session.TokenModifierOffset(pack));

        // Types are counted separately, so WebForms' one token type still pushes this pack along.
        Assert.Equal(
            SemanticTokensHandler.TokenTypes.Length + WebFormsLanguage.SemanticTokenTypeNames.Length,
            session.TokenTypeOffset(pack));

        int offset = session.TokenModifierOffset(pack);
        long csharpBits = (1L << SemanticTokensHandler.TokenModifiers.Length) - 1;
        long deprecated = 1L << offset;
        long experimental = 1L << (offset + 1);

        // "static" and friends must keep their bits: a pack overlapping them would make every
        // static C# symbol read as deprecated as well.
        Assert.Equal(0L, deprecated & csharpBits);
        Assert.Equal(0L, experimental & csharpBits);
        Assert.NotEqual(deprecated, experimental);

        // The encoded modifier field is one 32-bit integer, so the whole legend has to fit inside
        // it — and every bit below the legend's length is claimed exactly once, with no gaps.
        Assert.True(
            session.Legend.TokenModifiers.Length <= 32,
            $"{session.Legend.TokenModifiers.Length} modifiers cannot be encoded in 32 bits");
        Assert.Equal(
            (1L << session.Legend.TokenModifiers.Length) - 1,
            csharpBits | deprecated | experimental);
    }

    /// <summary>A pack that exists only to declare token modifiers, which no real pack does yet.</summary>
    private sealed class ModifierPack : ILanguagePack
    {
        public string Id => "modifiers";

        public string DisplayName => "Modifier Test Pack";

        public ImmutableArray<string> FileExtensions { get; } = [".modtest"];

        public LanguageCapabilities Capabilities { get; } = new(
            CompletionTriggerCharacters: [],
            SignatureHelpTriggerCharacters: [],
            Commands: [],
            FileOperationGlobs: [],
            SemanticTokenTypes: ["marker"],
            SemanticTokenModifiers: ["deprecated", "experimental"],
            SupportsBreakpoints: false);

        public ImmutableArray<string> WellKnownTypeNames { get; } = [];

        public ImmutableArray<SymbolKind> InterestingSymbolKinds { get; } = [];

        public bool IsProjectionPath(string? filePath) => false;
    }

    /// <summary>Every glob the three file-operation registrations ask for. They are one list by
    /// construction, so a difference between them is itself a failure.</summary>
    private static string[] FileOperationGlobs(ServerCapabilities capabilities)
    {
        var operations = capabilities.Workspace?.FileOperations;
        Assert.NotNull(operations);

        string[] willRename = Globs(operations!.WillRename);
        Assert.Equal(willRename, Globs(operations.DidCreate!));
        Assert.Equal(willRename, Globs(operations.DidDelete!));
        return willRename;

        static string[] Globs(FileOperationRegistration registration) =>
            [.. registration.Filters.Select(filter => filter.Pattern.Glob)];
    }

    /// <summary>The packs these arguments leave registered, in registration order — the same list
    /// <see cref="Capabilities"/> builds its answer from.</summary>
    private static IReadOnlyList<ILanguagePack> EnabledPacks(params string[] args) =>
        LanguagePackRegistration.Create(
            EffectiveSettings.Resolve(args, null, out _), new MarkdownFormatter());

    /// <summary>Initialize as a client would see it, for the packs these arguments leave enabled.</summary>
    private static ServerCapabilities Capabilities(params string[] args)
    {
        var settings = EffectiveSettings.Resolve(args, null, out _);
        var registry = new LanguageRegistry(
            LanguagePackRegistration.Create(settings, new MarkdownFormatter()));

        return new LspServer(new RegistryOnly(registry))
            .Initialize(new InitializeParams(null, null, null, null))
            .Capabilities;
    }

    /// <summary>The registry is the only service <see cref="LspServer.Initialize"/> resolves.</summary>
    private sealed class RegistryOnly(LanguageRegistry registry) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(LanguageRegistry) ? registry : null;
    }
}
