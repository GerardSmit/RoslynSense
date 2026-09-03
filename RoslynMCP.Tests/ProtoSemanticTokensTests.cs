using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages;
using RoslynMCP.Languages.Proto;
using RoslynMCP.Languages.Proto.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// semanticTokens for a <c>.proto</c>, which the pack answers for exactly one reason: whether a
/// type reference resolves is not a question a TextMate grammar can ask.
/// </summary>
/// <remarks>
/// Everything else the pass emits is a restatement of what a grammar already knows, and is
/// deliberately emitted onto C#'s own legend entries rather than onto names of the pack's own — so
/// one theme colours the contract and the code protoc generates from it the same way, with nothing
/// for the user to configure. That is what the shared-entry assertions below are about; the pack's
/// single name of its own is the one a grammar cannot produce.
/// </remarks>
[Collection(SharedState.Name)]
public class ProtoSemanticTokensTests
{
    private static TextDocumentIdentifier Doc(string path) =>
        new(LspConverters.PathToUri(path));

    [Fact]
    public async Task AResolvedTypeReferenceIsColouredWithTheEntryCSharpAlreadyDefines()
    {
        var (text, tokens) = await TokensOfAsync(FixturePaths.WidgetsProtoFile);

        // A message is a class and an enum is an enum, said in C#'s own legend words. Naming these
        // from the pack instead would mean a theme that colours the generated Widget class leaves
        // the `Widget` in the contract uncoloured until the user configures a second scope.
        Assert.Equal(Shared("class"), TypeAt(text, tokens, "Widget widgets"));
        Assert.Equal(Shared("class"), TypeAt(text, tokens, "common.UUID"));
        Assert.Equal(Shared("enum"), TypeAt(text, tokens, "common.Channel"));

        // One of protoc's own types, whose .proto need not be anywhere on this machine — the
        // resolution falls back to the well-known table and the colour must not change with it.
        Assert.Equal(Shared("class"), TypeAt(text, tokens, "google.protobuf.Timestamp"));

        // A scalar names a built-in rather than a declaration, so it is C#'s `type` and never the
        // unresolved colour: `int64` resolving to no declaration is the correct answer for it.
        Assert.Equal(Shared("type"), TypeAt(text, tokens, "int64 ids"));
    }

    [Fact]
    public async Task ADeclarationIsColouredAsWhatProtocGeneratesFromIt()
    {
        var (text, tokens) = await TokensOfAsync(FixturePaths.WidgetsProtoFile);

        Assert.Equal(Shared("class"), TypeAt(text, tokens, "service WidgetService", "service ".Length));
        Assert.Equal(Shared("class"), TypeAt(text, tokens, "message WidgetEvent", "message ".Length));
        Assert.Equal(Shared("method"), TypeAt(text, tokens, "GetWidgetsById("));
        Assert.Equal(Shared("enum"), TypeAt(text, tokens, "enum Kind", "enum ".Length));
        Assert.Equal(Shared("enumMember"), TypeAt(text, tokens, "KIND_CREATED"));
        Assert.Equal(Shared("property"), TypeAt(text, tokens, "ids = 1"));

        // A oneof generates a `…Case` property and nothing else carrying its name, so colouring it
        // as a property agrees with where F12 on it lands.
        Assert.Equal(Shared("property"), TypeAt(text, tokens, "oneof payload", "oneof ".Length));

        // The word in front of each of those is a keyword and the name after it is not, even though
        // protobuf reserves neither: `message message = 1;` is a legal field, so membership in the
        // grammar's word list cannot decide this on its own. The parse claims every span it read as
        // a name first, and only what is left over is coloured as a keyword.
        Assert.Equal(Shared("keyword"), TypeAt(text, tokens, "message WidgetEvent"));
        Assert.Equal(Shared("keyword"), TypeAt(text, tokens, "repeated Widget"));
    }

    [Fact]
    public async Task AMisspelledTypeReferenceIsColouredWithThePacksOwnUnresolvedEntry()
    {
        const string Proto = """
            syntax = "proto3";

            package scratch;

            message Known {
              string value = 1;
            }

            message Uses {
              Known good = 1;
              Knwon typo = 2;
            }
            """;

        await WithScratchProtoAsync("typo.proto", Proto, async path =>
        {
            var pack = Pack();
            var session = new LanguageSession([pack]);
            var tokens = Decode(await pack.SemanticTokensFullAsync(
                new SemanticTokensParams(Doc(path)), session, default));

            int good = TypeAt(Proto, tokens, "Known good");
            int typo = TypeAt(Proto, tokens, "Knwon typo");

            // The entire reason this pack answers semanticTokens. A grammar matches `Knwon` exactly
            // as happily as `Known`: it sees the shape of a name and has no scope to ask, so a
            // misspelling, a type moved to another package and a forgotten import all read as
            // completely ordinary code until protoc rejects the build.
            Assert.NotEqual(good, typo);
            Assert.Equal(Shared("class"), good);

            // A colour C# has no name for, so it is the pack's own — past the end of C#'s legend,
            // and really carried in the union the client was handed at initialize. An index the
            // legend does not name is a token the client silently drops.
            Assert.True(
                typo >= SemanticTokensHandler.TokenTypes.Length,
                $"the unresolved-type token must be the pack's own, not C# index {typo}");
            Assert.Equal("unresolvedType", session.Legend.TokenTypes[typo]);

            // Computed the way the pack computes it, so the offset and the name cannot drift apart.
            Assert.Equal(
                session.TokenTypeOffset(pack)
                    + Array.IndexOf(ProtoLanguage.SemanticTokenTypeNames, "unresolvedType"),
                typo);
        });
    }

    [Fact]
    public async Task TheEncodingIsWellFormedForEveryTokenInTheFile()
    {
        var pack = Pack();
        var session = new LanguageSession([pack]);

        var tokens = await pack.SemanticTokensFullAsync(
            new SemanticTokensParams(Doc(FixturePaths.WidgetsProtoFile)), session, default);

        Assert.NotEmpty(tokens.Data);
        Assert.Equal(0, tokens.Data.Length % 5);

        int line = 0;
        int character = 0;

        for (int i = 0; i < tokens.Data.Length; i += 5)
        {
            int deltaLine = tokens.Data[i];
            int deltaCharacter = tokens.Data[i + 1];
            int length = tokens.Data[i + 2];
            int type = tokens.Data[i + 3];

            // Positions are relative to the token before, so the sequence has to be sorted — the
            // pass collects declaration names, then the references inside them, then re-lexes the
            // whole file, and a client decoding an out-of-order array walks backwards off the line.
            Assert.True(deltaLine >= 0, $"token {i / 5} moves back to line {line + deltaLine}");
            Assert.True(deltaCharacter >= 0, $"token {i / 5} moves back within its line");
            Assert.True(length > 0, $"token {i / 5} is empty");

            // A number the legend does not name is a token the client drops on the floor.
            Assert.InRange(type, 0, session.Legend.TokenTypes.Length - 1);

            // The pack declares no modifiers, so every modifier field is empty rather than
            // borrowing a bit C# already owns.
            Assert.Equal(0, tokens.Data[i + 4]);

            line += deltaLine;
            character = deltaLine == 0 ? character + deltaCharacter : deltaCharacter;
            Assert.True(character >= 0, $"token {i / 5} starts before the line does");
        }
    }

    // ---- Helpers ----------------------------------------------------------------------------

    private static ProtoLanguage Pack() => new(new MarkdownFormatter());

    private static int Shared(string name)
    {
        int index = LanguageSession.SharedTokenType(name);
        Assert.True(index >= 0, $"C#'s legend has no '{name}' entry");
        return index;
    }

    private static async Task<(string Text, List<(int Line, int Char, int Length, int Type)> Tokens)>
        TokensOfAsync(string path)
    {
        var pack = Pack();
        var tokens = await pack.SemanticTokensFullAsync(
            new SemanticTokensParams(Doc(path)), new LanguageSession([pack]), default);

        return (await File.ReadAllTextAsync(path), Decode(tokens));
    }

    /// <summary>The token type at the position <paramref name="needle"/> starts on.</summary>
    private static int TypeAt(
        string text,
        List<(int Line, int Char, int Length, int Type)> tokens,
        string needle,
        int offsetIntoNeedle = 0)
    {
        int index = text.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{needle}' is not in the file");

        var position = SourceText.From(text).Lines.GetLinePosition(index + offsetIntoNeedle);
        var token = Assert.Single(
            tokens, t => t.Line == position.Line && t.Char == position.Character);

        return token.Type;
    }

    private static List<(int Line, int Char, int Length, int Type)> Decode(SemanticTokens tokens)
    {
        var decoded = new List<(int, int, int, int)>();
        int line = 0, character = 0;

        for (int i = 0; i < tokens.Data.Length; i += 5)
        {
            line += tokens.Data[i];
            character = tokens.Data[i] == 0 ? character + tokens.Data[i + 1] : tokens.Data[i + 1];
            decoded.Add((line, character, tokens.Data[i + 2], tokens.Data[i + 3]));
        }

        return decoded;
    }

    /// <summary>
    /// Runs the body against a real <c>.proto</c> outside any project, which is all this pass
    /// needs: it classifies from the parse and the import graph, never from a compilation.
    /// </summary>
    private static async Task WithScratchProtoAsync(string fileName, string text, Func<string, Task> body)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"rsense-proto-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        string path = Path.Combine(directory, fileName);
        await File.WriteAllTextAsync(path, text);

        try
        {
            await body(path);
        }
        finally
        {
            ProtoDocumentService.Invalidate(path);
            Directory.Delete(directory, recursive: true);
        }
    }
}
