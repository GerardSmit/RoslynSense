using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

/// <summary>roslynSense/itemProperties: what the project says about one file or folder.</summary>
public sealed record ItemPropertiesParams(
    [property: JsonPropertyName("path")] string Path);

/// <summary>
/// The answer the Properties panel renders. Exactly one of <see cref="File"/> and
/// <see cref="Folder"/> is set; both are null for a path no project claims.
/// </summary>
/// <param name="Reason">Why there is nothing to show, when there is nothing — no project owns
/// the path, or the project could not be evaluated. Null when the panel has something.</param>
public sealed record ItemPropertiesResult(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("projectPath")] string? ProjectPath,
    [property: JsonPropertyName("projectName")] string? ProjectName,
    [property: JsonPropertyName("file")] FileItemProperties? File = null,
    [property: JsonPropertyName("folder")] FolderItemProperties? Folder = null,
    [property: JsonPropertyName("reason")] string? Reason = null);

/// <summary>
/// One file's MSBuild item, as a form.
/// </summary>
/// <param name="ItemTypes">What the build action may be set to. The file's current type is in
/// the list even when it is one the project invented, so a picker never silently retypes it.
/// </param>
/// <param name="FromGlob">Whether a wildcard claimed the file rather than the project naming it.
/// Shown, because it is the difference between an edit that changes one attribute and one that
/// writes a Remove and an Include — and somebody watching their .csproj grow deserves to know
/// which they asked for.</param>
/// <param name="DeclaredIn">The file whose XML carried the item. For a globbed item that is an
/// SDK targets file, which is the honest answer to "where is this written".</param>
/// <param name="InProject">False for a file on disk that no item claims — the tree already says
/// so, and the panel says why the rest of the form is empty.</param>
public sealed record FileItemProperties(
    [property: JsonPropertyName("itemType")] string ItemType,
    [property: JsonPropertyName("itemTypes")] string[] ItemTypes,
    [property: JsonPropertyName("copyToOutputDirectory")] string? CopyToOutputDirectory,
    [property: JsonPropertyName("generator")] string? Generator,
    [property: JsonPropertyName("customToolNamespace")] string? CustomToolNamespace,
    [property: JsonPropertyName("link")] string? Link,
    [property: JsonPropertyName("dependentUpon")] string? DependentUpon,
    [property: JsonPropertyName("fromGlob")] bool FromGlob,
    [property: JsonPropertyName("declaredIn")] string? DeclaredIn,
    [property: JsonPropertyName("inProject")] bool InProject,
    [property: JsonPropertyName("sdkStyle")] bool SdkStyle);

/// <summary>
/// One folder's properties, which for now is the one setting a folder has.
/// </summary>
/// <param name="Namespace">What a new file in this folder would be given, with the folder's own
/// contribution already applied — the checkbox's effect, stated rather than described.</param>
public sealed record FolderItemProperties(
    [property: JsonPropertyName("namespaceProvider")] bool NamespaceProvider,
    [property: JsonPropertyName("namespace")] string? Namespace,
    [property: JsonPropertyName("relativePath")] string RelativePath);

/// <summary>
/// roslynSense/setItemProperties. Every field is optional: what is null is left alone, which is
/// what lets the panel send one changed control rather than the whole form.
/// </summary>
/// <param name="CopyToOutputDirectory">An empty string clears it; null leaves it.</param>
public sealed record SetItemPropertiesParams(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("itemType")] string? ItemType = null,
    [property: JsonPropertyName("copyToOutputDirectory")] string? CopyToOutputDirectory = null,
    [property: JsonPropertyName("generator")] string? Generator = null,
    [property: JsonPropertyName("customToolNamespace")] string? CustomToolNamespace = null,
    [property: JsonPropertyName("namespaceProvider")] bool? NamespaceProvider = null);

public sealed record SetItemPropertiesResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("properties")] ItemPropertiesResult? Properties = null);
