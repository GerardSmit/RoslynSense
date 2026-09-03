using System.Collections.Immutable;
using Microsoft.CodeAnalysis.CSharp;

namespace RoslynMCP.Languages.MsBuild.Core;

/// <summary>One value a property accepts.</summary>
internal readonly record struct MsBuildValue(string Value, string? Detail = null, string? Documentation = null);

/// <summary>
/// The property values the vendored corpus does not carry, layered on top of it.
/// </summary>
/// <remarks>
/// Two kinds, and both are here because the corpus cannot hold them. <c>LangVersion</c> is generated
/// from the compiler this server references, so it cannot drift from what that compiler accepts — a
/// static list would go stale the moment Roslyn is upgraded. The target frameworks are a curated
/// list because there is no API that enumerates the interesting ones: <c>NuGetFramework</c>
/// <em>parses</em> a TFM, and it is bound to the MSBuild the locator registered, so reaching for it
/// on a keystroke would force MSBuild to load on a path that has to answer in milliseconds.
/// </remarks>
internal static class MsBuildWellKnownValues
{
    /// <summary>
    /// Properties this file knows that the vendored corpus does not.
    /// </summary>
    /// <remarks>
    /// The corpus was extracted from the MSBuild XSDs and predates most of the SDK-style
    /// properties — it has no <c>Nullable</c> at all, and nothing for <c>ImplicitUsings</c> or
    /// <c>PublishAot</c>. Name completion unions this in, so a property whose values are offered
    /// can also be typed by name; without it the two halves disagree, and the one that goes missing
    /// is the half a modern project is actually made of.
    /// </remarks>
    public static readonly ImmutableArray<string> Additional =
    [
        "LangVersion", "Nullable", "TargetFramework", "TargetFrameworks", "ImplicitUsings",
        "InvariantGlobalization", "PublishAot", "PublishTrimmed", "PublishSingleFile",
        "SelfContained", "RuntimeIdentifier", "RuntimeIdentifiers", "AnalysisMode", "AnalysisLevel",
        "EnforceCodeStyleInBuild", "GenerateDocumentationFile", "ManagePackageVersionsCentrally",
        "CentralPackageTransitivePinningEnabled", "IsPackable", "IsTestProject",
        "UseArtifactsOutput", "AccelerateBuildsInVisualStudio", "EnableNETAnalyzers",
        "TreatWarningsAsErrors", "WarningsAsErrors", "NoWarn", "DebugType", "Platforms",
    ];

    /// <summary>
    /// The values a property offers, or empty when it is not one with a fixed set.
    /// </summary>
    /// <remarks>
    /// The flavour gate is not cosmetic. <c>LangVersion</c> is a real property in a
    /// <c>.vbproj</c> and an <c>.fsproj</c>, and this list is C#'s — offering <c>13.0</c> for
    /// Visual Basic is worse than offering nothing, because it looks authoritative and the build
    /// then fails on a value the editor suggested.
    /// </remarks>
    public static ImmutableArray<MsBuildValue> For(string property, MsBuildFlavour flavour) =>
        property switch
        {
            "LangVersion" => flavour is MsBuildFlavour.CSharp ? LangVersions.Value : [],
            "TargetFramework" or "TargetFrameworks" => TargetFrameworks,
            "Nullable" => Nullable,
            "OutputType" => OutputType,
            "DebugType" => DebugType,
            "AnalysisMode" => AnalysisMode,
            "AnalysisLevel" => AnalysisLevel,
            "Platform" or "Platforms" => Platforms,
            _ => FromCorpus(property),
        };

    /// <summary>
    /// Anything else the vendored corpus says takes a fixed set.
    /// </summary>
    /// <remarks>
    /// This is what makes value completion general instead of one hand-written case per property:
    /// the corpus already records <c>defaultValues</c> for every boolean-ish property MSBuild
    /// defines, so <c>ImplicitUsings</c>, <c>PublishAot</c> and a hundred others work without being
    /// named here.
    /// </remarks>
    private static ImmutableArray<MsBuildValue> FromCorpus(string property) =>
        MsBuildSchemaHelp.Property(property) is { DefaultValues.IsEmpty: false } entry
            ? [.. entry.DefaultValues.Select(v => new MsBuildValue(v))]
            : [];

    /// <summary>
    /// Every C# version the referenced compiler accepts, in the spelling it accepts.
    /// </summary>
    /// <remarks>
    /// Generated from <see cref="LanguageVersionFacts.ToDisplayString"/> rather than written down,
    /// so the list is whatever this build of Roslyn supports and an upgrade updates it for free.
    /// Newest first, because that is the order someone picking a version wants them in — the
    /// aliases lead, since <c>latest</c> and <c>preview</c> are what most projects should say.
    /// </remarks>
    private static readonly Lazy<ImmutableArray<MsBuildValue>> LangVersions = new(() =>
    {
        var aliases = new[]
        {
            new MsBuildValue("latest", "the newest supported version",
                "The latest minor version this compiler supports. Moves when the SDK moves."),
            new MsBuildValue("latestMajor", "the newest supported major version"),
            new MsBuildValue("preview", "including unreleased features",
                "Everything in `latest`, plus features still being designed. Not for a shipping build."),
            new MsBuildValue("default", "the compiler's own default",
                "Whatever the compiler picks for the target framework. This is the value you get by "
                + "omitting the property, so setting it explicitly says nothing."),
        };

        var numbered = Enum.GetValues<LanguageVersion>()
            .Where(v => v is not (LanguageVersion.Default or LanguageVersion.Latest
                or LanguageVersion.LatestMajor or LanguageVersion.Preview))
            .OrderByDescending(v => (int)v)
            .Select(v => new MsBuildValue(
                LanguageVersionFacts.ToDisplayString(v),
                Detail: null,
                Documentation: Highlights.TryGetValue(v, out string? note) ? note : null));

        return [.. aliases, .. numbered];
    });

    /// <summary>
    /// What each version is remembered for. Hand-written, because no API carries it.
    /// </summary>
    /// <remarks>
    /// Deliberately partial: a version with nothing here still appears, it just has no prose. The
    /// list stops where the versions stop being ones anybody chooses on purpose — nobody picks
    /// <c>ISO-1</c> for a new project, and a project already on it does not need reminding why.
    /// </remarks>
    private static readonly Dictionary<LanguageVersion, string> Highlights = new()
    {
        [LanguageVersion.CSharp13] = "Params collections, a `lock` type, `\\e` escapes, "
            + "`ref`/`unsafe` in iterators and async methods.",
        [LanguageVersion.CSharp12] = "Primary constructors on any type, collection expressions "
            + "(`[1, 2, ..rest]`), alias any type, default lambda parameters.",
        [LanguageVersion.CSharp11] = "Raw string literals, required members, generic math, "
            + "list patterns, file-local types, UTF-8 string literals.",
        [LanguageVersion.CSharp10] = "File-scoped namespaces, global usings, record structs, "
            + "extended property patterns, lambda improvements.",
        [LanguageVersion.CSharp9] = "Records, init-only setters, top-level statements, "
            + "pattern-matching enhancements, target-typed `new`.",
        [LanguageVersion.CSharp8] = "Nullable reference types, async streams, ranges and indices, "
            + "default interface members, `switch` expressions, `using` declarations.",
        [LanguageVersion.CSharp7_3] = "Ref locals reassignment, stackalloc initializers, "
            + "unmanaged generic constraints.",
        [LanguageVersion.CSharp7] = "Tuples, `out` variables, pattern matching, local functions, "
            + "`ref` returns.",
        [LanguageVersion.CSharp6] = "String interpolation, null-conditional `?.`, expression-bodied "
            + "members, `nameof`.",
    };

    /// <summary>
    /// The frameworks worth offering, newest first.
    /// </summary>
    /// <remarks>
    /// Curated rather than exhaustive. Every OS-specific variant of every version would be several
    /// hundred entries, nearly all of them noise; what is here is the versions in support plus the
    /// platform suffixes that actually get typed, and the older ones a project being migrated is
    /// moving away from.
    /// </remarks>
    private static readonly ImmutableArray<MsBuildValue> TargetFrameworks =
    [
        new("net10.0"),
        new("net10.0-windows"),
        new("net9.0"),
        new("net9.0-windows"),
        new("net8.0", "long-term support"),
        new("net8.0-windows"),
        new("net8.0-android"),
        new("net8.0-ios"),
        new("net8.0-maccatalyst"),
        new("netstandard2.1", "the last netstandard", "Targets .NET Core 3.0+ and Xamarin. .NET "
            + "Framework cannot consume it — use netstandard2.0 if that matters."),
        new("netstandard2.0", "widest reach",
            "The last version .NET Framework 4.6.1+ can consume, which is why libraries still target it."),
        new("net48"),
        new("net472"),
        new("net462"),
    ];

    private static readonly ImmutableArray<MsBuildValue> Nullable =
    [
        new("enable", "warnings and annotations"),
        new("disable", "neither"),
        new("warnings", "warnings only", "Nullability is analysed and reported, but `?` annotations "
            + "are not honoured. A step on the way to `enable` for an existing codebase."),
        new("annotations", "annotations only", "`?` is honoured but nothing is reported — the API "
            + "surface is annotated for consumers without the project itself having to be clean."),
    ];

    private static readonly ImmutableArray<MsBuildValue> OutputType =
    [
        new("Library", "a .dll"),
        new("Exe", "a console application"),
        new("WinExe", "a windowed application", "Like `Exe`, but starts without a console window."),
    ];

    private static readonly ImmutableArray<MsBuildValue> DebugType =
    [
        new("portable", "a .pdb beside the assembly"),
        new("embedded", "symbols inside the assembly",
            "One file to ship and nothing to lose, at the cost of a larger assembly."),
        new("none", "no symbols"),
        new("full"),
        new("pdbonly"),
    ];

    private static readonly ImmutableArray<MsBuildValue> AnalysisMode =
    [
        new("Default"),
        new("None"),
        new("Minimum"),
        new("Recommended"),
        new("All", "every rule", "Including the ones that contradict each other; expect to suppress."),
    ];

    private static readonly ImmutableArray<MsBuildValue> AnalysisLevel =
    [
        new("latest"),
        new("latest-recommended"),
        new("preview"),
        new("none"),
        new("10.0"),
        new("9.0"),
        new("8.0"),
    ];

    private static readonly ImmutableArray<MsBuildValue> Platforms =
    [
        new("AnyCPU"),
        new("x64"),
        new("x86"),
        new("ARM64"),
    ];
}
