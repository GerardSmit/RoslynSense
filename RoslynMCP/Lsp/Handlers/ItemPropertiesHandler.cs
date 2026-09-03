using RoslynMCP.Languages.DotSettings.Core;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Services.ProjectModel;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>
/// The Properties panel's two requests: what the project says about a file or folder, and the
/// edit that changes it.
/// </summary>
/// <remarks>
/// <para>
/// A file's build action, where it copies to, and which tool generates it are MSBuild item
/// metadata; a folder's one property is a ReSharper setting. They are answered together because
/// they are one dialog to the person opening it, and because the tree node the dialog opens from
/// does not know which kind of answer it is about to get.
/// </para>
/// <para>
/// Reads go through the evaluation cache, so opening the panel on a project that is already
/// loaded costs nothing. Writes go through the same mutation service the tree's own edits use,
/// which is what keeps a project-file rewrite undoable and gets the tree refreshed afterwards.
/// </para>
/// </remarks>
internal static class ItemPropertiesHandler
{
    public static async Task<ItemPropertiesResult> GetAsync(
        ItemPropertiesParams p, CancellationToken ct)
    {
        string path = Path.GetFullPath(p.Path);
        bool isFolder = Directory.Exists(path);
        string kind = isFolder ? "folder" : "file";

        if (ProjectMutationService.FindOwningProject(path) is not { Length: > 0 } projectPath)
            return new ItemPropertiesResult(path, kind, null, null,
                Reason: "No project in this folder or above it claims this.");

        string projectName = Path.GetFileNameWithoutExtension(projectPath);
        string projectDirectory = Path.GetDirectoryName(projectPath)!;

        if (isFolder)
        {
            string relative = Path.GetRelativePath(projectDirectory, path)
                .Replace('/', '\\')
                .Trim('\\');

            var settings = ReSharperSettings.ForProject(projectPath);

            return new ItemPropertiesResult(
                path, kind, projectPath, projectName,
                Folder: new FolderItemProperties(
                    settings.IsNamespaceProvider(relative),
                    ProjectMutationService.InferNamespace(
                        projectPath, projectDirectory, Path.Combine(path, "File.cs")),
                    relative));
        }

        var evaluation = await ProjectEvaluationService.EvaluateAsync(projectPath, ct);

        if (evaluation is null)
            return new ItemPropertiesResult(path, kind, projectPath, projectName,
                Reason: $"{projectName} could not be evaluated, so its items are unknown.");

        var item = evaluation.Items.FirstOrDefault(candidate =>
            string.Equals(candidate.FullPath, path, StringComparison.OrdinalIgnoreCase));

        // A file no item claims still gets a form: the build action it would be given is the one
        // worth showing, because choosing it is how the file gets into the project at all.
        string itemType = item?.ItemType ?? "";

        return new ItemPropertiesResult(
            path, kind, projectPath, projectName,
            File: new FileItemProperties(
                itemType,
                ItemTypesFor(itemType),
                item?.CopyToOutputDirectory,
                item?.Generator,
                item?.CustomToolNamespace,
                item?.Link,
                item?.DependentUpon,
                item?.FromGlob ?? false,
                item?.DeclaredIn,
                InProject: item is not null,
                SdkStyle: PathHelper.ReadProjectSdk(projectPath) is not null));
    }

    public static async Task<SetItemPropertiesResult> SetAsync(
        SetItemPropertiesParams p, CancellationToken ct)
    {
        string path = Path.GetFullPath(p.Path);

        if (ProjectMutationService.FindOwningProject(path) is not { Length: > 0 } projectPath)
            return new SetItemPropertiesResult(false, "No project claims this path.");

        if (p.NamespaceProvider is { } isProvider && Directory.Exists(path))
        {
            bool written = DotSettingsWriter.SetNamespaceProvider(projectPath, path, isProvider);

            return new SetItemPropertiesResult(
                written,
                written
                    ? isProvider
                        ? $"{Path.GetFileName(path)} contributes its name to namespaces again."
                        : $"{Path.GetFileName(path)} no longer contributes to namespaces."
                    : "The .DotSettings layer could not be written.",
                written ? await GetAsync(new ItemPropertiesParams(path), ct) : null);
        }

        // Null leaves a value alone and empty clears it, and the two have to stay distinct all
        // the way down: a dictionary that dropped the nulls is what tells the writer which.
        var metadata = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (p.CopyToOutputDirectory is not null)
            metadata["CopyToOutputDirectory"] = p.CopyToOutputDirectory;

        if (p.Generator is not null)
            metadata["Generator"] = p.Generator;

        if (p.CustomToolNamespace is not null)
            metadata["CustomToolNamespace"] = p.CustomToolNamespace;

        var result = await ProjectMutationService.SetItemPropertiesAsync(
            projectPath, path, p.ItemType, metadata, ct);

        return new SetItemPropertiesResult(
            result.Ok,
            result.Message,
            result.Ok ? await GetAsync(new ItemPropertiesParams(path), ct) : null);
    }

    /// <summary>
    /// The build actions to offer, with the file's own first when the project invented it.
    /// </summary>
    /// <remarks>
    /// A project may define any item type it likes, and a file that has one must not lose it to
    /// a dropdown that only knows the common seven — so an unknown type joins the list rather
    /// than being replaced by the first entry in it.
    /// </remarks>
    private static string[] ItemTypesFor(string itemType) =>
        itemType.Length == 0
        || ProjectMutationService.FileItemTypes.Contains(itemType, StringComparer.OrdinalIgnoreCase)
            ? ProjectMutationService.FileItemTypes
            : [itemType, .. ProjectMutationService.FileItemTypes];
}
