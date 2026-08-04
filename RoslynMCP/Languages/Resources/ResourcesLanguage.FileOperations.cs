using RoslynMCP.Languages.Resources.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Services.ProjectModel;

namespace RoslynMCP.Languages.Resources;

internal sealed partial class ResourcesLanguage : ILanguageFileOperationProvider
{
    /// <summary>
    /// What every <c>.resx</c> code generator names its output after the file it generated from —
    /// <c>Strings.resx</c> gives <c>Strings.Designer.cs</c>, not <c>Strings.resx.Designer.cs</c>.
    /// </summary>
    private const string DesignerSuffix = ".Designer.cs";

    /// <summary>
    /// Renaming <c>Strings.resx</c> carries the family with it: every translation, every
    /// customization, and the generated designer beside them.
    /// </summary>
    /// <remarks>
    /// This is the case that genuinely needs <see cref="WorkspaceEdit.DocumentChanges"/>, unlike
    /// renaming a key: files really are moving, and a resource operation is the only form the
    /// protocol can carry one in. Leaving <c>Strings.nl-NL.resx</c> behind would not break the
    /// build — it would quietly stop being a translation of anything, which is worse.
    /// <para>
    /// Only the base file drags. Renaming <c>Strings.nl-NL.resx</c> to <c>Strings.de-DE.resx</c> is
    /// a statement about that one file's culture and nothing else's.
    /// </para>
    /// <para>
    /// The designer moves, but its contents are left alone. It declares a class named after the old
    /// file and looks the resources up by a base name that has just changed, and both are the
    /// generator's to write — rewriting the class without the <c>ResourceManager</c> base name it
    /// is paired with would compile and then find nothing at runtime.
    /// </para>
    /// </remarks>
    public async Task<WorkspaceEdit?> WillRenameAsync(RenameFilesParams p, CancellationToken ct)
    {
        var moves = new List<object>();

        foreach (var rename in p.Files)
        {
            ct.ThrowIfCancellationRequested();

            string oldPath = LspConverters.UriToPath(rename.OldUri);
            string newPath = LspConverters.UriToPath(rename.NewUri);

            if (!IsResx(oldPath) || !IsResx(newPath))
                continue;
            if (oldPath.Equals(newPath, StringComparison.OrdinalIgnoreCase))
                continue;

            if (ResourceDocuments.FamilyOf(oldPath, Settings.Discovery.Overrides) is not { } family)
                continue;

            string oldStem = Path.GetFileNameWithoutExtension(oldPath);
            if (!oldStem.Equals(family.BaseName, StringComparison.OrdinalIgnoreCase))
                continue;

            string newStem = Path.GetFileNameWithoutExtension(newPath);
            string newDirectory = Path.GetDirectoryName(newPath) ?? family.Directory;

            foreach (var member in family.Files)
            {
                string stem = Path.GetFileNameWithoutExtension(member.FilePath);
                if (stem.Length <= family.BaseName.Length)
                    continue;

                // The variant segments verbatim, rather than rebuilt from the culture and the
                // override rank: DNN lower-cases a name to look a file up and re-cases it to write
                // one, so re-deriving the tail would rename half a family as well as move it.
                string tail = stem[family.BaseName.Length..];

                await MoveAsync(member.FilePath, Path.Combine(newDirectory, newStem + tail + ".resx"), moves, ct);
            }

            string designer = Path.Combine(family.Directory, oldStem + DesignerSuffix);
            if (File.Exists(designer))
                await MoveAsync(designer, Path.Combine(newDirectory, newStem + DesignerSuffix), moves, ct);

            // The base file last, so that the DependentUpon naming it on everything just moved is
            // re-pointed by the same pass that moves its own item.
            await MoveItemAsync(oldPath, newPath, ct);
        }

        // No text edits at all: what a resource file's name means is where it sits, not anything
        // written inside it or inside anything that reads it.
        return moves.Count == 0
            ? null
            : new WorkspaceEdit(new Dictionary<string, TextEdit[]>(StringComparer.OrdinalIgnoreCase), [.. moves]);
    }

    /// <summary>
    /// A <c>.resx</c> appeared. Membership is a function of file names, so a family that did not
    /// know about this one has to be regrouped before anything reads it.
    /// </summary>
    public Task DidCreateAsync(CreateFilesParams p, CancellationToken ct)
    {
        foreach (var file in p.Files)
            ResourceCatalogService.InvalidateLayout(LspConverters.UriToPath(file.Uri));

        return Task.CompletedTask;
    }

    /// <summary>
    /// A <c>.resx</c> was deleted. Its project item goes with it — a legacy site lists every
    /// resource file as an <c>EmbeddedResource</c> and one pointing at nothing does not build — and
    /// its family is regrouped. The rest of the family stays: the editor deleted exactly what the
    /// user selected.
    /// </summary>
    public async Task DidDeleteAsync(DeleteFilesParams p, CancellationToken ct)
    {
        foreach (var file in p.Files)
        {
            ct.ThrowIfCancellationRequested();

            string path = LspConverters.UriToPath(file.Uri);
            if (!IsResx(path))
                continue;

            ResourceCatalogService.InvalidateLayout(path);

            try
            {
                await ProjectMutationService.ForgetDeletedFileAsync(path, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                ServiceLog.Warn(
                    $"Could not clean up after '{Path.GetFileName(path)}': {ex.Message}",
                    key: $"resx-delete-cleanup-failed:{path}");
            }
        }
    }

    private static async Task MoveAsync(
        string oldPath, string newPath, List<object> moves, CancellationToken ct)
    {
        moves.Add(new RenameFile(
            LspConverters.PathToUri(oldPath), LspConverters.PathToUri(newPath)));

        await MoveItemAsync(oldPath, newPath, ct);
    }

    /// <summary>
    /// Carries one of the family's project items over to the new path.
    /// </summary>
    /// <remarks>
    /// A legacy site lists every resource file explicitly and nests the designer under the base
    /// file with <c>DependentUpon</c>, so the items have to follow the files or the project stops
    /// building and the tree stops nesting. Done here rather than after the rename because there is
    /// no didRename to do it in, and a failure is logged rather than raised: the rename itself is
    /// still worth performing.
    /// </remarks>
    private static async Task MoveItemAsync(string oldPath, string newPath, CancellationToken ct)
    {
        try
        {
            await ProjectMutationService.RenameFileItemAsync(oldPath, newPath, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ServiceLog.Warn(
                $"Could not move the project item for '{Path.GetFileName(oldPath)}': {ex.Message}",
                key: $"resx-rename-item-failed:{oldPath}");
        }
    }

    private static bool IsResx(string path) =>
        Path.GetExtension(path).Equals(".resx", StringComparison.OrdinalIgnoreCase);
}
