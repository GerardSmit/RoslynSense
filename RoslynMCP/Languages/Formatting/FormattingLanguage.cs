using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace RoslynMCP.Languages.Formatting;

/// <summary>
/// Composite format strings and the specifiers inside them: <c>string.Format("{0:dd-MM-yyyy}", …)</c>
/// and <c>$"{DateTime.Now:yyyyMMdd}"</c>, which are a small language of their own living in a C#
/// string.
/// </summary>
/// <remarks>
/// <para>
/// A pack with no files, like Mediator and Logging, and for the same reason: what is missing is not
/// a file type but an <i>edge</i>. A specifier is handed to the value's <c>ToString</c> and read at
/// run time, so <c>dd-mm-yyyy</c> compiles, runs, and prints the minute where the month should be —
/// one keystroke from correct and invisible in review, because both spellings look like a date.
/// </para>
/// <para>
/// The grammar lives in <see cref="FormatString"/> rather than here, because markup writes the same
/// language: <c>DataFormatString="{0:dd-MM-yyyy}"</c> on a grid column is the same specifier read
/// by the same runtime. This pack is the C# half — which literals are format strings, and what the
/// values beside them are — and the WebForms pack is the markup half.
/// </para>
/// <para>
/// The <c>[LoggerMessage]</c>-style analysis the logging pack does is deliberately absent. The
/// compiler already checks a composite string's hole count against its arguments in every modern
/// SDK; what it has never done is tell the reader what <c>MM</c> prints, which is what colour and
/// hover are here for.
/// </para>
/// </remarks>
internal sealed partial class FormattingLanguage : ILanguagePack
{
    /// <summary>
    /// The pack id, the <c>roslynSense.languages.*</c> key and the <c>tools.formatting</c> gate,
    /// one string so a new surface cannot spell it differently from the last one.
    /// </summary>
    public const string PackId = "formatting";

    public string Id => PackId;

    public string DisplayName => "Format strings";

    /// <summary>
    /// None. A format string lives inside a <c>.cs</c> file, which the C# routes already cover; the
    /// pack reaches it through the embedded-string seam instead of by owning a document.
    /// </summary>
    public ImmutableArray<string> FileExtensions { get; } = [];

    /// <summary>
    /// Nothing to declare. The components are coloured with C#'s own <c>method</c>, <c>class</c>
    /// and <c>number</c> — see <see cref="FormatColours"/> — so a theme that already distinguishes
    /// those distinguishes a month from a day with no configuration.
    /// </summary>
    public LanguageCapabilities Capabilities => LanguageCapabilities.None;

    /// <summary>
    /// None, and none is right. Every other pack names a package whose absence means there is
    /// nothing to find; composite formatting is in the runtime, so a project that cannot resolve it
    /// is a project that cannot compile.
    /// </summary>
    public ImmutableArray<string> WellKnownTypeNames { get; } = [];

    /// <summary>
    /// A specifier is not a symbol and no caret on one is a caret on a declaration, so no
    /// contributor pass over C# symbols has anything to add.
    /// </summary>
    public ImmutableArray<SymbolKind> InterestingSymbolKinds { get; } = [];

    /// <summary>Nothing is projected: the specifier is read where it is written.</summary>
    public bool IsProjectionPath(string? filePath) => false;
}
