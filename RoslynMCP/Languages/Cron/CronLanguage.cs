using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynMCP.Config;
using RoslynMCP.Languages.Cron.Core;

namespace RoslynMCP.Languages.Cron;

/// <summary>
/// Scheduled jobs: the crontab expressions Hangfire and Quartz are given, and the registrations
/// that hand them over.
/// </summary>
/// <remarks>
/// <para>
/// A pack with no files, like Mediator and Formatting, and for the same reason: what is missing is
/// not a file type but an <i>edge</i>. <c>"0 22 * * 1-6"</c> is a string to the compiler and a
/// schedule to a library that reads it months later on a server nobody is watching, so a
/// transposed field is a job that quietly runs on the wrong day — and the registration itself is a
/// static call in one startup file, which means the job methods look uncalled and the answer to
/// "what runs on this system, and when" exists nowhere in the IDE.
/// </para>
/// <para>
/// The grammar lives in <see cref="Cron"/> rather than here, because two libraries read it and they
/// do not agree: Cronos numbers Sunday 0 and Quartz numbers it 1, so the same six fields name days
/// a day apart. Which reading applies is what <see cref="Core.CronCallSite"/> decides, and it is the
/// one fact a person staring at the string cannot recover from it.
/// </para>
/// </remarks>
internal sealed partial class CronLanguage : ILanguagePack
{
    /// <summary>
    /// The pack id, the <c>roslynSense.languages.*</c> key and the <c>tools.cron</c> gate, one
    /// string so a new surface cannot spell it differently from the last one.
    /// </summary>
    public const string PackId = "cron";

    public CronLanguage(EffectiveSettings settings)
        : this(settings.Cron)
    {
    }

    /// <summary>The settings directly, for the hosts and the tests that have already resolved them.</summary>
    internal CronLanguage(CronSettings settings)
    {
        Settings = settings;
        Jobs = new CronJobIndex(settings);
    }

    internal CronSettings Settings { get; }

    /// <summary>
    /// The registrations found in each compilation, memoized for this pack's lifetime.
    /// </summary>
    /// <remarks>
    /// Owned by the pack rather than static, because what it finds depends on the configured
    /// bindings — and a pack is a singleton per host holding one resolved settings, so this is the
    /// narrowest scope the answer is actually valid in.
    /// </remarks>
    internal CronJobIndex Jobs { get; }

    public string Id => PackId;

    public string DisplayName => "Scheduled jobs";

    /// <summary>
    /// None. A schedule lives inside a <c>.cs</c> file, which the C# routes already cover; the pack
    /// reaches it through the embedded-string seam instead of by owning a document.
    /// </summary>
    public ImmutableArray<string> FileExtensions { get; } = [];

    /// <summary>
    /// Nothing to declare. The fields are coloured with C#'s own <c>method</c>, <c>class</c> and
    /// <c>number</c> — see <see cref="CronColours"/> — and completion inside a schedule is asked for
    /// by typing, not by a trigger character: every character of a crontab expression is one.
    /// </summary>
    public LanguageCapabilities Capabilities => LanguageCapabilities.None;

    /// <summary>
    /// The scheduling libraries. A project resolving neither has no registrations to find, which is
    /// what keeps the pack free in the solutions that do not schedule anything.
    /// </summary>
    /// <remarks>
    /// It does not gate the expression features, and it must not: the parameter-name rule claims a
    /// wrapper method in a project that references no scheduler at all, which is exactly the code
    /// most in need of the help.
    /// </remarks>
    public ImmutableArray<string> WellKnownTypeNames { get; } = CronPresets.WellKnownTypes;

    /// <summary>
    /// A schedule is not a symbol and no caret on one is a caret on a declaration, so no contributor
    /// pass over C# symbols has anything to add.
    /// </summary>
    public ImmutableArray<SymbolKind> InterestingSymbolKinds { get; } = [];

    /// <summary>Nothing is projected: the expression is read where it is written.</summary>
    public bool IsProjectionPath(string? filePath) => false;
}
