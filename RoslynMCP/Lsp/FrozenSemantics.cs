using Microsoft.CodeAnalysis;
using RoslynMCP.Services;
using System.Runtime.CompilerServices;

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
    /// re-run. Changed declarations in another document require the current solution; body-only
    /// edits can keep the frozen path. Diagnostics must always bind the real solution because a
    /// partial compilation is not sufficient to establish the absence of errors.
    /// </summary>
    public static async Task<Document> FreezeAsync(this Document document, CancellationToken ct)
    {
        var timing = RunwayTrace.Begin("freeze");
        timing?.Mark($"requested document={document.Id} project={document.Project.Name} " +
            $"currentState={RuntimeHelpers.GetHashCode(document.Project.State)}");
        // A source-generated document is not in the regular document map the freeze API reads,
        // and a frozen snapshot would not re-run its generator anyway — hand it back as is.
        if (document is SourceGeneratedDocument)
        {
            timing?.Mark("selected current: source-generated document");
            return document;
        }

        // The public API's fast path, mirrored: a compilation that already exists means
        // generators and skeletons already ran, and freezing would only discard the semantic
        // models cached on this instance.
        if (document.Project.TryGetCompilation(out var currentCompilation))
        {
            timing?.Mark($"selected current: existing compilation={RuntimeHelpers.GetHashCode(currentCompilation)}");
            return document;
        }

        timing?.Mark("current compilation unavailable");

        var frozen = document.Project.Solution
            .WithFrozenPartialCompilationIncludingSpecificDocument(document.Id, ct)
            .GetDocument(document.Id) ?? document;
        timing?.Mark($"created frozen document: frozenState={RuntimeHelpers.GetHashCode(frozen.Project.State)} " +
            $"sameProjectState={ReferenceEquals(document.Project.State, frozen.Project.State)}");

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
            timing?.Mark("obtained frozen compilation");
            if (frozenCompilation is not null && frozenCompilation.SyntaxTrees.Count() <= 1)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Parse this project while independent dependencies compile. Roslyn's
                        // own trackers share the work with completion and import-cache warming.
                        // Cancellation stops optional priming; the original cold compilation
                        // still finishes so the next keystroke can reuse it.
                        var priming = ColdCompilationPrimer.PrimeAsync(document.Project, ct);
                        await Task.WhenAll(priming, document.Project.GetCompilationAsync(CancellationToken.None));
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                    catch (Exception ex)
                    {
                        ServiceLog.Warn($"Could not warm '{document.Project.Name}': {ex.Message}",
                            key: $"cold-compilation:{document.Project.Id}");
                    }
                });
                timing?.Mark("selected current: cold frozen compilation has at most one tree; started current compilation");
                return document;
            }
        }

        // Roslyn overlays only the requested document onto the frozen state. A property added in
        // A.cs therefore disappeared from completion in unchanged B.cs until a background bind
        // finished. Keep the fast path for body edits, but require every declaration the request
        // can see to match the current snapshot.
        bool currentDeclarations = await HasCurrentDeclarationsAsync(document.Project, frozen.Project, ct, timing);
        timing?.Mark(currentDeclarations ? "selected frozen: declarations current" : "selected current: stale frozen declarations");
        return currentDeclarations ? frozen : document;
    }

    private static async Task<bool> HasCurrentDeclarationsAsync(
        Project project, Project frozen, CancellationToken ct, RunwayTrace.Operation? timing)
    {
        var solution = project.Solution;
        var scope = solution.GetProjectDependencyGraph()
            .GetProjectsThatThisProjectTransitivelyDependsOn(project.Id).Prepend(project.Id);

        foreach (var projectId in scope)
        {
            ct.ThrowIfCancellationRequested();
            var current = solution.GetProject(projectId)!;
            var previous = frozen.Solution.GetProject(projectId);
            if (previous is null)
            {
                timing?.Mark($"rejected frozen: project absent {projectId}");
                return false;
            }
            if (ReferenceEquals(current.State, previous.State))
                continue;

            timing?.Mark($"checking changed project={current.Name} " +
                $"currentState={RuntimeHelpers.GetHashCode(current.State)} frozenState={RuntimeHelpers.GetHashCode(previous.State)}");

            // Project.Version covers references/options and project attributes. Additional and
            // analyzer-config files can change generated declarations without changing C# text.
            if (current.Version != previous.Version)
            {
                timing?.Mark($"rejected frozen: project version {current.Name} current={current.Version} frozen={previous.Version}");
                return false;
            }
            if (!current.State.AdditionalDocumentStates.Equals(previous.State.AdditionalDocumentStates))
            {
                timing?.Mark($"rejected frozen: additional documents {current.Name}");
                return false;
            }
            if (!current.State.AnalyzerConfigDocumentStates.Equals(previous.State.AnalyzerConfigDocumentStates))
            {
                timing?.Mark($"rejected frozen: analyzer configuration {current.Name}");
                return false;
            }

            var changes = current.GetChanges(previous);
            if (changes.GetAddedDocuments().Any() || changes.GetRemovedDocuments().Any())
            {
                timing?.Mark($"rejected frozen: documents added or removed {current.Name}");
                return false;
            }

            // Compare only divergent documents, never the aggregate latest semantic version:
            // a newer edit in B can mask an older missing declaration change in A in that MAX.
            // These stamps parse changed trees when necessary; they do not compile projects.
            foreach (var documentId in changes.GetChangedDocuments())
            {
                var newDocument = current.GetDocument(documentId)!;
                var oldDocument = previous.GetDocument(documentId)!;
                if (newDocument.FilePath != oldDocument.FilePath
                    || newDocument.SourceCodeKind != oldDocument.SourceCodeKind)
                {
                    timing?.Mark($"rejected frozen: document path or kind {documentId} path={newDocument.FilePath}");
                    return false;
                }
                var currentVersion = await newDocument.GetTopLevelChangeTextVersionAsync(ct);
                var frozenVersion = await oldDocument.GetTopLevelChangeTextVersionAsync(ct);
                if (currentVersion != frozenVersion)
                {
                    timing?.Mark($"rejected frozen: document declarations {documentId} path={newDocument.FilePath} " +
                        $"current={currentVersion} frozen={frozenVersion}");
                    return false;
                }
            }
        }

        return true;
    }
}
