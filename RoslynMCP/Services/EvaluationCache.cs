using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynMCP.Services;

/// <summary>
/// Persists what MSBuild evaluation produced for a project — the <see cref="ProjectFileInfo"/>s
/// the BuildHost returned — so the next process can load the project without evaluating it at all.
/// </summary>
/// <remarks>
/// <para>
/// This cache exists because evaluation is where solution loading spends its time, and none of it
/// survived a restart. Measured on an 80-project solution: 95% of a 53-second cold open and a
/// 29-second warm open was MSBuild evaluating projects in BuildHost subprocesses; the "warm" half
/// of that was only the operating system's file cache. Every editor restart after the daemon's
/// idle timeout paid the whole price again, which is the "wait a minute before I can navigate"
/// experience this removes.
/// </para>
/// <para>
/// What is stored is the BuildHost wire contract itself
/// (<c>Microsoft.CodeAnalysis.MSBuild.ProjectFileInfo</c> and its parts) — plain data records
/// designed to cross a JSON RPC boundary, so they round-trip through JSON here by construction.
/// Serving one from disk is indistinguishable, to everything downstream, from the BuildHost
/// having answered.
/// </para>
/// <para>
/// Validity is a fingerprint over every input that can change what evaluation would produce:
/// the project file, the ancestor <c>Directory.Build.props/targets</c> chain, the restore graph
/// (<c>project.assets.json</c>), the <em>names</em> of the source-shaped files under the project
/// directory (a file added or removed changes the evaluated document list without touching the
/// project file), the global MSBuild properties the evaluation ran under, and the versions of
/// this tool and of the contract assembly. Contents of source files are deliberately not part of
/// it: editing a file changes no item list, and hashing every source in a large solution would
/// cost a noticeable slice of what the cache saves.
/// </para>
/// <para>
/// <c>ROSLYNMCP_NO_EVAL_CACHE=1</c> turns it off; <c>ROSLYNMCP_EVAL_CACHE_DIR</c> relocates it
/// (the test suite points it at a sandbox so parallel testhosts and the user's live daemon never
/// share entries).
/// </para>
/// </remarks>
internal static class EvaluationCache
{
    /// <summary>Bumped when the entry shape or fingerprint recipe changes.</summary>
    private const int FormatVersion = 1;

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("ROSLYNMCP_NO_EVAL_CACHE") is not ("1" or "true" or "on");

    private static string Root =>
        Environment.GetEnvironmentVariable("ROSLYNMCP_EVAL_CACHE_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RoslynSense", "eval-cache");

    /// <summary>Entries served without a BuildHost this process lifetime. Diagnostic only.</summary>
    internal static int HitCount;

    /// <summary>Entries written after a genuine evaluation this process lifetime. Diagnostic only.</summary>
    internal static int StoreCount;

    private sealed record Entry(
        int Version,
        string Fingerprint,
        ImmutableArray<ProjectFileInfo> Infos,
        ImmutableArray<string> OutputPaths);

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = false };

    /// <summary>
    /// The cached evaluation of <paramref name="projectPath"/>, when every input it was computed
    /// from is unchanged. False on any doubt: a miss costs one ordinary evaluation.
    /// </summary>
    /// <param name="fingerprint">
    /// The current fingerprint when the caller has already computed it. Fingerprinting reads the
    /// project file, the restore assets and a directory walk, and one load asks about the same
    /// path up to three times (probe, post-miss re-check, store) — computed fresh each time it
    /// was a measurable slice of the loading it exists to avoid.
    /// </param>
    public static bool TryGet(
        string projectPath,
        ImmutableDictionary<string, string> properties,
        out ImmutableArray<ProjectFileInfo> infos,
        out ImmutableArray<string> outputPaths,
        string? fingerprint = null)
    {
        infos = default;
        outputPaths = default;

        if (!Enabled)
            return false;

        try
        {
            string file = EntryPath(projectPath, properties);
            if (!File.Exists(file))
                return false;

            var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllBytes(file), s_json);
            if (entry is null
                || entry.Version != FormatVersion
                || entry.Infos.IsDefault
                || entry.Fingerprint != (fingerprint ?? Fingerprint(projectPath, properties)))
            {
                return false;
            }

            infos = entry.Infos;
            outputPaths = entry.OutputPaths.IsDefault ? [] : entry.OutputPaths;
            Interlocked.Increment(ref HitCount);
            return true;
        }
        catch (Exception)
        {
            // A torn write, a JSON shape from a different tool version, a file lock — every one of
            // them means "evaluate normally", never "fail the load".
            return false;
        }
    }

    /// <summary>
    /// Records what evaluating <paramref name="projectPath"/> produced. Asynchronous and
    /// contained: the load that just paid for the evaluation must not also wait on the disk.
    /// </summary>
    public static void Store(
        string projectPath,
        ImmutableDictionary<string, string> properties,
        ImmutableArray<ProjectFileInfo> infos,
        ImmutableArray<string> outputPaths,
        string? fingerprint = null)
    {
        if (!Enabled || infos.IsDefaultOrEmpty)
            return;

        var write = Task.Run(() =>
        {
            try
            {
                var entry = new Entry(
                    FormatVersion, fingerprint ?? Fingerprint(projectPath, properties),
                    infos, outputPaths);

                string file = EntryPath(projectPath, properties);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);

                // Atomic replace, so a reader never sees half an entry. The temp name carries a
                // counter as well as the pid: two loads in one process can evaluate the same
                // project concurrently (a seed and a prewarm), and both stores are welcome —
                // last write wins with identical content — as long as they stop sharing a file.
                string temp = file + "." + Environment.ProcessId + "."
                    + Interlocked.Increment(ref s_tempCounter) + ".tmp";
                File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(entry, s_json));
                File.Move(temp, file, overwrite: true);
                Interlocked.Increment(ref StoreCount);
            }
            catch (Exception ex)
            {
                ServiceLog.Warn(
                    $"Could not cache the evaluation of '{Path.GetFileName(projectPath)}': {ex.Message}",
                    key: $"eval-cache:{projectPath}");
            }
        });

        s_pendingStores.TryAdd(write, 0);
        write.ContinueWith(t => s_pendingStores.TryRemove(t, out _),
            TaskContinuationOptions.ExecuteSynchronously);
    }

    private static readonly ConcurrentDictionary<Task, byte> s_pendingStores = new();

    private static int s_tempCounter;

    /// <summary>
    /// Completes when every store issued so far has hit the disk. For tests that reload right
    /// after a load, and for short-lived processes whose exit would otherwise race the last
    /// few writes.
    /// </summary>
    public static Task WhenStoresIdleAsync() => Task.WhenAll(s_pendingStores.Keys.ToArray());

    private static string EntryPath(string projectPath, ImmutableDictionary<string, string> properties)
    {
        string key = Path.GetFullPath(projectPath).ToLowerInvariant()
            + "|" + PropertiesKey(properties);

        string name = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 12));

        return Path.Combine(Root, name + ".json");
    }

    private static string PropertiesKey(ImmutableDictionary<string, string> properties) =>
        string.Join(";", properties.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{p.Key}={p.Value}"));

    /// <summary>
    /// One hash over every input that can change what evaluation would produce. Section labels
    /// keep "file absent" distinguishable from "file empty" and one section's data out of the
    /// next one's.
    /// </summary>
    internal static string Fingerprint(
        string projectPath, ImmutableDictionary<string, string> properties)
    {
        string full = Path.GetFullPath(projectPath);
        string? projectDir = Path.GetDirectoryName(full);

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        void AddText(string text)
        {
            sha.AppendData(Encoding.UTF8.GetBytes(text));
            sha.AppendData([0]);
        }

        void AddFile(string label, string path)
        {
            AddText(label);
            try
            {
                sha.AppendData(File.ReadAllBytes(path));
            }
            catch (Exception)
            {
                AddText("<absent>");
            }
            sha.AppendData([1]);
        }

        AddText($"v{FormatVersion}");
        AddText(typeof(EvaluationCache).Assembly.GetName().Version?.ToString() ?? "");
        AddText(typeof(ProjectFileInfo).Assembly.GetName().Version?.ToString() ?? "");
        AddText(PropertiesKey(properties));

        AddFile("project", full);

        if (projectDir is not null)
        {
            // Every Directory.Build.props/targets from the project directory up: any of them can
            // inject items, properties or analyzers into this project's evaluation.
            for (var dir = new DirectoryInfo(projectDir); dir is not null; dir = dir.Parent)
            {
                AddFile("props", Path.Combine(dir.FullName, "Directory.Build.props"));
                AddFile("targets", Path.Combine(dir.FullName, "Directory.Build.targets"));
            }

            AddFile("assets", Path.Combine(projectDir, "obj", "project.assets.json"));

            AddText("files");
            foreach (string relative in SourceShapedFiles(projectDir))
                AddText(relative);
        }

        return Convert.ToHexString(sha.GetHashAndReset());
    }

    /// <summary>
    /// Extensions whose presence changes an evaluated item list — sources, markup, resources,
    /// analyzer configs, and loose MSBuild files a glob might import.
    /// </summary>
    private static readonly HashSet<string> s_sourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".vb", ".razor", ".cshtml", ".aspx", ".ascx", ".master",
        ".resx", ".editorconfig", ".globalconfig", ".props", ".targets",
    };

    /// <summary>
    /// The names (not contents) of the source-shaped files under <paramref name="projectDir"/>,
    /// as sorted relative paths, skipping the directories evaluation itself skips.
    /// </summary>
    private static IEnumerable<string> SourceShapedFiles(string projectDir)
    {
        var names = new List<string>();
        var pending = new Stack<string>();
        pending.Push(projectDir);

        while (pending.Count > 0)
        {
            string dir = pending.Pop();

            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir); }
            catch (Exception) { continue; }

            foreach (string entry in entries)
            {
                string name = Path.GetFileName(entry);

                if (Directory.Exists(entry))
                {
                    if (!name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                        && !name.Equals("obj", StringComparison.OrdinalIgnoreCase)
                        && !name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
                        && !name.StartsWith('.'))
                    {
                        pending.Push(entry);
                    }
                }
                else if (s_sourceExtensions.Contains(Path.GetExtension(name)))
                {
                    names.Add(entry[(projectDir.Length + 1)..]);
                }
            }
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>Forgets everything, for tests that need a cold start.</summary>
    internal static void ClearForTests()
    {
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch (Exception)
        {
            // A locked entry just means the next fingerprint check decides, same as always.
        }

        HitCount = 0;
        StoreCount = 0;
    }
}
