using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.DotSettings.Core;
using RoslynMCP.Services;

namespace RoslynMCP.Lsp.Search;

/// <summary>One declaration, reduced to what a search box ranks it by.</summary>
public sealed record NameDeclaration(
    string Name,
    string Container,
    DeclaredSymbolInfoKind Kind,
    int Line,
    int Character,
    int EndLine,
    int EndCharacter);

/// <summary>One C# file's declarations, plus what says whether they are still current.</summary>
public sealed record NameSource(
    string Path,
    long Length,
    long ModifiedUtcTicks,
    IReadOnlyList<NameDeclaration> Declarations);

/// <summary>Every name in the solution, without the solution.</summary>
public sealed record NameIndexSnapshot(
    IReadOnlyList<string> Files,
    IReadOnlyList<NameSource> Sources);

/// <summary>
/// The names Search Everywhere matches, read straight off disk — no project evaluation, no
/// reference graph, no compilation.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of what the first Ctrl+T after opening a solution used to cost. The search
/// itself was never slow; it was parked behind <see cref="SolutionWarmup"/> evaluating every
/// project through MSBuild, which is seconds of work whose answer the search does not need. Every
/// row Ctrl+T shows is a name: a type, a member, or a file. Names come from parsing, and parsing
/// needs a file and nothing else — not the reference graph, not the target framework, not a
/// compilation. So the whole corpus can be built while MSBuild is still starting, and the search
/// can answer out of it in the meantime.
/// </para>
/// <para>
/// Declarations are extracted through Roslyn's own <see cref="TopLevelSyntaxTreeIndex"/> over an
/// <see cref="AdhocWorkspace"/> rather than by a hand-written walk of the syntax tree, so the
/// corpus here holds exactly what the loaded solution's corpus holds — same names, same container
/// strings, same kinds. A second extractor that agreed with the first only most of the time would
/// show up as results that shuffle when the load lands, which is worse than the wait it replaces.
/// </para>
/// <para>
/// Persisted per solution and keyed per file on length and write time, so the ordinary case — open
/// a solution you were working in yesterday — is a single sequential read of a few megabytes rather
/// than a re-parse of every file. Only the files that actually changed are parsed again. A cache
/// that cannot be read or written is not an error anywhere: it degrades to the parse it was meant
/// to save.
/// </para>
/// <para>
/// Deliberately not kept up to date after it is built. It is a stand-in with a lifetime of a few
/// seconds — from the editor connecting to the solution finishing its load — and once
/// <see cref="SolutionWarmup"/> is done every search goes back to the workspace, which the editor
/// keeps current through <c>didChange</c>. Watching files to maintain a corpus nobody will read
/// again this session would be a subsystem bought for nothing.
/// </para>
/// </remarks>
public static class NameIndex
{
    private static readonly object s_gate = new();
    private static string? s_solutionPath;
    private static Task<NameIndexSnapshot?> s_build = Task.FromResult<NameIndexSnapshot?>(null);

    /// <summary>
    /// Starts building the index for <paramref name="solutionPath"/> if it is not already being
    /// built, and returns the task that carries it. Never throws: a solution whose names cannot be
    /// read early is one that waits for its load, exactly as it did before.
    /// </summary>
    public static Task<NameIndexSnapshot?> Start(string solutionPath)
    {
        lock (s_gate)
        {
            if (s_solutionPath is not null
                && string.Equals(s_solutionPath, solutionPath, StringComparison.OrdinalIgnoreCase))
            {
                return s_build;
            }

            s_solutionPath = solutionPath;
            s_build = Task.Run(() => BuildAsync(solutionPath));
            return s_build;
        }
    }

    /// <summary>
    /// The index, if it becomes ready before <paramref name="loaded"/> completes; null if the
    /// solution finished loading first.
    /// </summary>
    /// <remarks>
    /// The race is the point. This index is worth answering from only while the real corpus is
    /// missing — the moment the solution is loaded it is the better answer, and one that is
    /// certain to be current. On a small solution, or a warm daemon, the load wins this race and
    /// the caller never sees a provisional result at all.
    /// </remarks>
    public static async Task<NameIndexSnapshot?> ReadyBeforeAsync(Task loaded, CancellationToken ct)
    {
        // Checked first and checked again below: an index that is ready is not a reason to prefer
        // it. Once the solution is loaded this corpus is both the poorer answer and a potentially
        // stale one — it stopped being maintained the moment it was built.
        if (loaded.IsCompleted)
            return null;

        Task<NameIndexSnapshot?> build;
        lock (s_gate)
            build = s_build;

        if (!build.IsCompleted)
        {
            var winner = await Task.WhenAny(build, loaded).WaitAsync(ct);
            if (winner != build)
                return null;
        }

        return loaded.IsCompleted ? null : await build;
    }

    /// <summary>
    /// Drops the built index, keeping the solution it was built for so nothing rebuilds it.
    /// </summary>
    /// <remarks>
    /// Called when the solution finishes loading, which is when this stops being an answer anybody
    /// should be given. Distinct from <see cref="Reset"/>, which forgets the solution too: a second
    /// editor window connecting after the load must not set the whole parse going again for a
    /// corpus that would be retired the moment it existed.
    /// </remarks>
    public static void Retire()
    {
        lock (s_gate)
            s_build = Task.FromResult<NameIndexSnapshot?>(null);
    }

    /// <summary>Test seam: forgets the current index, so the next start rebuilds it.</summary>
    internal static void Reset()
    {
        lock (s_gate)
        {
            s_solutionPath = null;
            s_build = Task.FromResult<NameIndexSnapshot?>(null);
        }
    }

    /// <summary>Test seam: builds one without the static gate, so a test owns its own index.</summary>
    internal static Task<NameIndexSnapshot?> BuildForTestAsync(string solutionPath, bool persist = false) =>
        BuildAsync(solutionPath, persist);

    private static async Task<NameIndexSnapshot?> BuildAsync(string solutionPath, bool persist = true)
    {
        try
        {
            var started = Stopwatch.GetTimestamp();

            var files = Walk(solutionPath);
            if (files.Count == 0)
                return null;

            var sources = files
                .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var cached = persist ? NameIndexStore.TryRead(solutionPath) : null;
            var (current, stale) = Partition(sources, cached);

            if (stale.Count > 0)
                current.AddRange(await ParseAsync(stale, CancellationToken.None));

            var snapshot = new NameIndexSnapshot(files, current);

            if (persist && stale.Count > 0)
                NameIndexStore.TryWrite(solutionPath, current);

            ServiceLog.Info(
                $"Name index for {Path.GetFileNameWithoutExtension(solutionPath)} ready in " +
                $"{Stopwatch.GetElapsedTime(started).TotalSeconds:0.00}s: {current.Count} files " +
                $"({stale.Count} parsed, {current.Count - stale.Count} from cache), " +
                $"{files.Count} files searchable by name.");

            return snapshot;
        }
        catch (Exception ex)
        {
            // Nothing downstream requires this — without it a search waits for the load, which is
            // the behaviour it had before the index existed.
            ServiceLog.Warn(
                $"Could not index the names in '{Path.GetFileName(solutionPath)}' ahead of its load: " +
                $"{ex.Message}. The first search will wait for the solution instead.",
                key: $"name-index:{solutionPath}");
            return null;
        }
    }

    /// <summary>
    /// Which cached entries still describe the file on disk, and which files have to be parsed.
    /// </summary>
    /// <remarks>
    /// Length and last-write time rather than a content hash: hashing every file is reading every
    /// file, which is most of what parsing them costs. The failure mode of the cheap key — an edit
    /// that preserves both — is a stale row in a corpus that is replaced by the real one seconds
    /// later anyway.
    /// </remarks>
    private static (List<NameSource> Current, List<string> Stale) Partition(
        IReadOnlyList<string> sources, IReadOnlyDictionary<string, NameSource>? cached)
    {
        var current = new List<NameSource>(sources.Count);
        var stale = new List<string>();

        foreach (string path in sources)
        {
            FileInfo info;
            try
            {
                info = new FileInfo(path);
                if (!info.Exists)
                    continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (cached is not null
                && cached.TryGetValue(path, out var entry)
                && entry.Length == info.Length
                && entry.ModifiedUtcTicks == info.LastWriteTimeUtc.Ticks)
            {
                current.Add(entry);
                continue;
            }

            stale.Add(path);
        }

        return (current, stale);
    }

    /// <summary>
    /// The declarations in <paramref name="paths"/>, through Roslyn's index over a workspace that
    /// exists only to hold the files.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ad-hoc project has no references and is never compiled — <see cref="TopLevelSyntaxTreeIndex"/>
    /// is derived from one document's syntax and asks for neither. Preview language version so the
    /// newest syntax in a file parses rather than erroring out mid-file, and no documentation mode
    /// because doc comments carry no declarations and parsing them is real time over a whole
    /// solution.
    /// </para>
    /// <para>
    /// Its <see cref="Solution.FilePath"/> is deliberately left unset. Setting it to the real
    /// <c>.sln</c> would point Roslyn's persistent index storage at the database the loaded
    /// workspace uses, and SQLite holds that exclusively: whichever opened first would keep it and
    /// the other would silently degrade to no storage at all. This index does its own persistence
    /// (see <see cref="NameIndexStore"/>) precisely so it never has to touch that one.
    /// </para>
    /// </remarks>
    private static async Task<List<NameSource>> ParseAsync(
        IReadOnlyList<string> paths, CancellationToken ct)
    {
        using var workspace = new AdhocWorkspace();

        var projectId = ProjectId.CreateNewId("names");
        var documents = paths
            .Select(path => DocumentInfo.Create(
                DocumentId.CreateNewId(projectId),
                Path.GetFileName(path),
                loader: new FileTextLoader(path, Encoding.UTF8),
                filePath: path))
            .ToList();

        var project = ProjectInfo.Create(
            projectId,
            VersionStamp.Default,
            name: "names",
            assemblyName: "names",
            language: LanguageNames.CSharp,
            parseOptions: new CSharpParseOptions(
                LanguageVersion.Preview, DocumentationMode.None),
            documents: documents);

        var solution = workspace.AddProject(project).Solution;

        var found = new ConcurrentBag<NameSource>();

        await Parallel.ForEachAsync(
            solution.GetProject(projectId)!.Documents,
            new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = Environment.ProcessorCount,
            },
            async (document, token) =>
            {
                if (await ExtractAsync(document, token) is { } source)
                    found.Add(source);
            });

        return [.. found];
    }

    private static async Task<NameSource?> ExtractAsync(Document document, CancellationToken ct)
    {
        string path = document.FilePath!;

        try
        {
            var index = await TopLevelSyntaxTreeIndex.GetIndexAsync(document, ct).ConfigureAwait(false);
            if (index is null)
                return null;

            var text = await document.GetTextAsync(ct).ConfigureAwait(false);

            var declarations = new List<NameDeclaration>(index.DeclaredSymbolInfos.Length);
            foreach (var info in index.DeclaredSymbolInfos)
            {
                if (info.Span.End > text.Length)
                    continue;

                var span = text.Lines.GetLinePositionSpan(info.Span);
                declarations.Add(new NameDeclaration(
                    info.Name,
                    info.FullyQualifiedContainerName,
                    info.Kind,
                    span.Start.Line,
                    span.Start.Character,
                    span.End.Line,
                    span.End.Character));
            }

            var file = new FileInfo(path);
            return new NameSource(path, file.Length, file.LastWriteTimeUtc.Ticks, declarations);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A file that will not read or will not parse is one file missing from an index that
            // is itself a stand-in. Never the whole build.
            return null;
        }
    }

    /// <summary>
    /// Every file under the solution's own folder and its projects' folders, read from the
    /// <c>.sln</c> on disk.
    /// </summary>
    /// <remarks>
    /// The roots come from the solution file rather than from
    /// <see cref="SolutionFileIndex.FilesAsync"/>'s <see cref="Solution"/>, for the obvious reason:
    /// at the moment this runs there is no <see cref="Solution"/>. The walk itself is shared, so
    /// the exclusions — build output, tooling folders, whatever a <c>.DotSettings</c> layer adds —
    /// are the ones every other search already obeys.
    /// </remarks>
    private static IReadOnlyList<string> Walk(string solutionPath)
    {
        var roots = new List<string>();

        if (Path.GetDirectoryName(Path.GetFullPath(solutionPath)) is { Length: > 0 } solutionDirectory)
            roots.Add(solutionDirectory);

        foreach (string project in PathHelper.GetProjectsFromSolution(solutionPath))
        {
            if (Path.GetDirectoryName(Path.GetFullPath(project)) is not { Length: > 0 } directory)
                continue;

            // A project inside the solution folder is already covered by the walk above; walking
            // it again would be one extra pass over the same files per project in the repo.
            if (!roots.Any(root => IsUnderOrEqual(directory, root)))
                roots.Add(directory);
        }

        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string root in roots)
        {
            foreach (string file in SolutionFileIndex.FilesUnder(root, CancellationToken.None))
            {
                if (SearchFileRules.IsExcluded(file) || DotSettingsExclusions.IsExcluded(file))
                    continue;

                if (seen.Add(file))
                    files.Add(file);
            }
        }

        return files;
    }

    private static bool IsUnderOrEqual(string path, string root) =>
        string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(
            root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The on-disk copy of a <see cref="NameIndex"/>, so a solution opened twice is parsed once.
/// </summary>
/// <remarks>
/// <para>
/// A private format rather than Roslyn's persistent storage, which the loaded workspace owns
/// exclusively — see the remarks on <c>NameIndex.ParseAsync</c>. Binary rather than JSON because
/// this is read on the path it exists to make fast: a few hundred thousand short strings through a
/// JSON reader is most of the parse it saves.
/// </para>
/// <para>
/// Beside the other caches under LocalApplicationData and keyed the same way — on the solution path
/// alone, so an upgraded daemon reads what its predecessor wrote. A version stamp in the header
/// retires the file when the format moves; there is nothing to migrate, since everything in it can
/// be derived again from the files it describes.
/// </para>
/// </remarks>
internal static class NameIndexStore
{
    private const uint Magic = 0x494E5352; // "RSNI"
    private const int Version = 1;

    public static IReadOnlyDictionary<string, NameSource>? TryRead(string solutionPath)
    {
        try
        {
            string file = PathFor(solutionPath);
            if (!File.Exists(file))
                return null;

            using var stream = File.OpenRead(file);
            using var reader = new BinaryReader(stream, Encoding.UTF8);

            if (reader.ReadUInt32() != Magic || reader.ReadInt32() != Version)
                return null;

            int count = reader.ReadInt32();
            var sources = new Dictionary<string, NameSource>(count, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < count; i++)
            {
                string path = reader.ReadString();
                long length = reader.ReadInt64();
                long ticks = reader.ReadInt64();

                int declarationCount = reader.ReadInt32();
                var declarations = new List<NameDeclaration>(declarationCount);
                for (int d = 0; d < declarationCount; d++)
                {
                    declarations.Add(new NameDeclaration(
                        reader.ReadString(),
                        reader.ReadString(),
                        (DeclaredSymbolInfoKind)reader.ReadByte(),
                        reader.ReadInt32(),
                        reader.ReadInt32(),
                        reader.ReadInt32(),
                        reader.ReadInt32()));
                }

                sources[path] = new NameSource(path, length, ticks, declarations);
            }

            return sources;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or EndOfStreamException or InvalidDataException)
        {
            // A truncated or half-written cache is a cache miss, never a failure: everything in it
            // is derivable from the files it describes.
            return null;
        }
    }

    public static void TryWrite(string solutionPath, IReadOnlyList<NameSource> sources)
    {
        string file = PathFor(solutionPath);
        string temporary = file + ".tmp";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);

            using (var stream = File.Create(temporary))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(sources.Count);

                foreach (var source in sources)
                {
                    writer.Write(source.Path);
                    writer.Write(source.Length);
                    writer.Write(source.ModifiedUtcTicks);
                    writer.Write(source.Declarations.Count);

                    foreach (var declaration in source.Declarations)
                    {
                        writer.Write(declaration.Name);
                        writer.Write(declaration.Container);
                        writer.Write((byte)declaration.Kind);
                        writer.Write(declaration.Line);
                        writer.Write(declaration.Character);
                        writer.Write(declaration.EndLine);
                        writer.Write(declaration.EndCharacter);
                    }
                }
            }

            // Written aside and moved, so a daemon killed mid-write leaves the previous index
            // rather than a half-file the next start has to detect and discard.
            File.Move(temporary, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); } catch { }
        }
    }

    internal static string PathFor(string solutionPath)
    {
        byte[] hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(solutionPath).ToLowerInvariant()));

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RoslynSense",
            "name-index",
            Convert.ToHexString(hash.AsSpan(0, 8)) + ".bin");
    }
}
