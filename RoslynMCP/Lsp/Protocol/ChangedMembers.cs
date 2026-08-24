using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

/// <param name="Scope">uncommitted | branch | ref</param>
public sealed record ChangedMembersParams(
    [property: JsonPropertyName("scope")] string Scope = "uncommitted",
    [property: JsonPropertyName("gitRef")] string? GitRef = null,
    /// <summary>Any path inside the repository; the workspace root when the client has one.</summary>
    [property: JsonPropertyName("anchorPath")] string? AnchorPath = null);

/// <summary>One contiguous run of changed lines inside a member, with the text it leads with.</summary>
public sealed record ChangedBlockInfo(
    [property: JsonPropertyName("startLine")] int StartLine,
    [property: JsonPropertyName("endLine")] int EndLine,
    [property: JsonPropertyName("preview")] string Preview,
    /// <summary>Whether every line of the run is staged already.</summary>
    [property: JsonPropertyName("staged")] bool Staged);

public sealed record ChangedMemberInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("containerType")] string ContainerType,
    [property: JsonPropertyName("namespace")] string Namespace,
    /// <summary>method | constructor | operator | property | event | field</summary>
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("startLine")] int StartLine,
    [property: JsonPropertyName("endLine")] int EndLine,
    /// <summary>Where a click lands: the first changed line inside the member, 1-based.</summary>
    [property: JsonPropertyName("firstChangedLine")] int FirstChangedLine,
    [property: JsonPropertyName("changedLineCount")] int ChangedLineCount,
    /// <summary>The member's changed runs in file order; empty for a whole-file change.</summary>
    [property: JsonPropertyName("blocks")] ChangedBlockInfo[] Blocks,
    /// <summary>Whether the member's whole change is staged — nothing of it is left dirty.
    /// A client may read that as "already reviewed".</summary>
    [property: JsonPropertyName("staged")] bool Staged);

public sealed record ChangedMembersFileInfo(
    [property: JsonPropertyName("filePath")] string FilePath,
    /// <summary>New or untracked: every line is a change, so per-member counts mean little.</summary>
    [property: JsonPropertyName("wholeFile")] bool WholeFile,
    [property: JsonPropertyName("members")] ChangedMemberInfo[] Members,
    /// <summary>Whether the file belongs to a test project.</summary>
    [property: JsonPropertyName("isTest")] bool IsTest,
    /// <summary>The file's first changed line — the landing spot when there are no members to
    /// click instead, as for non-C# files, which list with an empty member array.</summary>
    [property: JsonPropertyName("firstChangedLine")] int FirstChangedLine,
    /// <summary>Whether the file's whole change is staged. Always false outside the
    /// uncommitted scope, where staging says nothing about what a diff contains.</summary>
    [property: JsonPropertyName("staged")] bool Staged);

/// <summary>
/// The Changed Members view's data: each changed source file with the members the diff touched.
/// How the members nest — by file, by namespace, or not at all — is the client's rendering
/// choice, so the shape here stays flat.
/// </summary>
public sealed record ChangedMembersResult(
    [property: JsonPropertyName("files")] ChangedMembersFileInfo[] Files,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("error")] string? Error,
    /// <summary>The revision the diff compared against — what a side-by-side view's left half
    /// should show. "HEAD" for uncommitted changes, a merge-base sha for a branch.</summary>
    [property: JsonPropertyName("diffBaseRef")] string? DiffBaseRef);
