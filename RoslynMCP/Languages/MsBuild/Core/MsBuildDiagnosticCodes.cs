namespace RoslynMCP.Languages.MsBuild.Core;

/// <summary>What the pack can say about a package reference.</summary>
internal static class MsBuildDiagnosticCodes
{
    public const string Source = "roslynSense";

    /// <summary>A newer patch exists — same major and minor.</summary>
    public const string OutdatedPatch = "MSB-NUGET001";

    /// <summary>A newer minor exists within the same major.</summary>
    public const string OutdatedMinor = "MSB-NUGET002";

    /// <summary>A newer major exists.</summary>
    public const string OutdatedMajor = "MSB-NUGET003";

    /// <summary>This exact version carries a published advisory.</summary>
    public const string Vulnerable = "MSB-NUGET010";

    /// <summary>The package is deprecated, whatever version is pinned.</summary>
    public const string Deprecated = "MSB-NUGET011";

    /// <summary>No feed publishes this version, and every feed answered.</summary>
    public const string UnknownVersion = "MSB-NUGET020";

    /// <summary>
    /// Severity for the outdated codes.
    /// </summary>
    /// <remarks>
    /// A hint rather than information. A solution one release behind on forty packages is normal
    /// and not a problem to be fixed today; forty rows in the Problems panel would be, and the
    /// panel is shared with diagnostics that mean something is broken. A hint still underlines and
    /// still carries its quick fixes.
    /// </remarks>
    public const int OutdatedSeverity = 4;

    public const int Error = 1;
    public const int Warning = 2;
}
