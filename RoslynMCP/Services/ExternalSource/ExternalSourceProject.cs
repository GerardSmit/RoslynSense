using System.Text.Json;
using Microsoft.CodeAnalysis;

namespace RoslynMCP.Services.ExternalSource;

/// <summary>
/// What turns a fetched file into something the language features can answer about.
/// </summary>
/// <remarks>
/// <para>
/// A file under one of the cache roots belongs to no project, and everything above the workspace
/// starts by asking which project owns the file — so without this, opening real framework source
/// gave an inert buffer: no hover, no F12, no completion, only the grammar's colours. Decompiled
/// output never had that problem because it has always been written with a manifest beside it that
/// stands in for a project.
/// </para>
/// <para>
/// The manifest here is a sidecar named after its file rather than a fixed name in the directory,
/// because these caches are keyed by origin: one reference-source directory holds every type that
/// happened to live in the same folder of the repository, and each of them is its own project.
/// </para>
/// </remarks>
internal static class ExternalSourceProject
{
    /// <summary>Appended to the source file's own name: <c>webclient.cs.roslynsense.json</c>.</summary>
    internal const string ManifestSuffix = ".roslynsense.json";

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    /// <summary>Whether a path names a project that only this service can open.</summary>
    public static bool IsProjectPath(string? path) =>
        path is { Length: > 0 }
        && (path.EndsWith(ManifestSuffix, StringComparison.OrdinalIgnoreCase)
            || DecompiledSourceService.IsGeneratedProjectPath(path));

    /// <summary>The stand-in project for a file outside the solution, if it has one.</summary>
    public static string? TryGetProjectPath(string filePath)
    {
        if (filePath is not { Length: > 0 })
            return null;

        // Asked on every document resolve, so the cheap test that rules out the whole solution
        // comes first and no disk is touched for a file the user owns.
        if (ExternalSourceCache.IsExternalSourcePath(filePath))
        {
            string sidecar = filePath + ManifestSuffix;
            if (File.Exists(sidecar))
                return sidecar;
        }

        return DecompiledSourceService.TryGetGeneratedProjectPath(filePath);
    }

    /// <summary>Opens the ad-hoc project a stand-in project path describes.</summary>
    public static async Task<(Workspace Workspace, Project Project, string? TempDir)> OpenAsync(
        string projectPath, CancellationToken ct = default)
    {
        if (DecompiledSourceService.IsGeneratedProjectPath(projectPath))
            return await DecompiledSourceService.OpenProjectAsync(projectPath, ct);

        var manifest = Read(projectPath)
            ?? throw new InvalidOperationException(
                $"External source manifest '{projectPath}' could not be read.");

        return await DecompiledSourceService.OpenSingleFileProjectAsync(
            manifest.AssemblyPath, manifest.SourceFilePath, ProjectName(manifest), ct);
    }

    /// <summary>
    /// Writes the sidecar for a fetched file, so the next request that lands in it finds a project.
    /// </summary>
    /// <remarks>
    /// Rewritten whenever the type differs from what the sidecar records. One file can declare
    /// several types, and the record only decides what the project is called — but a project named
    /// after the type someone last navigated to is the more useful of the two.
    /// </remarks>
    public static void Ensure(ExternalSourceResult result, string reflectionTypeName)
    {
        if (result.Kind == ExternalSourceKind.Decompiled)
            return;

        string path = result.FilePath + ManifestSuffix;
        var manifest = new ExternalSourceManifest
        {
            AssemblyPath = result.AssemblyPath,
            SourceFilePath = result.FilePath,
            TypeReflectionName = reflectionTypeName,
            Kind = result.Kind.ToString(),
            Origin = result.Origin,
        };

        if (Read(path) is { } existing
            && existing.AssemblyPath == manifest.AssemblyPath
            && existing.TypeReflectionName == manifest.TypeReflectionName)
        {
            return;
        }

        ExternalSourceCache.WriteReadOnly(path, JsonSerializer.SerializeToUtf8Bytes(manifest, s_json));
    }

    /// <summary>What the sidecar records about a fetched file: which assembly it came from and
    /// which type someone navigated to in it. Null when the file has no readable sidecar.</summary>
    internal static (string AssemblyPath, string TypeReflectionName)? TryReadSidecar(string filePath)
    {
        var manifest = Read(filePath + ManifestSuffix);
        return manifest is null ? null : (manifest.AssemblyPath, manifest.TypeReflectionName);
    }

    private static ExternalSourceManifest? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            var manifest = JsonSerializer.Deserialize<ExternalSourceManifest>(File.ReadAllBytes(path));

            return manifest is { AssemblyPath.Length: > 0, SourceFilePath.Length: > 0 }
                   && File.Exists(manifest.SourceFilePath)
                ? manifest
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ProjectName(ExternalSourceManifest manifest) =>
        $"{Path.GetFileNameWithoutExtension(manifest.AssemblyPath)}."
        + manifest.TypeReflectionName.Replace('+', '.');

    private sealed class ExternalSourceManifest
    {
        public string AssemblyPath { get; set; } = "";

        public string SourceFilePath { get; set; } = "";

        public string TypeReflectionName { get; set; } = "";

        /// <summary>Recorded for diagnosis; the project does not depend on it.</summary>
        public string Kind { get; set; } = "";

        public string? Origin { get; set; }
    }
}
