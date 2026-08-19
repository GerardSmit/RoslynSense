using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynMCP.Lsp.Protocol;
using LspDiagnostic = RoslynMCP.Lsp.Protocol.Diagnostic;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Languages;

/// <summary>
/// A language that lives inside a C# string literal rather than in files of its own — a route
/// template in <c>[HttpGet("api/{id}")]</c>, a GraphQL document, a SQL statement.
/// </summary>
/// <remarks>
/// Deliberately not <see cref="ILanguagePack"/>. A pack is resolved from a file extension and
/// owns whole documents; this is resolved from the symbol a literal flows into and owns a span
/// inside someone else's document. The two are orthogonal, so a language may implement both —
/// which is how GraphQL will answer for <c>.graphql</c> files and for GraphQL-in-C#-strings out
/// of one folder — and ASP.NET Core, which has no file type at all, implements only this one.
/// <para>
/// Detection is Roslyn's, not ours: <see cref="RoslynEmbeddedLanguages"/> asks
/// <c>EmbeddedLanguageDetector</c>, which resolves a literal through <c>[StringSyntax]</c> on the
/// parameter, field, property or local it reaches — chasing assignments and interpolation format
/// clauses — and through <c>// lang=id</c> comments. Both signals key off
/// <see cref="StringSyntaxIdentifiers"/>, so a language declares its identifiers and gets the
/// whole of that analysis for free.
/// </para>
/// </remarks>
internal interface IEmbeddedStringLanguage
{
    /// <summary>
    /// The identifiers this language answers to, as they appear in <c>[StringSyntax("Route")]</c>
    /// and in <c>// lang=route</c>. Matched case-insensitively, and a language may claim several.
    /// </summary>
    /// <remarks>
    /// Roslyn's third detection signal — matching well-known APIs by parameter name with no
    /// annotation at all — is not available here: <c>SupportsUnannotatedAPIs</c> is hardcoded to
    /// <c>Regex</c> and <c>Json</c>, so anything else has to be reachable from an attribute or a
    /// comment. Which is the better rule anyway; a name-based guess is how you end up parsing
    /// someone's error message as a query.
    /// </remarks>
    ImmutableArray<string> StringSyntaxIdentifiers { get; }
}

/// <summary>
/// A language whose literals carry no annotation for Roslyn to find, so it decides for itself
/// which tokens are its own.
/// </summary>
/// <remarks>
/// Roslyn's detector resolves an identifier from the symbol the literal flows into — a
/// <c>[StringSyntax]</c> on the parameter, field, property or local it reaches, or a
/// <c>// lang=</c> comment above it. Neither can be made to say yes here. "Synthesising" the
/// attribute would mean writing one onto a method in an assembly we do not own, and the third
/// signal — well-known APIs with no annotation at all — is hardcoded to Regex and Json, which
/// <see cref="IEmbeddedStringLanguage.StringSyntaxIdentifiers"/> records. What a resource key
/// actually is, "argument N of a method the user named in <c>roslynsense.json</c>", is not
/// something any Roslyn signal can express.
/// <para>
/// So the seam keeps asking one question and widens who may answer it. Roslyn's detector runs
/// first — an attribute or a comment is unambiguous, and is the case it was built for — and this
/// runs only on the tokens it declined.
/// </para>
/// </remarks>
internal interface IConfiguredStringLanguage : IEmbeddedStringLanguage
{
    /// <summary>The identifier this language claims for the token, or null. Called only for tokens
    /// Roslyn's own detector already declined.</summary>
    /// <remarks>
    /// Runs against every string literal in the document on the diagnostics pass, so an
    /// implementation rejects on syntax before it binds anything.
    /// <para>
    /// The document comes along because a literal's meaning can depend on a declaration in another
    /// project — the method a configuration key is passed to, whose body says whether it is one —
    /// and reaching that needs the solution the model alone does not carry.
    /// </para>
    /// </remarks>
    Task<string?> DetectAsync(
        Document document, SyntaxToken token, SemanticModel semanticModel, CancellationToken ct);
}

/// <summary>
/// One literal that turned out to be an embedded language, with everything the language needs to
/// answer about it: which of its identifiers matched, the token, and the semantics around it.
/// </summary>
/// <param name="Language">The language that declared <paramref name="Identifier"/>.</param>
/// <param name="Identifier">The identifier the attribute or comment actually named, which matters
/// when a language claims more than one.</param>
/// <param name="Options">The extra words from a <c>// lang=id,opt1,opt2</c> comment. Empty when
/// the literal was found through <c>[StringSyntax]</c>, which carries no options.</param>
/// <param name="Token">The string literal token, whose span is the whole literal including its
/// quotes — the text between them is what the language parses.</param>
/// <param name="Position">The caret, for the request-shaped providers. Meaningless for the
/// document-wide diagnostic pass, which reports over the token instead.</param>
internal readonly record struct EmbeddedStringContext(
    IEmbeddedStringLanguage Language,
    string Identifier,
    ImmutableArray<string> Options,
    Document Document,
    SemanticModel SemanticModel,
    SyntaxToken Token,
    int Position);

/// <summary>
/// Completion inside the literal — a route constraint, a GraphQL field name.
/// </summary>
/// <remarks>
/// Items must be complete as sent. <c>completionItem/resolve</c> carries no document, and an
/// embedded literal has no URI of its own to route one back by; the resolve handler leaves an
/// item with no <c>data</c> payload untouched, so a self-contained item survives the round trip
/// and anything else silently would not.
/// </remarks>
internal interface IEmbeddedCompletionProvider
{
    Task<CompletionList> CompletionAsync(
        EmbeddedStringContext context, CompletionParams p, CancellationToken ct);
}

/// <summary>
/// Go-to-definition from inside the literal: <c>{id}</c> in a route template is a reference to the
/// action's parameter, and F12 on it should land there rather than on nothing.
/// </summary>
internal interface IEmbeddedDefinitionProvider
{
    Task<LspLocation[]> DefinitionAsync(
        EmbeddedStringContext context, bool typeDefinition, CancellationToken ct);
}

/// <summary>
/// Hover inside the literal: what a resource key resolves to, what a route parameter binds to.
/// </summary>
/// <remarks>
/// <c>HoverHandler</c> goes straight from the caret to <c>FindSymbolAtPositionAsync</c>, which
/// binds to nothing inside a literal — so without this the key under the cursor hovers blank.
/// </remarks>
internal interface IEmbeddedHoverProvider
{
    Task<Hover?> HoverAsync(EmbeddedStringContext context, CancellationToken ct);
}

/// <summary>
/// Problems in one literal — an unterminated route parameter, an unknown constraint.
/// </summary>
/// <remarks>
/// Called once per embedded literal in the document rather than once per document, because the
/// language sees a span and not a file: ranges are relative to the document the literal sits in,
/// which <see cref="EmbeddedStringContext.Token"/> gives it.
/// </remarks>
internal interface IEmbeddedDiagnosticProvider
{
    Task<IReadOnlyList<LspDiagnostic>> DiagnosticsAsync(
        EmbeddedStringContext context, CancellationToken ct);
}
