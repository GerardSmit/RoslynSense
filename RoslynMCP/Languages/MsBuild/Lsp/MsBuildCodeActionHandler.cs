using NuGet.Versioning;
using RoslynMCP.Languages.MsBuild.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using Range = RoslynMCP.Lsp.Protocol.Range;

namespace RoslynMCP.Languages.MsBuild.Lsp;

/// <summary>
/// The fixes for an outdated or vulnerable reference: move it to a newer version, or move all of
/// them at once.
/// </summary>
/// <remarks>
/// Every edit is a <see cref="WorkspaceEdit"/> over the exact span of the version, never a write to
/// disk. <c>CentralPackageVersionWriter</c> exists for the MCP tools, where there is no buffer and
/// writing the file is the only option; from the editor the same write would bypass undo and lose
/// an unsaved change in a file the user has open. The span comes from the parse, so the rest of the
/// line — the attribute order, the alignment, the comment above it — is untouched by construction.
/// </remarks>
internal static class MsBuildCodeActionHandler
{
    public static CodeAction[] Compute(CodeActionParams p)
    {
        string path = LspConverters.UriToPath(p.TextDocument.Uri);

        if (MsBuildDocumentCache.Get(path) is not { } document)
            return [];

        var references = MsBuildPackageReader.Read(document);
        if (references.IsEmpty)
            return [];

        var lines = document.Text.Lines;
        int from = LspConverters.ToOffset(document.Text, p.Range.Start);
        int to = LspConverters.ToOffset(document.Text, p.Range.End);

        var actions = new List<CodeAction>();
        var everyUpgrade = new List<TextEdit>();

        foreach (var reference in references)
        {
            if (Upgrade(reference) is not { } upgrade)
                continue;

            var range = LspConverters.ToRange(lines, reference.VersionSpan);
            everyUpgrade.Add(new TextEdit(range, upgrade.Newest.ToString()));

            // Only the reference the cursor is actually on gets its own actions; the rest
            // contribute to the fix-all below.
            if (reference.VersionSpan.End < from || reference.VersionSpan.Start > to)
                continue;

            foreach (var (label, version) in upgrade.Choices)
            {
                actions.Add(new CodeAction(
                    $"Update {reference.Id} to {version} ({label})",
                    "quickfix",
                    Edit(p.TextDocument.Uri, range, version.ToString())));
            }

            // Client-owned: the panel is the extension's, and the server has no way to show it.
            // A command on the action rather than a server command, because workspace/executeCommand
            // is the list the server answers and this is not one of ours.
            actions.Add(new CodeAction($"Manage {reference.Id} in the NuGet panel", "refactor", null)
            {
                Command = NuGetPanelCommand.For(document.FilePath, reference.Id),
            });
        }

        // Worth offering only when it does more than the single fix above already does. After a
        // framework bump a project can be thirty versions behind, and fixing those one at a time is
        // not a fix.
        if (everyUpgrade.Count > 1)
        {
            actions.Add(new CodeAction(
                $"Update all {everyUpgrade.Count} outdated packages in this file",
                "quickfix",
                new WorkspaceEdit(new Dictionary<string, TextEdit[]>
                {
                    [p.TextDocument.Uri] = [.. everyUpgrade],
                })));
        }

        return [.. actions];
    }

    private static WorkspaceEdit Edit(string uri, Range range, string version) =>
        new(new Dictionary<string, TextEdit[]> { [uri] = [new TextEdit(range, version)] });

    /// <summary>
    /// The versions worth offering for one reference: the newest patch, minor and major above it.
    /// </summary>
    /// <remarks>
    /// Deduplicated, because they frequently coincide — a package one patch behind has the same
    /// answer for all three, and offering "to 1.0.1 (patch)", "to 1.0.1 (minor)" and "to 1.0.1
    /// (major)" is three ways to spell one fix.
    /// </remarks>
    private static Upgrades? Upgrade(MsBuildPackageRef reference)
    {
        if (reference.Version is not { Length: > 0 } version
            || reference.VersionSpan.Length == 0
            || !NuGetVersion.TryParse(version, out var current))
        {
            return null;
        }

        if (PackageStatusCache.TryGet(reference.Id, version) is not { FeedsHealthy: true } status)
            return null;

        NuGetVersion? patch = null, minor = null, major = null;

        foreach (var candidate in status.Versions)
        {
            if (candidate <= current || (candidate.IsPrerelease && !current.IsPrerelease))
                continue;

            if (candidate.Major == current.Major && candidate.Minor == current.Minor)
                patch = Max(patch, candidate);
            else if (candidate.Major == current.Major)
                minor = Max(minor, candidate);
            else
                major = Max(major, candidate);
        }

        var newest = Max(Max(patch, minor), major);
        if (newest is null)
            return null;

        var choices = new List<(string Label, NuGetVersion Version)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (label, candidate) in new[] { ("patch", patch), ("minor", minor), ("major", major) })
        {
            if (candidate is not null && seen.Add(candidate.ToString()))
                choices.Add((label, candidate));
        }

        return new Upgrades(newest, choices);
    }

    private static NuGetVersion? Max(NuGetVersion? left, NuGetVersion? right) =>
        left is null ? right : right is null ? left : right > left ? right : left;

    private readonly record struct Upgrades(
        NuGetVersion Newest, IReadOnlyList<(string Label, NuGetVersion Version)> Choices);
}

/// <summary>
/// The client command that opens the NuGet panel scoped to one package.
/// </summary>
/// <remarks>
/// A contract with TypeScript that no compiler checks: the extension registers
/// <c>roslynSense.manageNuGetForProject</c> taking a node whose <c>id</c> is <c>project:&lt;path&gt;</c>
/// and an optional package to select. Built in one place so the shape is asserted once.
/// </remarks>
internal static class NuGetPanelCommand
{
    public const string Name = "roslynSense.manageNuGetForProject";

    public static Command For(string projectPath, string packageId) =>
        new($"Manage {packageId} in NuGet", Name, [new { id = $"project:{projectPath}" }, packageId]);
}
