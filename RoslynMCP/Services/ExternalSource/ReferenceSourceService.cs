using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Config;

namespace RoslynMCP.Services.ExternalSource;

/// <summary>
/// The .NET Framework's published source, for assemblies that have no Source Link at all.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here is verified against a checksum, because there is nothing to verify against: the
/// reference source was published as a readable snapshot, not as the build inputs, and the
/// assemblies carry no hash of it. What can be checked is that the file parses and that it really
/// declares the type asked for, in the right namespace, with the right arity — which is enough to
/// rule out a wrong file, and not enough to prove the right one. That distinction is carried
/// through to the reader in <see cref="ExternalSourceResult.Provenance"/>, and it is why this
/// source is never handed to the debugger.
/// </para>
/// <para>
/// Paths in the repository do not mirror namespaces — <c>System.Net.WebClient</c> lives at
/// <c>System/net/System/Net/_WebClient.cs</c> — so a candidate is guessed by file name, ranked,
/// and then confirmed by reading it. Only a handful of candidates are ever fetched.
/// </para>
/// </remarks>
internal static class ReferenceSourceService
{
    private const long MaxSourceBytes = 16L * 1024 * 1024;

    /// <summary>How many guesses are worth a round trip before decompiling is the better answer.</summary>
    private const int MaxCandidates = 5;

    /// <summary>Extra places the same declaration was found, beyond the one navigated to.</summary>
    private const int MaxSecondaryPositions = 3;

    /// <summary>The published source for a symbol, or null when there is none to be had.</summary>
    public static async Task<ExternalSourceResult?> TryResolveAsync(
        ISymbol? symbol, string reflectionTypeName, string assemblyPath, CancellationToken ct)
    {
        if (!LspFeatureOptions.ExternalSource || !LspFeatureOptions.ReferenceSource)
            return null;

        if (!DecompiledSourceService.IsFrameworkAssembly(assemblyPath))
            return null;

        string? tfm = ReferenceSourceCommitMap.TfmForAssembly(assemblyPath);
        if (ReferenceSourceCommitMap.CommitFor(tfm) is not { } commit)
            return null;

        string directory = Path.GetFileNameWithoutExtension(assemblyPath);
        var index = await GitHubTreeIndex
            .LoadAsync(ReferenceSourceCommitMap.Repository, commit, directory, ct).ConfigureAwait(false);

        if (index is null || index.Paths.Length == 0)
            return null;

        var (simpleName, arity) = SourceMemberLocator.SplitReflectionName(reflectionTypeName);

        // The nested type is what a member lives in, but the file is named after the outermost one.
        string outerName = OutermostName(reflectionTypeName);
        string @namespace = SourceMemberLocator.NamespaceOf(reflectionTypeName);

        var candidates = Rank(index.Paths, outerName, @namespace).Take(MaxCandidates);

        ExternalSourceResult? typeOnly = null;

        foreach (string path in candidates)
        {
            ct.ThrowIfCancellationRequested();

            string? text = await ReadAsync(commit, path, ct).ConfigureAwait(false);
            if (text is null)
                continue;

            var accepted = Verify(text, symbol, simpleName, arity, @namespace, ct);
            if (accepted is null)
                continue;

            var result = new ExternalSourceResult(
                ExternalSourceKind.ReferenceSource,
                assemblyPath,
                CachePath(commit, path),
                accepted.Value.Positions,
                $"{ReferenceSourceCommitMap.Repository}@{commit[..7]}/{path}");

            if (accepted.Value.MemberFound || symbol is null or INamedTypeSymbol)
                return result;

            // The type is here but the member is not — a partial class, most likely. Keep looking
            // for the file that actually declares it, and settle for this one if none does.
            typeOnly ??= result;
        }

        return typeOnly;
    }

    /// <summary>
    /// Orders the repository's files by how likely each is to declare the type.
    /// </summary>
    /// <remarks>
    /// The weights say that the file name matters far more than the directory, which is how this
    /// repository is actually laid out: the directories follow the old product structure while the
    /// file names follow the types. The underscore rules earn their place the same way — a leading
    /// underscore marks an internal implementation file, and that is where a good deal of
    /// <c>System.dll</c> lives.
    /// </remarks>
    internal static IEnumerable<string> Rank(
        IEnumerable<string> paths, string simpleName, string @namespace)
    {
        string[] segments = @namespace.Length == 0
            ? []
            : @namespace.Split('.', StringSplitOptions.RemoveEmptyEntries);

        return paths
            .Select(path => (Path: path, Score: Score(path, simpleName, segments)))
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Path.Length)
            .ThenBy(candidate => candidate.Path, StringComparer.Ordinal)
            .Select(candidate => candidate.Path);
    }

    private static int Score(string path, string simpleName, string[] namespaceSegments)
    {
        string stem = Path.GetFileNameWithoutExtension(path);

        int score = 0;
        if (string.Equals(stem, simpleName, StringComparison.OrdinalIgnoreCase))
            score += 100;
        else if (string.Equals(stem, "_" + simpleName, StringComparison.OrdinalIgnoreCase))
            score += 60;
        else if (stem.Contains('_')
                 && string.Equals(
                     stem.Replace("_", "", StringComparison.Ordinal),
                     simpleName,
                     StringComparison.OrdinalIgnoreCase))
        {
            score += 40;
        }

        if (score == 0)
            return 0;

        if (string.Equals(stem, simpleName, StringComparison.Ordinal)
            || string.Equals(stem.TrimStart('_'), simpleName, StringComparison.Ordinal))
        {
            score += 5;
        }

        string[] directories = path.Split('/', StringSplitOptions.RemoveEmptyEntries)[..^1];
        foreach (string segment in namespaceSegments)
        {
            if (directories.Any(d => string.Equals(d, segment, StringComparison.OrdinalIgnoreCase)))
                score += 10;
        }

        return score;
    }

    /// <summary>
    /// Whether a downloaded file really is the declaration, and where in it to land.
    /// </summary>
    internal static (IReadOnlyList<LinePosition> Positions, bool MemberFound)? Verify(
        string text,
        ISymbol? symbol,
        string simpleName,
        int arity,
        string @namespace,
        CancellationToken ct)
    {
        var tree = CSharpSyntaxTree.ParseText(text, cancellationToken: ct);
        var root = tree.GetRoot(ct);

        // A 404 page, an LFS pointer or a truncated download all fail here, before anything is
        // shown to a reader as though it were source.
        if (tree.GetDiagnostics(ct).Any(d => d.Severity == DiagnosticSeverity.Error))
            return null;

        var declarations = TypeDeclarations(root, simpleName, arity, @namespace).ToList();
        if (declarations.Count == 0)
            return null;

        if (symbol is not null and not INamedTypeSymbol)
        {
            var members = SourceMemberLocator.FindLocations(root, symbol, requireMatchingNamespace: true);
            if (members.Count > 0)
                return (Positions(members.Select(m => m.GetLineSpan().StartLinePosition)), true);
        }

        return (Positions(declarations), false);
    }

    private static IReadOnlyList<LinePosition> Positions(IEnumerable<LinePosition> found) =>
        [.. found.Take(MaxSecondaryPositions + 1)];

    /// <summary>Where a file declares the type, by identifier, arity and enclosing namespace.</summary>
    private static IEnumerable<LinePosition> TypeDeclarations(
        SyntaxNode root, string simpleName, int arity, string @namespace)
    {
        foreach (var node in root.DescendantNodes())
        {
            var (identifier, candidateArity) = node switch
            {
                TypeDeclarationSyntax type when type.Identifier.Text == simpleName =>
                    ((SyntaxToken?)type.Identifier, type.TypeParameterList?.Parameters.Count ?? 0),
                BaseTypeDeclarationSyntax other when other.Identifier.Text == simpleName =>
                    (other.Identifier, 0),
                DelegateDeclarationSyntax del when del.Identifier.Text == simpleName =>
                    (del.Identifier, del.TypeParameterList?.Parameters.Count ?? 0),
                _ => (null, 0),
            };

            if (identifier is not { } token || candidateArity != arity)
                continue;

            if (EnclosingNamespace(node) != @namespace)
                continue;

            yield return token.GetLocation().GetLineSpan().StartLinePosition;
        }
    }

    private static string EnclosingNamespace(SyntaxNode node)
    {
        var parts = new Stack<string>();
        for (var current = node; current is not null; current = current.Parent)
        {
            if (current is BaseNamespaceDeclarationSyntax declaration)
                parts.Push(declaration.Name.ToString());
        }

        return string.Join(".", parts);
    }

    /// <summary>"Ns.Outer`1+Inner" is declared in the file named after <c>Outer</c>.</summary>
    private static string OutermostName(string reflectionTypeName)
    {
        int nested = reflectionTypeName.IndexOf('+');
        string topLevel = nested < 0 ? reflectionTypeName : reflectionTypeName[..nested];

        return SourceMemberLocator.SplitReflectionName(topLevel).SimpleName;
    }

    private static async Task<string?> ReadAsync(string commit, string path, CancellationToken ct)
    {
        string cached = CachePath(commit, path);
        try
        {
            if (File.Exists(cached))
                return await File.ReadAllTextAsync(cached, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable cache entry is simply refetched.
        }

        var uri = new Uri(
            $"https://raw.githubusercontent.com/{ReferenceSourceCommitMap.Repository}/{commit}/{path}");

        byte[]? content = await HttpFetch.GetAsync(uri, MaxSourceBytes, ct).ConfigureAwait(false);
        if (content is null)
            return null;

        // Written before verification so a rejected candidate is not fetched again either; being
        // in the cache says the bytes are what GitHub served, not that they are the right file.
        ExternalSourceCache.WriteReadOnly(cached, content);

        // Read through SourceText so an encoding preamble is honoured rather than decoded.
        return SourceText.From(content, content.Length).ToString();
    }

    private static string CachePath(string commit, string path)
    {
        string[] segments = path.Split('/');

        // Directories are folded hard; the file name keeps its extension, since this path is what
        // an editor is handed to open.
        var parts = segments[..^1]
            .Select(ExternalSourceCache.SanitizePathSegment)
            .Append(ExternalSourceCache.SanitizeFileName(segments[^1]));

        return Path.Combine(
            ExternalSourceCache.ReferenceSourceDirectory, commit, Path.Combine([.. parts]));
    }
}
