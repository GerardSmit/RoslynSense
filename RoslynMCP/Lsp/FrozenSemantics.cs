using Microsoft.CodeAnalysis;

namespace RoslynMCP.Lsp;

/// <summary>
/// The freeze that actually freezes. <see cref="Document.WithFrozenPartialSemantics(CancellationToken)"/>
/// is gated on <c>Solution.PartialSemanticsEnabled</c>, which only hosts that override
/// <c>Workspace.PartialSemanticsEnabled</c> ever get — MSBuildWorkspace is sealed and never does —
/// so the public call returns the document unchanged, and every post-edit request drives the
/// compilation tracker to a final compilation: a full rebind of the edited project plus a
/// source-generator run. The internal entry point used here (publicized — see csproj) skips that
/// gate and shares Roslyn's per-Solution frozen-document memo, so every handler that freezes the
/// same post-keystroke solution gets the same frozen compilation between them.
/// </summary>
internal static class FrozenSemantics
{
    /// <summary>
    /// Freezes <paramref name="document"/>'s project in whatever compilation state exists right
    /// now: at least this document's tree is present; generators and skeleton references are not
    /// re-run. For latency-first features only — the snapshot is deliberately stale for other
    /// documents, so diagnostics must keep binding the real solution (frozen state would publish
    /// false errors as squiggles).
    /// </summary>
    public static async Task<Document> FreezeAsync(this Document document, CancellationToken ct)
    {
        // A source-generated document is not in the regular document map the freeze API reads,
        // and a frozen snapshot would not re-run its generator anyway — hand it back as is.
        if (document is SourceGeneratedDocument)
            return document;

        // The public API's fast path, mirrored: a compilation that already exists means
        // generators and skeletons already ran, and freezing would only discard the semantic
        // models cached on this instance.
        if (document.Project.TryGetCompilation(out _))
            return document;

        var frozen = document.Project.Solution
            .WithFrozenPartialCompilationIncludingSpecificDocument(document.Id, ct)
            .GetDocument(document.Id) ?? document;

        // Freezing assumes a background compiler has been building this project — that is the
        // state it snapshots. A tracker that never started holds nothing, and its freeze is a
        // compilation of just this document: siblings' types vanish, extension methods stop
        // resolving, tokens degrade. Detectable exactly, because the freeze keeps only trees
        // that already existed: one tree for a multi-document project is the never-built case.
        // (The await is what makes the check possible at all — the frozen compilation object is
        // assembled lazily, so TryGetCompilation says nothing until it is asked for; asking is
        // cheap here because a frozen tracker never binds.) Take the full bind once — what every
        // request paid before freezing existed — and start the build the next freeze snapshots.
        if (document.Project.DocumentIds.Count > 1)
        {
            var frozenCompilation = await frozen.Project.GetCompilationAsync(ct);
            if (frozenCompilation is not null && frozenCompilation.SyntaxTrees.Count() <= 1)
            {
                _ = Task.Run(() => document.Project.GetCompilationAsync(CancellationToken.None));
                return document;
            }
        }

        return frozen;
    }
}
