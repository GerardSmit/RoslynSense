using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.ChangeSignature;
using Microsoft.CodeAnalysis.CodeRefactorings.MoveType;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMCP.Services.Refactoring;

/// <summary>What a refactoring did, or why it did nothing.</summary>
internal sealed record RefactoringResult(
    bool Ok,
    string Message,
    IReadOnlyList<string> ChangedFiles)
{
    public static RefactoringResult Failed(string message) => new(false, message, []);
}

/// <summary>
/// The two refactorings people miss first: Change Signature and Move Type to File.
/// </summary>
/// <remarks>
/// <para>
/// Both exist inside Roslyn already — <c>ChangeSignatureCodeRefactoringProvider</c> and
/// <c>MoveTypeCodeRefactoringProvider</c> are exported, and this repo loads exported refactorings
/// into both the lightbulb and <c>get_code_actions</c>. Move Type therefore arrives for free.
/// Change Signature does not: it asks a host for the new signature through
/// <c>IChangeSignatureOptionsService</c>, which is Visual Studio's dialog. With no host to ask, the
/// refactoring cannot offer anything.
/// </para>
/// <para>
/// So the work here is supplying that answer programmatically. The caller states the new parameter
/// order — a permutation of the original indices, with omissions meaning removal — and the engine
/// underneath does the rest: the declaration, every override and implementation, every call site,
/// named arguments, and the XML doc <c>&lt;param&gt;</c> tags.
/// </para>
/// </remarks>
internal static class RefactoringService
{
    /// <summary>
    /// Reorders and removes parameters of the member at a position, updating every call site.
    /// </summary>
    /// <param name="newOrder">Original parameter indices in their new order. Omitting an index
    /// removes that parameter. <c>[1, 0]</c> swaps a two-parameter method; <c>[0]</c> drops the
    /// second.</param>
    public static async Task<RefactoringResult> ChangeSignatureAsync(
        Document document, int position, IReadOnlyList<int> newOrder,
        CancellationToken cancellationToken = default)
    {
        var service = document.Project.Services.GetService<AbstractChangeSignatureService>();
        if (service is null)
            return RefactoringResult.Failed("Changing a signature is only supported for C#.");

        var context = await service.GetChangeSignatureContextAsync(
            document, position, restrictToDeclarations: false, cancellationToken);

        if (context is not ChangeSignatureAnalysisSucceededContext succeeded)
        {
            return RefactoringResult.Failed(
                "No method, constructor, indexer or delegate was found at that position. Put the " +
                "cursor on the member's name.");
        }

        var original = succeeded.ParameterConfiguration.ToListOfParameters();
        if (original.Length == 0)
            return RefactoringResult.Failed("That member has no parameters to change.");

        if (newOrder.Count == 0)
        {
            return RefactoringResult.Failed(
                "The new order is empty. To remove every parameter, that is what it would do — " +
                "say so explicitly by listing no indices only when you mean it.");
        }

        foreach (int index in newOrder)
        {
            if (index < 0 || index >= original.Length)
            {
                return RefactoringResult.Failed(
                    $"Parameter index {index} is out of range; the member has {original.Length}.");
            }
        }

        if (newOrder.Distinct().Count() != newOrder.Count)
            return RefactoringResult.Failed("The new order repeats a parameter index.");

        var updated = ImmutableArray.CreateRange(newOrder.Select(i => original[i]));

        // Rebuilt through Create so the this/params/default-value grouping the engine relies on
        // is derived rather than guessed at.
        var configuration = ParameterConfiguration.Create(
            updated, succeeded.ParameterConfiguration.ThisParameter is not null, selectedIndex: 0);

        var change = new SignatureChange(succeeded.ParameterConfiguration, configuration);

        var result = await service.ChangeSignatureWithContextAsync(
            succeeded, new ChangeSignatureOptionsResult(change, previewChanges: false), cancellationToken);

        if (!result.Succeeded)
        {
            return RefactoringResult.Failed(
                result.ConfirmationMessage is { Length: > 0 } message
                    ? message
                    : "The signature could not be changed.");
        }

        return await DescribeAsync(document.Project.Solution, result.UpdatedSolution,
            $"Changed the signature of {succeeded.Symbol.Name}.", cancellationToken);
    }

    /// <summary>
    /// Moves the type at a position into a file of its own, named after it.
    /// </summary>
    /// <remarks>
    /// Exposed directly as well as through the lightbulb because an AI asking "put this type in its
    /// own file" should not have to enumerate code actions and pattern-match a title to find it.
    /// </remarks>
    public static async Task<RefactoringResult> MoveTypeToFileAsync(
        Document document, int position, CancellationToken cancellationToken = default)
    {
        var service = document.Project.Services.GetService<IMoveTypeService>();
        if (service is null)
            return RefactoringResult.Failed("Moving a type is only supported for C#.");

        var updated = await service.GetModifiedSolutionAsync(
            document, new TextSpan(position, 0), MoveTypeOperationKind.MoveType, cancellationToken);

        if (updated is null || updated == document.Project.Solution)
        {
            return RefactoringResult.Failed(
                "There is no type to move at that position, or it is already alone in its file.");
        }

        return await DescribeAsync(
            document.Project.Solution, updated, "Moved the type into its own file.", cancellationToken);
    }

    /// <summary>
    /// Applies the new solution and reports which files it touched.
    /// </summary>
    /// <remarks>
    /// The changed-file list is the useful part of the answer: a signature change reaches call
    /// sites across the solution, and a caller that cannot see where has to diff the working tree
    /// to find out what just happened.
    /// </remarks>
    private static async Task<RefactoringResult> DescribeAsync(
        Solution before, Solution after, string message, CancellationToken cancellationToken)
    {
        var changed = new List<string>();

        // Rebased onto the workspace's live solution rather than applied wholesale: `after` was
        // computed against a snapshot that overlays every open editor buffer, and TryApplyChanges
        // persists whatever differs from CurrentSolution — handing it `after` directly would flush
        // every unsaved buffer to disk as an unreported side effect of the refactoring.
        var target = after.Workspace.CurrentSolution;

        foreach (var projectChange in after.GetChanges(before).GetProjectChanges())
        {
            foreach (var id in projectChange.GetChangedDocuments())
            {
                if (after.GetDocument(id) is not { } document)
                    continue;

                if (document.FilePath is { Length: > 0 } path)
                    changed.Add(path);

                if (target.ContainsDocument(id))
                    target = target.WithDocumentText(id, await document.GetTextAsync(cancellationToken));
            }

            foreach (var id in projectChange.GetAddedDocuments())
            {
                if (after.GetDocument(id) is not { } document)
                    continue;

                if (document.FilePath is { Length: > 0 } path)
                    changed.Add($"{path} (new)");

                var text = await document.GetTextAsync(cancellationToken);
                target = target.AddDocument(DocumentInfo.Create(
                    id, document.Name, document.Folders, SourceCodeKind.Regular,
                    TextLoader.From(TextAndVersion.Create(text, VersionStamp.Create())),
                    document.FilePath));
            }

            foreach (var id in projectChange.GetRemovedDocuments())
            {
                if (before.GetDocument(id)?.FilePath is { Length: > 0 } path)
                    changed.Add($"{path} (removed)");

                if (target.ContainsDocument(id))
                    target = target.RemoveDocument(id);
            }
        }

        if (!after.Workspace.TryApplyChanges(target))
            return RefactoringResult.Failed("The workspace refused the edit; nothing was changed.");

        return new RefactoringResult(true, message, changed);
    }
}
