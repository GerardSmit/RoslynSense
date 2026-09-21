using Microsoft.CodeAnalysis;
using RoslynMCP.Services;

namespace RoslynMCP.Lsp;

/// <summary>
/// Resolves queued background work to what a workspace holds <em>now</em>, for work that was
/// queued against a snapshot and only dequeues later.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="Microsoft.CodeAnalysis.Document"/> or <see cref="Microsoft.CodeAnalysis.Project"/> is a
/// Solution snapshot, and a snapshot holds every compilation it has built. Background work that
/// captures one and waits behind a gate pins that snapshot for as long as it waits, and then
/// computes over a version that has moved on. Queue the workspace and the id instead, and ask here
/// when the slot is taken.
/// </para>
/// <para>
/// The eviction check comes first and is not redundant with the lookup. A disposed workspace still
/// answers <c>CurrentSolution</c> with the last solution it had, so the lookup alone would find the
/// document and run a full pass over a solution nothing serves.
/// </para>
/// </remarks>
internal static class LiveSnapshot
{
    /// <summary>The document as its workspace holds it now, or null when the workspace was evicted
    /// or the document left it.</summary>
    public static Document? Document(Workspace workspace, DocumentId documentId) =>
        WorkspaceService.IsEvicted(workspace) ? null : workspace.CurrentSolution.GetDocument(documentId);

    /// <summary>The project as its workspace holds it now, or null when the workspace was evicted
    /// or the project left it.</summary>
    public static Project? Project(Workspace workspace, ProjectId projectId) =>
        WorkspaceService.IsEvicted(workspace) ? null : workspace.CurrentSolution.GetProject(projectId);
}
