using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynMCP.Services;
using Lexer = WebFormsCore.Language.Lexer;
using Parser = WebFormsCore.Language.Parser;
using TokenType = WebFormsCore.Models.TokenType;

namespace RoslynMCP.Languages.WebForms.Core;

/// <summary>
/// Who includes whom, across a project's markup: the server-side
/// <c>&lt;!--#include file="..." --&gt;</c> edges, scanned from text without a compilation.
/// </summary>
/// <remarks>
/// <para>
/// This exists because an included fragment must not be analyzed standalone. A skin's
/// <c>footer.ascx</c> is written to run inline in the page that includes it — the page's
/// <c>@Register</c> prefixes in scope, its open tags waiting to be closed — and judging it on its
/// own reports errors the runtime can never produce. Deciding "is this file an include target,
/// and whose scope does it run in" therefore has to happen <em>before</em> anything parses, which
/// is why this is a lexer-level scan over the file list rather than a question the parse answers.
/// </para>
/// <para>
/// The directive is read by the same <see cref="Parser.TryParseIncludePath"/> /
/// <see cref="Parser.ResolveIncludePath"/> the real parse uses, so the graph and the inlining can
/// never disagree about what an include points at. Per-file results are cached on the disk stamp;
/// an open editor buffer is always rescanned, because it changes without touching the disk.
/// </para>
/// </remarks>
internal static class AspxIncludeService
{
    private sealed record ScanEntry(DateTime WriteTimeUtc, long Length, ImmutableArray<string> Targets);

    private static readonly ConcurrentDictionary<string, ScanEntry> s_scans =
        new(StringComparer.OrdinalIgnoreCase);

    public static AspxIncludeGraph GetGraph(Project project)
    {
        string? projectDir = Path.GetDirectoryName(project.FilePath);
        return Build(AspxReferenceService.EnumerateFiles(project), projectDir);
    }

    /// <summary>
    /// Builds the graph for an explicit file list — the project-independent core, and the way
    /// tests exercise it. Include targets found along the way are scanned too, whatever their
    /// extension: a fragment can include further fragments, and the closure has to see them.
    /// </summary>
    public static AspxIncludeGraph Build(IEnumerable<string> files, string? rootDirectory)
    {
        var targets = new Dictionary<string, ImmutableArray<string>>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();

        foreach (string file in files)
        {
            if (TryNormalize(file) is { } normalized)
                queue.Enqueue(normalized);
        }

        while (queue.Count > 0)
        {
            string file = queue.Dequeue();
            if (targets.ContainsKey(file))
                continue;

            var direct = ScanTargets(file, rootDirectory);
            targets[file] = direct;

            foreach (string target in direct)
            {
                if (!targets.ContainsKey(target))
                    queue.Enqueue(target);
            }
        }

        return new AspxIncludeGraph(targets);
    }

    private static string? TryNormalize(string path)
    {
        try
        {
            return PathHelper.NormalizePath(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static ImmutableArray<string> ScanTargets(string file, string? rootDirectory)
    {
        if (OpenDocumentStore.TryGet(file, out var open))
            return ScanText(file, open.ToString(), rootDirectory);

        FileInfo info;
        try
        {
            info = new FileInfo(file);
            if (!info.Exists)
                return [];
        }
        catch (Exception)
        {
            return [];
        }

        if (s_scans.TryGetValue(file, out var cached)
            && cached.WriteTimeUtc == info.LastWriteTimeUtc
            && cached.Length == info.Length)
        {
            return cached.Targets;
        }

        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        var result = ScanText(file, text, rootDirectory);
        s_scans[file] = new ScanEntry(info.LastWriteTimeUtc, info.Length, result);
        return result;
    }

    private static ImmutableArray<string> ScanText(string file, string text, string? rootDirectory)
    {
        // The lexer walk below costs more than this containment test, and almost no file has an
        // include directive.
        if (!text.Contains("#include", StringComparison.OrdinalIgnoreCase))
            return [];

        var targets = ImmutableArray.CreateBuilder<string>();
        var lexer = new Lexer(file, text.AsSpan());

        while (lexer.Next() is { } token)
        {
            if (token.Type != TokenType.Comment)
                continue;

            if (!Parser.TryParseIncludePath(token.Text.Value, out string path))
                continue;

            if (Parser.ResolveIncludePath(file, path, rootDirectory) is not { } resolved)
                continue;

            if (!targets.Contains(resolved, StringComparer.OrdinalIgnoreCase))
                targets.Add(resolved);
        }

        return targets.ToImmutable();
    }
}

/// <summary>The include edges of one project's markup, with the two questions diagnostics ask:
/// is this file included by anyone, and from which page-level files does it inherit scope.</summary>
internal sealed class AspxIncludeGraph
{
    /// <summary>File → the files it directly includes.</summary>
    private readonly Dictionary<string, ImmutableArray<string>> _targets;

    /// <summary>File → the files that directly include it.</summary>
    private readonly Dictionary<string, List<string>> _includers;

    public AspxIncludeGraph(Dictionary<string, ImmutableArray<string>> targets)
    {
        _targets = targets;
        _includers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (file, includes) in targets)
        {
            foreach (string target in includes)
            {
                if (!_includers.TryGetValue(target, out var list))
                    _includers[target] = list = [];
                list.Add(file);
            }
        }
    }

    public bool IsIncludeTarget(string path) =>
        _includers.ContainsKey(path);

    /// <summary>
    /// The page-level files whose parse inlines <paramref name="path"/>: walk the includer edges
    /// up until reaching files nobody includes. Those are the parses that carry this file's
    /// diagnostics. Empty when the file is not an include target. Sorted, so callers report in a
    /// stable order. A pure include cycle with no outside includer has no root and resolves to
    /// empty — its members are analyzed standalone, which is the only scope they have.
    /// </summary>
    public ImmutableArray<string> RootIncluders(string path)
    {
        if (!_includers.ContainsKey(path))
            return [];

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { path };
        var roots = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(path);

        while (queue.Count > 0)
        {
            foreach (string includer in DirectIncluders(queue.Dequeue()))
            {
                if (!visited.Add(includer))
                    continue;

                if (_includers.ContainsKey(includer))
                    queue.Enqueue(includer);
                else
                    roots.Add(includer);
            }
        }

        return [.. roots];
    }

    private IEnumerable<string> DirectIncluders(string path) =>
        _includers.TryGetValue(path, out var list) ? list : [];

    /// <summary>
    /// Every file whose content can change what <paramref name="path"/>'s diagnostics say: the
    /// file itself, everything it transitively includes, and — when it is itself included — each
    /// root includer with that includer's own transitive includes. Sorted, so a hash over the
    /// members is stable.
    /// </summary>
    public ImmutableArray<string> Closure(string path)
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        AddWithTargets(path, set);
        foreach (string root in RootIncluders(path))
            AddWithTargets(root, set);

        return [.. set];
    }

    private void AddWithTargets(string path, SortedSet<string> set)
    {
        if (!set.Add(path))
            return;

        if (_targets.TryGetValue(path, out var includes))
        {
            foreach (string target in includes)
                AddWithTargets(target, set);
        }
    }
}
