using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMCP.Services.Testing;

namespace RoslynMCP.Services;

/// <summary>One contiguous run of changed lines inside a member, clipped to the member.</summary>
/// <param name="Preview">The first changed line's text, trimmed — enough for a list of blocks
/// to read as "which edit is this" without opening anything.</param>
public sealed record ChangedBlock(int StartLine, int EndLine, string Preview);

/// <summary>One member declaration the diff touched, located where it is in the file now.</summary>
/// <param name="FirstChangedLine">The first changed line inside the member — where a click that
/// asks "what changed here" should land. For a whole-file change it is the member's own name,
/// because every line is changed and the name is the only line worth landing on.</param>
/// <param name="Blocks">The member's changed runs, in file order. Empty for a whole-file change,
/// where "which lines" has no useful answer.</param>
public sealed record ChangedMember(
    string Name,
    string ContainerType,
    string Namespace,
    string Kind,
    int StartLine,
    int EndLine,
    int FirstChangedLine,
    int ChangedLineCount,
    IReadOnlyList<ChangedBlock> Blocks);

/// <summary>A changed source file and the members the diff touched inside it.</summary>
/// <param name="Members">Empty when the file could not be read or parsed — the file is still
/// worth listing, there is just nothing below it to point at.</param>
/// <param name="IsTest">Whether the nearest project file calls itself a test project — the
/// half of a diff a reviewer usually wants to see apart, or not at all.</param>
public sealed record ChangedMemberFile(
    string FilePath,
    bool WholeFile,
    IReadOnlyList<ChangedMember> Members,
    bool IsTest);

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

        var files = new List<ChangedMemberFile>();

        foreach (var file in changes.Files)
        {
            ct.ThrowIfCancellationRequested();

            if (!file.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                continue;

            // Generated designer files change whenever their source does and nobody reviews them.
            if (file.FilePath.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)
                || file.FilePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
                continue;

            files.Add(new ChangedMemberFile(
                file.FilePath, file.WholeFile, ReadMembers(file, ct), IsInTestProject(file.FilePath)));
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

    private static IReadOnlyList<ChangedMember> ReadMembers(ChangedFile file, CancellationToken ct)
    {
        string source;
        try
        {
            source = File.ReadAllText(file.FilePath);
        }
        catch (IOException)
        {
            // Mid-write, locked, or gone since the diff ran; the file node still lists.
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        var root = CSharpSyntaxTree.ParseText(source, cancellationToken: ct).GetRoot(ct);
        return CollectMembers(root, file);
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
                        return new ChangedBlock(from, Math.Min(r.End, end), Preview(lines, from));
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
                blocks));
        }

        members.Sort((a, b) => a.StartLine.CompareTo(b.StartLine));
        return members;
    }

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
