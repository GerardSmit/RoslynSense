using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynMCP.Config;

namespace RoslynMCP.Languages.Logging;

/// <summary>
/// Structured logging message templates: the <c>"{OrderId} was {Status}"</c> inside a logging call
/// or a <c>[LoggerMessage]</c> attribute, which is a small language of its own living in a C#
/// string.
/// </summary>
/// <remarks>
/// <para>
/// A pack with no files, like Mediator, and for a similar reason: what is missing is not a file
/// type but an <i>edge</i>. A template's holes name the values the call passes, and nothing in C#
/// connects the two — the template is a string, the values are arguments, and the compiler is happy
/// with any pairing of them. The whole cost of that shows up at runtime, in a log line that says
/// the wrong thing or drops a value that was expensive to compute.
/// </para>
/// <para>
/// Four dialects, one engine. Microsoft.Extensions.Logging, Serilog and NLog all implement
/// <see href="https://messagetemplates.org">messagetemplates.org</see>, and all three bind holes to
/// values <b>by position</b> at a call site; the <c>[LoggerMessage]</c> source generator is the one
/// that binds by name. That single difference is why hover exists here: at a call site the names in
/// the template are labels, not a lookup, and the only way to know which value a hole prints is to
/// count.
/// </para>
/// <para>
/// log4net is deliberately absent. <c>ILog.WarnFormat</c> is <c>string.Format</c> with a logger
/// attached — numbered holes, no properties, no structure — so there is no binding to explain and
/// the compiler's own composite-format analysis already covers it.
/// </para>
/// </remarks>
internal sealed partial class LoggingLanguage : ILanguagePack
{
    /// <summary>
    /// The pack id, the <c>roslynSense.languages.*</c> key and the <c>tools.logging</c> gate, one
    /// string so a new surface cannot spell it differently from the last one.
    /// </summary>
    public const string PackId = "logging";

    /// <summary>The id every diagnostic from this pack is reported under.</summary>
    internal const string DiagnosticSource = "roslyn-sense";

    public LoggingLanguage(EffectiveSettings settings)
        : this(settings.Logging)
    {
    }

    /// <summary>The rules directly, for a caller that has resolved them without a whole
    /// <see cref="EffectiveSettings"/> — the tests, and nothing else so far.</summary>
    internal LoggingLanguage(LoggingSettings settings) => Settings = settings;

    /// <summary>Which of the pack's rules this process runs.</summary>
    public LoggingSettings Settings { get; }

    public string Id => PackId;

    public string DisplayName => "Logging templates";

    /// <summary>
    /// None. A template lives inside a <c>.cs</c> file, which the C# routes already cover; the pack
    /// reaches it through the embedded-string seam instead of by owning a document.
    /// </summary>
    public ImmutableArray<string> FileExtensions { get; } = [];

    /// <summary>
    /// <c>{</c> opens a hole, so it is what completion has to trigger on. Everything else is
    /// nothing: no commands, no file globs, and no token types of its own — the holes are coloured
    /// with C#'s own <c>parameter</c> and <c>operator</c>, so a theme that already distinguishes
    /// those distinguishes these.
    /// </summary>
    public LanguageCapabilities Capabilities { get; } = new(
        CompletionTriggerCharacters: ["{"],
        SignatureHelpTriggerCharacters: [],
        Commands: [],
        FileOperationGlobs: [],
        SemanticTokenTypes: [],
        SemanticTokenModifiers: [],
        SupportsBreakpoints: false);

    /// <summary>
    /// One per library, and any one of them is enough. A solution referencing none of them has no
    /// template anywhere, and the detector should never run.
    /// </summary>
    public ImmutableArray<string> WellKnownTypeNames { get; } =
    [
        "Microsoft.Extensions.Logging.ILogger",
        "Serilog.ILogger",
        "NLog.ILogger",
    ];

    /// <summary>
    /// A hole is not a symbol and a template is not a declaration, so no contributor pass over C#
    /// symbols has anything to add. Everything this pack answers arrives through the embedded
    /// string seam, which does not consult this.
    /// </summary>
    public ImmutableArray<SymbolKind> InterestingSymbolKinds { get; } = [];

    /// <summary>Nothing is projected: the template is read where it is written.</summary>
    public bool IsProjectionPath(string? filePath) => false;
}
