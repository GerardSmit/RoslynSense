using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace RoslynMCP.Languages.Templates.Core;

/// <summary>
/// The merged templates of each root, parsed once and kept until a file changes.
/// </summary>
/// <remarks>
/// <para>
/// Reading a folder of a couple of hundred files is not free, and every expand in the tree asks
/// the same question: what is under this entry. So the answer is the whole merged set rather than
/// one entry's worth — the merge cannot be done for part of a folder anyway, since the file that
/// declares an entry is rarely the one that adds a module to it.
/// </para>
/// <para>
/// Keyed on the newest write across the files that were enumerated, plus how many there were. The
/// count is what catches a deletion: removing a file leaves every remaining timestamp where it
/// was, so a stamp alone would serve the deleted file's declarations until something else was
/// edited.
/// </para>
/// </remarks>
internal sealed class TemplateIndex(ImmutableArray<string> controlFolders)
{
    private readonly ConcurrentDictionary<string, (long Stamp, int Files, TemplateSet Set)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Everything one root declares.</summary>
    public TemplateSet Of(TemplateRoot root, CancellationToken ct)
    {
        var files = TemplateRoots.Files(root);
        long stamp = Newest(files);

        if (_cache.TryGetValue(root.ContentRoot, out var cached)
            && cached.Stamp == stamp
            && cached.Files == files.Length)
        {
            return cached.Set;
        }

        var set = TemplateSet.Build(root.ContentRoot, Documents(files, ct), controlFolders);
        _cache[root.ContentRoot] = (stamp, files.Length, set);
        return set;
    }

    private static IEnumerable<TemplateDocument> Documents(
        ImmutableArray<string> files, CancellationToken ct)
    {
        foreach (string file in files)
        {
            ct.ThrowIfCancellationRequested();

            var (text, error) = Read(file);

            yield return text is null
                ? new TemplateDocument(file, [], [], error)
                : TemplateYaml.Read(file, text);
        }
    }

    /// <summary>The file's text, or why it could not be had.</summary>
    /// <remarks>
    /// Split out because a <c>yield</c> cannot live in a catch block, and because a file that
    /// cannot be read is the same kind of answer as one that cannot be parsed — a line in the log
    /// and the rest of the folder still listed.
    /// </remarks>
    private static (string? Text, string? Error) Read(string file)
    {
        try
        {
            return (File.ReadAllText(file), null);
        }
        catch (IOException ex)
        {
            return (null, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return (null, ex.Message);
        }
    }

    private static long Newest(ImmutableArray<string> files)
    {
        long newest = 0;

        foreach (string file in files)
        {
            try
            {
                long written = File.GetLastWriteTimeUtc(file).Ticks;

                if (written > newest)
                    newest = written;
            }
            catch (IOException)
            {
            }
        }

        return newest;
    }
}
