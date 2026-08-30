using System.Collections.Immutable;

namespace RoslynMCP.Languages.Templates.Core;

/// <summary>
/// One application's templates: the project they belong to, the root their paths are relative to,
/// and the folders they are written in.
/// </summary>
internal readonly record struct TemplateRoot(
    string ProjectPath,
    string ProjectName,
    string ContentRoot,
    ImmutableArray<string> Folders);

/// <summary>
/// Which projects have templates, decided from directory names and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// This answers on the Discovery root listing, which is drawn every time the view becomes visible,
/// so it may not evaluate a project and may not read a file. What it does instead is ask whether
/// the configured folder exists beside each project file — a handful of directory probes for a
/// solution of two hundred, and none of them touches the workspace.
/// </para>
/// <para>
/// The project directory is the content root, because the folder holding the templates lives
/// inside the application that serves them, and a control path is written relative to what the
/// application serves. Nothing else in the file says where the root is, so a layout that puts them
/// apart is handled by <see cref="TemplateSet.Resolve"/> walking up rather than by configuration
/// nobody would know to write.
/// </para>
/// </remarks>
internal static class TemplateRoots
{
    private const string Extension = "*.yml";

    /// <summary>The projects that have at least one of the configured folders.</summary>
    public static ImmutableArray<TemplateRoot> Of(
        IEnumerable<(string Path, string Name)> projects, IReadOnlyList<string> folders)
    {
        if (folders.Count == 0)
            return [];

        var found = ImmutableArray.CreateBuilder<TemplateRoot>();

        foreach (var (path, name) in projects)
        {
            if (Path.GetDirectoryName(Path.GetFullPath(path)) is not { Length: > 0 } directory)
                continue;

            var present = ImmutableArray.CreateBuilder<string>(folders.Count);

            foreach (string folder in folders)
            {
                string candidate = Path.Combine(directory, folder.Replace('/', Path.DirectorySeparatorChar));

                // Either spelling: a folder of files, or one file named outright. Both occur —
                // an application that started with one template file and grew a folder beside it
                // keeps reading both, and so does this.
                if (Directory.Exists(candidate) || File.Exists(candidate))
                    present.Add(candidate);
            }

            if (present.Count > 0)
                found.Add(new TemplateRoot(path, name, directory, present.ToImmutable()));
        }

        return found.ToImmutable();
    }

    /// <summary>
    /// The files of one root, in the order the application reads them.
    /// </summary>
    /// <remarks>
    /// The leading number first, then the name. A folder like this is ordered by a numeric prefix
    /// precisely because the order is load-bearing — the file that introduces an entry decides its
    /// name and its parent, and the ones after it add — so listing them by file name alone would
    /// put <c>100-</c> before <c>2-</c> and attribute a declaration to the wrong file.
    /// </remarks>
    public static ImmutableArray<string> Files(TemplateRoot root)
    {
        var files = new List<string>();

        foreach (string folder in root.Folders)
        {
            try
            {
                if (File.Exists(folder))
                    files.Add(folder);
                else
                    files.AddRange(Directory.EnumerateFiles(folder, Extension, SearchOption.AllDirectories));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return
        [
            .. files
                .OrderBy(file => Sequence(file))
                .ThenBy(file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>The number a file name starts with, or 0 when it starts with none.</summary>
    private static int Sequence(string file)
    {
        string name = Path.GetFileName(file);
        int end = 0;

        while (end < name.Length && char.IsAsciiDigit(name[end]))
            end++;

        return end > 0 && int.TryParse(name[..end], out int number) ? number : 0;
    }
}
