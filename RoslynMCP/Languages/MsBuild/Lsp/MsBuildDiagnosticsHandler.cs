using NuGet.Versioning;
using RoslynMCP.Languages.MsBuild.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.MsBuild.Lsp;

/// <summary>
/// What a project file's package references are worth saying: outdated, vulnerable, deprecated, or
/// naming a version nobody publishes.
/// </summary>
/// <remarks>
/// Answers only from what is already known. Every reference whose facts are missing reports nothing
/// and starts a fetch, so the first pass over a freshly opened file is quiet and the one a second
/// later is not. That is the whole design — see <see cref="PackageStatusCache"/> — and it is why
/// this method has no <c>await</c> on a feed anywhere in it.
/// </remarks>
internal static class MsBuildDiagnosticsHandler
{
    public static Diagnostic[] Compute(string filePath)
    {
        if (MsBuildDocumentCache.Get(filePath) is not { } document)
            return [];

        var references = MsBuildPackageReader.Read(document);
        if (references.IsEmpty)
            return [];

        var lines = document.Text.Lines;
        var results = new List<Diagnostic>();

        foreach (var reference in references)
        {
            // A reference with no version in this file is centrally managed: the version lives in a
            // Directory.Packages.props, and so does everything worth saying about it.
            if (reference.Version is not { Length: > 0 } version)
                continue;

            if (PackageStatusCache.TryGet(reference.Id, version) is not { } status)
            {
                PackageStatusCache.Prime(reference.Id, version);
                continue;
            }

            // Nothing to say when nobody could tell us. Reporting from a half-answered lookup is how
            // a private feed being briefly unreachable turns into a red squiggle on a valid file.
            if (!status.FeedsHealthy)
                continue;

            results.AddRange(For(reference, version, status, lines));
        }

        return [.. results];
    }

    private static IEnumerable<Diagnostic> For(
        MsBuildPackageRef reference,
        string version,
        PackageStatus status,
        Microsoft.CodeAnalysis.Text.TextLineCollection lines)
    {
        // Where the squiggle goes: the version when this file carries one, the id otherwise.
        var span = reference.VersionSpan.Length > 0 ? reference.VersionSpan : reference.IdSpan;
        var range = LspConverters.ToRange(lines, span);

        if (!NuGetVersion.TryParse(version, out var current))
        {
            // A floating version like `1.*`, or a property reference. Neither is wrong, and neither
            // is something a version comparison can say anything about.
            yield break;
        }

        if (!status.Exists)
        {
            yield return new Diagnostic(
                range,
                MsBuildDiagnosticCodes.Error,
                MsBuildDiagnosticCodes.UnknownVersion,
                MsBuildDiagnosticCodes.Source,
                $"No feed publishes {reference.Id} {version}.");

            yield break;
        }

        foreach (var vulnerability in status.Vulnerabilities)
        {
            yield return new Diagnostic(
                range,
                // Critical is an error because shipping it is not a judgement call. Everything
                // below stays a warning, so a moderate advisory on a dev dependency does not fail
                // a build configured to treat warnings as errors.
                vulnerability.Severity >= 3 ? MsBuildDiagnosticCodes.Error : MsBuildDiagnosticCodes.Warning,
                MsBuildDiagnosticCodes.Vulnerable,
                MsBuildDiagnosticCodes.Source,
                $"{reference.Id} {version} has a known {SeverityName(vulnerability.Severity)} "
                + "severity vulnerability.")
            {
                CodeDescription = vulnerability.AdvisoryUrl is { Length: > 0 } url
                    ? new CodeDescription(url)
                    : null,
            };
        }

        if (status.Deprecation is { } deprecation)
        {
            string alternative = deprecation.AlternatePackageId is { Length: > 0 } replacement
                ? $" Use {replacement} instead."
                : string.Empty;

            yield return new Diagnostic(
                range,
                MsBuildDiagnosticCodes.Warning,
                MsBuildDiagnosticCodes.Deprecated,
                MsBuildDiagnosticCodes.Source,
                $"{reference.Id} is deprecated.{alternative}")
            {
                Tags = [LspDiagnosticTag.Deprecated],
            };
        }

        if (Newest(status.Versions, current) is { } newest)
        {
            var (code, kind) = newest.Major > current.Major
                ? (MsBuildDiagnosticCodes.OutdatedMajor, "major")
                : newest.Minor > current.Minor
                    ? (MsBuildDiagnosticCodes.OutdatedMinor, "minor")
                    : (MsBuildDiagnosticCodes.OutdatedPatch, "patch");

            yield return new Diagnostic(
                range,
                MsBuildDiagnosticCodes.OutdatedSeverity,
                code,
                MsBuildDiagnosticCodes.Source,
                $"{reference.Id} {version} is behind {newest} ({kind}).");
        }
    }

    /// <summary>
    /// The newest stable release above the pinned one, or null when there is none.
    /// </summary>
    /// <remarks>
    /// Prereleases are excluded unless the pinned version is itself one. Someone on a stable version
    /// is not "behind" a release candidate, and saying so would put a hint on every up-to-date
    /// reference to a package that publishes nightlies.
    /// </remarks>
    private static NuGetVersion? Newest(
        System.Collections.Immutable.ImmutableArray<NuGetVersion> versions, NuGetVersion current)
    {
        NuGetVersion? best = null;

        foreach (var candidate in versions)
        {
            if (candidate.IsPrerelease && !current.IsPrerelease)
                continue;

            if (candidate > current && (best is null || candidate > best))
                best = candidate;
        }

        return best;
    }

    private static string SeverityName(int severity) => severity switch
    {
        >= 3 => "critical",
        2 => "high",
        1 => "moderate",
        _ => "low",
    };
}
