using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Services.Testing;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>
/// The Changed Members view's server side: the working copy's diff mapped onto member
/// declarations, so the client can list what changed by symbol rather than by hunk.
/// </summary>
internal static class ChangedMembersHandler
{
    public static async Task<ChangedMembersResult> GetAsync(ChangedMembersParams p, CancellationToken ct)
    {
        string anchor = p.AnchorPath is { Length: > 0 } given
            ? given
            : WorkspaceService.BoundSolutionPath ?? Environment.CurrentDirectory;

        var scope = p.Scope?.Trim().ToLowerInvariant() switch
        {
            "branch" => GitChangeScope.Branch,
            "ref" or "reference" => GitChangeScope.Ref,
            _ => GitChangeScope.Uncommitted,
        };

        var set = await ChangedMemberService.GetChangedMembersAsync(anchor, scope, p.GitRef, ct);

        return new ChangedMembersResult(
            set.Files
                .Select(f => new ChangedMembersFileInfo(
                    f.FilePath,
                    f.WholeFile,
                    IsTest: f.IsTest,
                    FirstChangedLine: f.FirstChangedLine,
                    Staged: f.Staged,
                    Members:
                    f.Members
                        .Select(m => new ChangedMemberInfo(
                            m.Name, m.ContainerType, m.Namespace, m.Kind,
                            m.StartLine, m.EndLine, m.FirstChangedLine, m.ChangedLineCount,
                            m.Blocks
                                .Select(b => new ChangedBlockInfo(
                                    b.StartLine, b.EndLine, b.Preview, b.Staged))
                                .ToArray(),
                            m.Staged))
                        .ToArray()))
                .ToArray(),
            set.Description,
            set.Error,
            set.DiffBaseRef);
    }
}
