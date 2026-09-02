using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMCP.Services.Testing;

namespace RoslynMCP.Services;

/// <summary>One contiguous run of changed lines inside a member, clipped to the member.</summary>
/// <param name="Preview">The first changed line's text, trimmed — enough for a list of blocks
/// to read as "which edit is this" without opening anything.</param>
/// <param name="Staged">Whether every line of the run is already staged.</param>
public sealed record ChangedBlock(int StartLine, int EndLine, string Preview, bool Staged);

/// <summary>One member declaration the diff touched, located where it is in the file now.</summary>
/// <param name="FirstChangedLine">The first changed line inside the member — where a click that
/// asks "what changed here" should land. For a whole-file change it is the member's own name,
/// because every line is changed and the name is the only line worth landing on.</param>
/// <param name="Blocks">The member's changed runs, in file order. Empty for a whole-file change,
/// where "which lines" has no useful answer.</param>
/// <param name="Staged">Whether nothing in the member is left to stage — the whole of its
/// change is in the index already.</param>
/// <param name="Removed">Whether the diff deleted the member outright. It has no lines of its
/// own any more: the line fields all point at where the deletion is visible — the spot the
/// removal collapsed onto in the file as it is now, or, for a deleted file, the member's own
/// line in the base revision, the only version left to open.</param>
public sealed record ChangedMember(
    string Name,
    string ContainerType,
    string Namespace,
    string Kind,
    int StartLine,
    int EndLine,
    int FirstChangedLine,
    int ChangedLineCount,
    IReadOnlyList<ChangedBlock> Blocks,
    bool Staged,
    bool Removed = false);

/// <summary>A changed file and the members the diff touched inside it.</summary>
/// <param name="Members">Empty when the file is not C# or could not be read or parsed — the
/// file is still worth listing, there is just nothing below it to point at.</param>
/// <param name="IsTest">Whether the nearest project file calls itself a test project — the
/// half of a diff a reviewer usually wants to see apart, or not at all.</param>
/// <param name="FirstChangedLine">The file's first changed line — where a click on the file
/// itself should land when there are no members to click instead.</param>
/// <param name="Staged">Whether the file's whole change is staged.</param>
/// <param name="ChangedLineCount">How many lines the diff touched in the file. Zero for a
/// whole-file change, where the count would only repeat "all of them".</param>
/// <param name="Deleted">Whether the diff deleted the file itself. Its members all list as
/// removed, and only the base revision is left to open.</param>
public sealed record ChangedMemberFile(
    string FilePath,
    bool WholeFile,
    IReadOnlyList<ChangedMember> Members,
    bool IsTest,
    int FirstChangedLine = 1,
    bool Staged = false,
    int ChangedLineCount = 0,
    bool Deleted = false);

/// <summary>What the diff touched, member by member, or why that could not be answered.</summary>
/// <param name="DiffBaseRef">The revision the diff compared against, for a client that wants to
/// show the same comparison side by side.</param>
public sealed record ChangedMemberSet(
    IReadOnlyList<ChangedMemberFile> Files,
    string Description,
    string? Error = null,
    string? DiffBaseRef = null)
{
    public static ChangedMemberSet Failed(string error) => new([], "", error);
}

/// <summary>
/// Maps a git diff onto member declarations, so a change can be reviewed symbol by symbol
/// instead of hunk by hunk.
/// </summary>
/// <remarks>
/// The files are parsed from disk rather than taken from an open workspace: the diff's new-side
/// line numbers describe the working tree, and disk is the working tree. Syntax alone answers
/// "which member owns this line", so no project ever needs to load for the view to fill.
/// </remarks>
public static class ChangedMemberService
{
    public static async Task<ChangedMemberSet> GetChangedMembersAsync(
        string anchorPath,
        GitChangeScope scope = GitChangeScope.Uncommitted,
        string? reference = null,
        CancellationToken ct = default)
    {
        var changes = await GitChangeService.GetChangesAsync(anchorPath, scope, reference, ct);
        if (changes.Error is not null)
            return ChangedMemberSet.Failed(changes.Error);

        // For naming what a deletion deleted: removed members only exist in the diff base.
        string? repository = GitChangeService.FindRepositoryRoot(anchorPath);

        var files = new List<ChangedMemberFile>();

        foreach (var file in changes.Files)
        {
            ct.ThrowIfCancellationRequested();

            // Generated designer files change whenever their source does and nobody reviews them.
            if (file.FilePath.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)
                || file.FilePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
                continue;

            // Only C# breaks down into members; anything else — markup, contracts, configs —
            // lists as a bare file the client may show or hide.
            bool isCSharp = file.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

            files.Add(new ChangedMemberFile(
                file.FilePath,
                file.WholeFile && !file.Deleted,
                isCSharp ? await ReadMembersAsync(file, repository, changes.DiffTarget, ct) : [],
                IsInTestProject(file.FilePath),
                file.WholeFile ? 1 : file.Ranges.Min(r => r.Start),
                file.IsFullyStaged,
                file.WholeFile ? 0 : file.Ranges.Sum(r => r.End - r.Start + 1),
                Deleted: file.Deleted));
        }

        return new ChangedMemberSet(files, changes.Description, DiffBaseRef: changes.DiffTarget);
    }

    /// <summary>
    /// Whether the nearest project file up the directory tree is a test project. No workspace is
    /// consulted, matching the rest of this service; <see cref="ProjectClassifier"/> caches per
    /// project file, so a diff with many files in one project pays for one read.
    /// </summary>
    private static bool IsInTestProject(string filePath)
    {
        for (string? directory = Path.GetDirectoryName(filePath);
             !string.IsNullOrEmpty(directory);
             directory = Path.GetDirectoryName(directory))
        {
            string[] projects;
            try
            {
                projects = Directory.GetFiles(directory, "*.csproj");
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }

            if (projects.Length > 0)
                return projects.Any(p => ProjectClassifier.Classify(p).IsTestProject);
        }

        return false;
    }

    private static async Task<IReadOnlyList<ChangedMember>> ReadMembersAsync(
        ChangedFile file, string? repository, string? diffTarget, CancellationToken ct)
    {
        var root = file.Deleted ? null : ParseFromDisk(file.FilePath, ct);
        IReadOnlyList<ChangedMember> members = root is null ? [] : CollectMembers(root, file);

        // What the diff deleted outright has no declaration on disk to find; only the base
        // revision can still name it.
        if (file.RemovedRanges is not { Count: > 0 } || repository is null || diffTarget is null)
            return members;

        string? oldSource = await GitChangeService.ReadFileAtAsync(
            repository, diffTarget, Path.GetRelativePath(repository, file.FilePath), ct);
        if (oldSource is null)
            return members;

        var oldRoot = CSharpSyntaxTree.ParseText(oldSource, cancellationToken: ct).GetRoot(ct);
        var removed = CollectRemovedMembers(oldRoot, root, file);
        return removed.Count == 0
            ? members
            : members.Concat(removed).OrderBy(m => m.StartLine).ToList();
    }

    private static SyntaxNode? ParseFromDisk(string filePath, CancellationToken ct)
    {
        string source;
        try
        {
            source = File.ReadAllText(filePath);
        }
        catch (IOException)
        {
            // Mid-write, locked, or gone since the diff ran; the file node still lists.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return CSharpSyntaxTree.ParseText(source, cancellationToken: ct).GetRoot(ct);
    }

    /// <summary>Every member declaration whose lines the diff touched, in file order.</summary>
    internal static IReadOnlyList<ChangedMember> CollectMembers(SyntaxNode root, ChangedFile file)
    {
        var members = new List<ChangedMember>();
        var lines = root.SyntaxTree.GetText().Lines;

        foreach (var node in root.DescendantNodes())
        {
            if (NameAndKind(node) is not { } named)
                continue;
            var (name, kind, nameToken) = named;

            var span = node.GetLocation().GetLineSpan();
            int start = span.StartLinePosition.Line + 1;
            int end = span.EndLinePosition.Line + 1;

            // The span covers attributes through body, so an edit anywhere in the member counts.
            List<ChangedBlock> blocks = file.WholeFile
                ? []
                : file.Ranges
                    .Where(r => r.Start <= end && r.End >= start)
                    .Select(r =>
                    {
                        int from = Math.Max(r.Start, start);
                        int to = Math.Min(r.End, end);
                        return new ChangedBlock(from, to, Preview(lines, from), file.IsStaged(from, to));
                    })
                    .OrderBy(b => b.StartLine)
                    .ToList();
            if (!file.WholeFile && blocks.Count == 0)
                continue;

            int nameLine = nameToken.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

            members.Add(new ChangedMember(
                name,
                ContainerOf(node),
                NamespaceOf(node),
                kind,
                start,
                end,
                file.WholeFile ? nameLine : blocks[0].StartLine,
                file.WholeFile
                    ? end - start + 1
                    : blocks.Sum(b => b.EndLine - b.StartLine + 1),
                blocks,
                file.WholeFile ? file.IsFullyStaged : blocks.All(b => b.Staged)));
        }

        members.Sort((a, b) => a.StartLine.CompareTo(b.StartLine));
        return members;
    }

    /// <summary>
    /// The declarations the diff deleted: present in the base revision, gone from the file as it
    /// is now. A removed type is one row — its members went with it and would only repeat it.
    /// Rows point at the line the deletion collapsed onto in the new file; for a deleted file
    /// (<paramref name="newRoot"/> null) they keep their base-revision lines, since that version
    /// is the only one left to open.
    /// </summary>
    internal static IReadOnlyList<ChangedMember> CollectRemovedMembers(
        SyntaxNode oldRoot, SyntaxNode? newRoot, ChangedFile file)
    {
        if (file.RemovedRanges is not { Count: > 0 } cuts)
            return [];

        HashSet<(string, string, string, string)> kept = newRoot is null
            ? []
            : Declarations(newRoot)
                .Select(d => (d.Name, d.Kind, d.Container, d.Namespace))
                .ToHashSet();

        var removed = new List<ChangedMember>();
        var removedTypes = new HashSet<SyntaxNode>();

        foreach (var d in Declarations(oldRoot))
        {
            var span = d.Node.GetLocation().GetLineSpan();
            int start = span.StartLinePosition.Line + 1;
            int end = span.EndLinePosition.Line + 1;

            var hits = cuts
                .Where(c => c.OldStart <= end && c.OldEnd >= start)
                .OrderBy(c => c.OldStart)
                .ToList();
            if (hits.Count == 0 || kept.Contains((d.Name, d.Kind, d.Container, d.Namespace)))
                continue;
            if (d.Node.Ancestors().Any(removedTypes.Contains))
                continue;
            if (d.Node is BaseTypeDeclarationSyntax)
                removedTypes.Add(d.Node);

            int anchor = newRoot is null ? start : hits[0].NewLine;

            removed.Add(new ChangedMember(
                d.Name, d.Container, d.Namespace, d.Kind,
                anchor, anchor, anchor,
                hits.Sum(c => Math.Min(c.OldEnd, end) - Math.Max(c.OldStart, start) + 1),
                [],
                file.IsStaged(anchor, anchor),
                Removed: true));
        }

        return removed;
    }

    /// <summary>
    /// Every named declaration, one entry per name: field and event variables come apart so a
    /// single deleted variable can be named alone, and types count too, so a removed type can
    /// stand as one row for everything inside it.
    /// </summary>
    private static IEnumerable<(string Name, string Kind, string Container, string Namespace, SyntaxNode Node)>
        Declarations(SyntaxNode root)
    {
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case BaseTypeDeclarationSyntax type:
                    yield return (type.Identifier.Text, TypeKindOf(type),
                        ContainerOf(node), NamespaceOf(node), node);
                    break;
                case FieldDeclarationSyntax field:
                    foreach (var variable in field.Declaration.Variables)
                        yield return (variable.Identifier.Text, "field",
                            ContainerOf(node), NamespaceOf(node), node);
                    break;
                case EventFieldDeclarationSyntax @event:
                    foreach (var variable in @event.Declaration.Variables)
                        yield return (variable.Identifier.Text, "event",
                            ContainerOf(node), NamespaceOf(node), node);
                    break;
                default:
                    if (NameAndKind(node) is { } named)
                        yield return (named.Name, named.Kind,
                            ContainerOf(node), NamespaceOf(node), node);
                    break;
            }
        }
    }

    private static string TypeKindOf(BaseTypeDeclarationSyntax type) =>
        type switch
        {
            RecordDeclarationSyntax r =>
                r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword)
                    ? "record struct"
                    : "record",
            InterfaceDeclarationSyntax => "interface",
            StructDeclarationSyntax => "struct",
            EnumDeclarationSyntax => "enum",
            _ => "class",
        };

    /// <summary>The text a block leads with, cut to a row's worth.</summary>
    private static string Preview(Microsoft.CodeAnalysis.Text.TextLineCollection lines, int line)
    {
        if (line < 1 || line > lines.Count)
            return "";

        string text = lines[line - 1].ToString().Trim();
        return text.Length <= 80 ? text : text[..80];
    }

    /// <summary>
    /// The members worth a row of their own: the things one reads as "a method" or "a property".
    /// Types themselves are not rows — a changed type is its changed members; local functions
    /// belong to the method that declares them.
    /// </summary>
    private static (string Name, string Kind, SyntaxToken NameToken)? NameAndKind(SyntaxNode node) =>
        node switch
        {
            MethodDeclarationSyntax m => (m.Identifier.Text, "method", m.Identifier),
            ConstructorDeclarationSyntax c => (c.Identifier.Text, "constructor", c.Identifier),
            DestructorDeclarationSyntax d => ($"~{d.Identifier.Text}", "method", d.Identifier),
            OperatorDeclarationSyntax o =>
                ($"operator {o.OperatorToken.Text}", "operator", o.OperatorToken),
            ConversionOperatorDeclarationSyntax c =>
                ($"{c.ImplicitOrExplicitKeyword.Text} operator {c.Type}", "operator",
                    c.ImplicitOrExplicitKeyword),
            PropertyDeclarationSyntax p => (p.Identifier.Text, "property", p.Identifier),
            IndexerDeclarationSyntax i => ("this[]", "property", i.ThisKeyword),
            EventDeclarationSyntax e => (e.Identifier.Text, "event", e.Identifier),
            EventFieldDeclarationSyntax e when FirstVariable(e.Declaration) is { } v =>
                (Names(e.Declaration), "event", v.Identifier),
            FieldDeclarationSyntax f when FirstVariable(f.Declaration) is { } v =>
                (Names(f.Declaration), "field", v.Identifier),
            _ => null,
        };

    private static VariableDeclaratorSyntax? FirstVariable(VariableDeclarationSyntax declaration) =>
        declaration.Variables.FirstOrDefault();

    private static string Names(VariableDeclarationSyntax declaration) =>
        string.Join(", ", declaration.Variables.Select(v => v.Identifier.Text));

    private static string ContainerOf(SyntaxNode node) =>
        string.Join(".", node.Ancestors()
            .OfType<BaseTypeDeclarationSyntax>()
            .Reverse()
            .Select(t => t.Identifier.Text));

    private static string NamespaceOf(SyntaxNode node) =>
        string.Join(".", node.Ancestors()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse()
            .Select(n => n.Name.ToString()));
}
