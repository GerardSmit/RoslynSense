using Microsoft.Language.Xml;

namespace RoslynMCP.Services;

/// <summary>
/// Where a project's <c>project.assets.json</c> is, for the callers that ask before the project
/// has ever been evaluated.
/// </summary>
/// <remarks>
/// <para>
/// NuGet writes the assets file into <c>$(MSBuildProjectExtensionsPath)</c>, which is
/// <c>$(BaseIntermediateOutputPath)</c> — <c>obj\</c> — unless the project says otherwise. A
/// legacy web project that keeps several projects' intermediates apart with
/// <c>&lt;BaseIntermediateOutputPath&gt;obj\$(MSBuildProjectName)\&lt;/BaseIntermediateOutputPath&gt;</c>
/// says otherwise, and every reader that assumed <c>obj\project.assets.json</c> never found the
/// file: the restore check ran an MSBuild restore before every load of it (two to ten seconds
/// each), the evaluation cache stamped a file that did not exist, and the restore watcher watched
/// a directory nothing wrote to.
/// </para>
/// <para>
/// Read from the project XML and the nearest <c>Directory.Build.props</c> rather than from an
/// evaluation, because the callers ask before the first evaluation — deciding whether to restore
/// is what gates it. The default is probed first and the XML only read when it misses, so the
/// ordinary project costs one existence check. Conditions are not evaluated, and a value that
/// uses a property other than the project's own name and directory is treated as undeclared:
/// either mistake costs what it cost before, a restore per load, and never a wrong file.
/// </para>
/// </remarks>
internal static class ProjectAssetsFile
{
    private const string FileName = "project.assets.json";

    /// <summary>The full path of <paramref name="projectPath"/>'s assets file, whether or not it exists.</summary>
    public static string Resolve(string projectPath)
    {
        string full = Path.GetFullPath(projectPath);
        string projectDir = Path.GetDirectoryName(full) ?? "";
        string standard = Path.Combine(projectDir, "obj", FileName);

        if (File.Exists(standard))
            return standard;

        string? declared = PathHelper.FileDerived<string?>.Get(full, DeclaredDirectory);
        return declared is null ? standard : Path.Combine(declared, FileName);
    }

    /// <summary>
    /// The intermediate directory the project or its nearest <c>Directory.Build.props</c>
    /// declares, or null when neither does. Memoized on the project file: the props file is
    /// read on the project's behalf and a change to it alone is picked up with the project's
    /// next edit, which is the trade the callers can afford — a stale answer is a restore that
    /// need not have run.
    /// </summary>
    private static string? DeclaredDirectory(string projectPath)
    {
        string projectDir = Path.GetDirectoryName(projectPath) ?? "";
        string projectName = Path.GetFileNameWithoutExtension(projectPath);

        // The project's own declaration first: a Directory.Build.props is imported by
        // Microsoft.Common.props, which a legacy project imports after its first property group.
        if (ReadDeclared(projectPath, projectDir, projectName, artifactsAllowed: false) is { } own)
            return own;

        string? props = NearestDirectoryBuildProps(projectDir);
        return props is null ? null : ReadDeclared(props, projectDir, projectName, artifactsAllowed: true);
    }

    private static string? NearestDirectoryBuildProps(string projectDir)
    {
        for (var dir = new DirectoryInfo(projectDir); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "Directory.Build.props");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// The intermediate directory <paramref name="file"/> declares, resolved against the
    /// project directory. <c>MSBuildProjectExtensionsPath</c> is what NuGet actually writes to;
    /// <c>BaseIntermediateOutputPath</c> is its default; the artifacts layout
    /// (<c>UseArtifactsOutput</c>) is honoured only from a props file, where the SDK requires it.
    /// </summary>
    private static string? ReadDeclared(string file, string projectDir, string projectName, bool artifactsAllowed)
    {
        string? extensions = null, baseIntermediate = null, artifactsPath = null;
        bool useArtifacts = false;

        try
        {
            var xml = Parser.ParseText(File.ReadAllText(file));

            // Last declaration wins, as it does in an evaluation.
            foreach (var element in xml.Descendants())
            {
                switch (element.NameNode?.LocalName)
                {
                    case "MSBuildProjectExtensionsPath": extensions = element.Value; break;
                    case "BaseIntermediateOutputPath": baseIntermediate = element.Value; break;
                    case "ArtifactsPath": artifactsPath = element.Value; break;
                    case "UseArtifactsOutput":
                        useArtifacts = string.Equals(element.Value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable: nothing declared is the honest answer. Malformed needs no catch — the
            // parse is error-tolerant, and a declaration above the damage still counts.
            return null;
        }

        string fileDir = Path.GetDirectoryName(file) ?? projectDir;

        string? Expand(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            string expanded = value.Trim()
                .Replace("$(MSBuildProjectName)", projectName, StringComparison.OrdinalIgnoreCase)
                .Replace("$(MSBuildProjectDirectory)", projectDir, StringComparison.OrdinalIgnoreCase)
                .Replace("$(MSBuildThisFileDirectory)", fileDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

            // Anything else would need an evaluation to know; the default stands in for it.
            if (expanded.Contains("$(", StringComparison.Ordinal))
                return null;

            try
            {
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(projectDir, expanded)));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }

        if (Expand(extensions) is { } fromExtensions)
            return fromExtensions;
        if (Expand(baseIntermediate) is { } fromBase)
            return fromBase;

        if (artifactsAllowed && useArtifacts)
        {
            // The SDK's layout: <ArtifactsPath>/obj/<project name>/, with ArtifactsPath defaulting
            // to an artifacts directory beside the props file that turned the layout on.
            string root = Expand(artifactsPath) ?? Path.Combine(fileDir, "artifacts");
            return Path.Combine(root, "obj", projectName);
        }

        return null;
    }
}
